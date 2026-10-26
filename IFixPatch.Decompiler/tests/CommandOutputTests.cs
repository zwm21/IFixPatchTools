using IFixPatch.Core;
using IFixPatchDecompiler;
using System.Text.Json;

namespace IFixPatch.Decompiler.Tests;

public sealed class CommandOutputTests
{
    [Theory]
    [InlineData("unknown", false)]
    [InlineData("unknown", true)]
    [InlineData("many-unknown", false)]
    [InlineData("many-unknown", true)]
    [InlineData("value-conflict", false)]
    [InlineData("value-conflict", true)]
    [InlineData("name-conflict", false)]
    [InlineData("name-conflict", true)]
    public async Task InfoExplainsMappingProblemsAndKeepsJsonMachineReadable(string defect, bool json)
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch();
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldc_I4", 42), fixture.Op("Ret", 1));
        fixture.Fix(patch, 0, "ReadA");
        var input = Path.Combine(fixture.DirectoryPath, "input.bytes");
        var map = Path.Combine(fixture.DirectoryPath, "opcodes.map");
        PatchWriter.WriteFile(input, patch); fixture.SaveMap(map);
        var extra = defect switch
        {
            "unknown" => "MisspelledOpcode=1000\n",
            "many-unknown" => string.Join('\n', Enumerable.Range(0, 13).Select(index => $"UnknownOpcode{index}={1000 + index}")),
            "value-conflict" => "Break=100\n", // StackSpace already owns 100 in this fixture.
            _ => "Nop=1000\n", // The same mnemonic cannot have two numeric identities.
        };
        File.AppendAllText(map, extra);
        var args = new List<string> { "info", input, "--opcode-map", map };
        if (json) args.Add("--json");

        var result = await Invoke(args.ToArray());

        Assert.Equal(0, result.Code); // Info is a diagnostic operation, including for suspect maps.
        var diagnostics = json ? result.Error : result.Output;
        if (defect == "unknown") Assert.Contains("MisspelledOpcode", diagnostics);
        else if (defect == "many-unknown")
        {
            Assert.Contains("未知名 13", diagnostics);
            Assert.Contains("UnknownOpcode0", diagnostics);
            Assert.Contains("…(+1)", diagnostics);
        }
        else
        {
            Assert.Contains("冲突", diagnostics);
            Assert.Contains(defect == "value-conflict" ? "StackSpace" : "Nop", diagnostics);
        }
        if (json)
        {
            using var document = JsonDocument.Parse(result.Output);
            Assert.Equal(1, document.RootElement.GetProperty("counts").GetProperty("methods").GetInt32());
            Assert.DoesNotContain("映射校验", result.Output);
        }
    }

    [Fact]
    public async Task InfoDistinguishesConflictingReturnTypesFromMissingMetadata()
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch();
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldc_I4", 42), fixture.Op("Ret", 1));
        fixture.Fix(patch, 0, "Value", 1);
        patch.ExternMethods.Add(new() { DeclaringTypeId = 0, Name = "Missing" });
        var input = Path.Combine(fixture.DirectoryPath, "input.bytes");
        var dump = Path.Combine(fixture.DirectoryPath, "dump.cs");
        PatchWriter.WriteFile(input, patch);
        File.WriteAllText(dump, """
            // Namespace: Example
            public class Calculator
            {
                // Methods
                public static int Value(int value) { }
                public static string Value(string value) { }
            }
            """);

        var result = await Invoke("info", input, "--dump", dump);

        Assert.Equal(0, result.Code);
        Assert.Contains("待解析 2：确定 0，重载歧义 1，未找到 1", result.Output);
        Assert.Contains("Example.Calculator::Missing/0", result.Output);
        Assert.Contains("转译会按补丁存的参数类型选重载", result.Output);
    }

    [Fact]
    public async Task InfoReportsTheUseCountOfAReferencedString()
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch(); patch.InternStrings.Add("visible");
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldstr"), fixture.Op("Pop"),
            fixture.Op("Ldc_I4", 42), fixture.Op("Ret", 1));
        fixture.Fix(patch, 0, "ReadA");
        var input = Path.Combine(fixture.DirectoryPath, "input.bytes");
        var map = Path.Combine(fixture.DirectoryPath, "opcodes.map");
        PatchWriter.WriteFile(input, patch); fixture.SaveMap(map);
        var result = await Invoke("info", input, "--opcode-map", map);
        Assert.Equal(0, result.Code);
        Assert.Contains("被引用 1 次", result.Output);
        Assert.Contains("visible", result.Output);
    }

    [Fact]
    public async Task ADirectoryRootCannotBePublishedAsADecompiledSourceFile()
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch();
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldc_I4", 42), fixture.Op("Ret", 1));
        fixture.Fix(patch, 0, "ReadA");
        var input = Path.Combine(fixture.DirectoryPath, "input.bytes");
        var map = Path.Combine(fixture.DirectoryPath, "opcodes.map");
        PatchWriter.WriteFile(input, patch); fixture.SaveMap(map);
        var directoryRoot = Path.GetPathRoot(fixture.DirectoryPath)!;
        // This path already denotes a directory. Opening it as a regular output file must fail.
        var result = await Invoke("decompile", input, "--dummy-dll", fixture.DirectoryPath,
            "--opcode-map", map, "--output", directoryRoot);
        Assert.NotEqual(0, result.Code);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.True(Directory.Exists(directoryRoot));
    }

    private static async Task<(int Code, string Output, string Error)> Invoke(params string[] arguments)
    {
        var originalOutput = Console.Out; var originalError = Console.Error;
        using var output = new StringWriter(); using var error = new StringWriter();
        Console.SetOut(output); Console.SetError(error);
        try
        {
            var invocation = typeof(PatchDecompiler).Assembly.EntryPoint!.Invoke(null, [arguments]);
            var code = invocation is Task<int> task ? await task : (int)invocation!;
            return (code, output.ToString(), error.ToString());
        }
        finally { Console.SetOut(originalOutput); Console.SetError(originalError); }
    }
}
