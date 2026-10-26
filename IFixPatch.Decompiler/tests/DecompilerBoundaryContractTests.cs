using IFixPatch.Core;
using IFixPatchDecompiler;
using System.Text.Json;

namespace IFixPatch.Decompiler.Tests;

public sealed class DecompilerBoundaryContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InfoReportsWarningsFromLazyOpcodeMetadataLoading(bool json)
    {
        using var fixture = new DecompilerFixture();
        var input = Path.Combine(fixture.DirectoryPath, "input.bytes");
        PatchWriter.WriteFile(input, fixture.Patch());
        var arguments = new List<string> { "info", input, "--dummy-dll", fixture.DirectoryPath };
        if (json) arguments.Add("--json");

        var result = await Invoke([.. arguments]);

        Assert.Equal(0, result.Code);
        Assert.Contains("IFix.Core.dll", json ? result.Error : result.Output);
        Assert.Contains("无法取 opcode 映射", json ? result.Error : result.Output);
        if (json)
        {
            using var document = JsonDocument.Parse(result.Output);
            Assert.True(document.RootElement.GetProperty("fullyParsed").GetBoolean());
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("opcodeMap").ValueKind);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecompileReportsMissingOrUnreadableMetadataLoadedDuringTranslation(bool unreadable)
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch();
        patch.ExternTypes.Add("Example.Unavailable, UnavailableMetadata");
        patch.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 5, Name = "Run" });
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("CallExtern"), fixture.Op("Ldc_I4", 42), fixture.Op("Ret", 1));
        fixture.Fix(patch, 0, "ReadA");
        PatchValidation.RequireValid(patch, fixture.Map);
        if (unreadable) File.WriteAllText(Path.Combine(fixture.DirectoryPath, "UnavailableMetadata.dll"), "This is not a managed assembly.");
        var input = Path.Combine(fixture.DirectoryPath, "input.bytes");
        var map = Path.Combine(fixture.DirectoryPath, "opcodes.map");
        var output = Path.Combine(fixture.DirectoryPath, "output.cs");
        PatchWriter.WriteFile(input, patch);
        fixture.SaveMap(map);

        var result = await Invoke("decompile", input, "--dummy-dll", fixture.DirectoryPath, "--opcode-map", map, "--output", output);

        Assert.Equal(2, result.Code);
        Assert.Contains("UnavailableMetadata.dll", result.Output);
        Assert.Contains(unreadable ? "读取 UnavailableMetadata.dll 失败" : "缺少 UnavailableMetadata.dll", result.Output);
        Assert.Contains("Run", result.Output);
        Assert.True(File.Exists(output));
    }

    [Theory]
    [InlineData("info", false)]
    [InlineData("info", true)]
    [InlineData("decompile", false)]
    [InlineData("decompile", true)]
    public async Task UnwritableOutputLocationsReturnAWriteFailure(string command, bool parentIsFile)
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch();
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldc_I4", 42), fixture.Op("Ret", 1));
        fixture.Fix(patch, 0, "ReadA");
        var input = Path.Combine(fixture.DirectoryPath, "input.bytes");
        var map = Path.Combine(fixture.DirectoryPath, "opcodes.map");
        var location = Path.Combine(fixture.DirectoryPath, "output");
        var output = parentIsFile ? Path.Combine(location, "child.txt") : location;
        PatchWriter.WriteFile(input, patch);
        fixture.SaveMap(map);
        if (parentIsFile) File.WriteAllText(location, "Preserve this file.");
        else Directory.CreateDirectory(location);

        var result = await Invoke(command, input, "--dummy-dll", fixture.DirectoryPath, "--opcode-map", map, "--output", output);

        Assert.Equal(1, result.Code);
        Assert.Contains("error: 写出失败", result.Error);
        if (parentIsFile) Assert.Equal("Preserve this file.", File.ReadAllText(location));
        else
        {
            Assert.True(Directory.Exists(location));
            Assert.Empty(Directory.EnumerateFileSystemEntries(location));
        }
    }

    [Theory]
    [InlineData("missing-input")]
    [InlineData("truncated-input")]
    [InlineData("missing-metadata")]
    [InlineData("missing-map")]
    public async Task InfoInputFailuresPreserveAnExistingOutput(string failure)
    {
        using var fixture = new DecompilerFixture();
        var input = Path.Combine(fixture.DirectoryPath, "input.bytes");
        var output = Path.Combine(fixture.DirectoryPath, "output.txt");
        var arguments = new List<string> { "info", input, "--output", output };
        if (failure != "missing-input") PatchWriter.WriteFile(input, fixture.Patch());
        if (failure == "truncated-input") File.WriteAllBytes(input, [1, 2, 3]);
        if (failure == "missing-metadata") arguments.AddRange(["--dummy-dll", Path.Combine(fixture.DirectoryPath, "absent")]);
        if (failure == "missing-map") arguments.AddRange(["--opcode-map", Path.Combine(fixture.DirectoryPath, "absent.map")]);
        byte[] original = [0, 1, 2, 3, 255];
        File.WriteAllBytes(output, original);

        var result = await Invoke([.. arguments]);

        Assert.NotEqual(0, result.Code);
        Assert.Contains("error:", result.Error);
        Assert.Equal(original, File.ReadAllBytes(output));
    }

    [Theory]
    [InlineData("field-owner", -1)]
    [InlineData("field-owner", 5)]
    [InlineData("field-type", -1)]
    [InlineData("field-type", 5)]
    [InlineData("fix-owner", -1)]
    [InlineData("fix-owner", 5)]
    [InlineData("static-type", -1)]
    [InlineData("static-type", 5)]
    public async Task DecompileRejectsInvalidTypeReferencesBeforeReplacingOutput(string location, int index)
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch();
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldc_I4", 42), fixture.Op("Ret", 1));
        fixture.Fix(patch, 0, "ReadA");
        if (location.StartsWith("field-", StringComparison.Ordinal))
            patch.Fields.Add(new FieldEntry
            {
                IsNew = true,
                DeclaringTypeId = location == "field-owner" ? index : 0,
                FieldTypeId = location == "field-type" ? index : 1,
                CctorId = -1,
                Name = "Added",
            });
        else if (location == "fix-owner") patch.FixList[0].Method.DeclaringTypeId = index;
        else patch.StaticFields.Add(new StaticFieldEntry { FieldTypeId = index, CctorId = -1 });
        var input = Path.Combine(fixture.DirectoryPath, "input.bytes");
        var map = Path.Combine(fixture.DirectoryPath, "opcodes.map");
        var output = Path.Combine(fixture.DirectoryPath, "output.cs");
        PatchWriter.WriteFile(input, patch);
        fixture.SaveMap(map);
        const string original = "Preserve the previous source when input is rejected.";
        File.WriteAllText(output, original);

        var result = await Invoke("decompile", input, "--dummy-dll", fixture.DirectoryPath, "--opcode-map", map, "--output", output);

        Assert.Equal(2, result.Code);
        Assert.Contains("index " + index + " outside", result.Error);
        Assert.Equal(original, File.ReadAllText(output));
    }

    [Fact]
    public void CallerMetadataSourceCanBeReusedAfterDecompilation()
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch();
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldc_I4", 42), fixture.Op("Ret", 1));
        fixture.Fix(patch, 0, "ReadA");
        using var source = new DummyDllSource(fixture.DirectoryPath);

        _ = PatchDecompiler.Run(patch, fixture.Map, source);
        var report = PatchDecompiler.Run(patch, fixture.Map, source);

        Assert.Empty(report.Problems);
        Assert.Empty(report.Balance);
        Assert.NotNull(source.FindMethod("Example.Calculator, SyntheticTarget", "ReadA", 0));
        var assembly = System.Reflection.Assembly.Load(report.AssemblyImage);
        var owner = assembly.GetType("Example.Calculator__Patch", throwOnError: true)!;
        Assert.Equal(42, owner.GetMethod("ReadA__m0")!.Invoke(null, null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingDeclaredFieldTypesAreDiagnosedRatherThanClaimingObjectStorageIsEquivalent(bool anonymous)
    {
        using var fixture = new DecompilerFixture();
        var patch = anonymous ? fixture.VirtualPatch() : fixture.Patch();
        patch.ExternTypes.Add("Missing.ValueType, MissingMetadata");
        if (anonymous)
            foreach (var storey in patch.AnonStoreys) storey.FieldTypes.Add(6);
        else
        {
            patch.StaticFields.Add(new StaticFieldEntry { FieldTypeId = 5, CctorId = -1 });
            fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldsfld", -1), fixture.Op("Ret", 1));
            fixture.Fix(patch, 0, "ObjectValue");
        }
        PatchValidation.RequireValid(patch, fixture.Map);
        using var source = new DummyDllSource(fixture.DirectoryPath);

        var report = PatchDecompiler.Run(patch, fixture.Map, source);

        Assert.Contains(report.Problems, problem => problem.Contains("Missing.ValueType") && problem.Contains("analysis placeholder"));
        Assert.NotEmpty(report.AssemblyImage);
        Assert.NotEmpty(report.CSharp);
        // Retaining an inspectable assembly does not prove the missing value type's storage semantics.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrUnreadableCoreMetadataReturnsAnExplicitFailure(bool unreadable)
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch();
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldc_I4", 42), fixture.Op("Ret", 1));
        fixture.Fix(patch, 0, "ReadA");
        var core = Path.Combine(fixture.DirectoryPath, "mscorlib.dll");
        if (unreadable) File.WriteAllText(core, "Not a managed core library.");
        else File.Delete(core);
        var input = Path.Combine(fixture.DirectoryPath, "input.bytes");
        var map = Path.Combine(fixture.DirectoryPath, "opcodes.map");
        var output = Path.Combine(fixture.DirectoryPath, "output.cs");
        PatchWriter.WriteFile(input, patch);
        fixture.SaveMap(map);

        var result = await Invoke("decompile", input, "--dummy-dll", fixture.DirectoryPath, "--opcode-map", map, "--output", output);

        Assert.Equal(2, result.Code);
        Assert.Contains("object..ctor", result.Error);
        Assert.Contains("mscorlib.dll", result.Error);
        Assert.False(File.Exists(output));
    }

    private static async Task<(int Code, string Output, string Error)> Invoke(params string[] arguments)
    {
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            var invocation = typeof(PatchDecompiler).Assembly.EntryPoint!.Invoke(null, [arguments]);
            var code = invocation is Task<int> task ? await task : (int)invocation!;
            return (code, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }
    }
}
