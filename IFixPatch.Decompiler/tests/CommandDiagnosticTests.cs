using IFixPatch.Core;
using IFixPatchDecompiler;
using System.Text.Json;

namespace IFixPatch.Decompiler.Tests;

public sealed class CommandDiagnosticTests
{
    [Theory]
    [InlineData("info")]
    [InlineData("info-json")]
    [InlineData("disasm")]
    public async Task MissingInterfaceMetadataIsReportedAsAPartialParse(string operation)
    {
        using var f = new DecompilerFixture();
        var p = f.VirtualPatch(); p.ExternTypes.Add("Example.IReader, SyntheticTarget");
        p.AnonStoreys[0].Interfaces.Add(new AnonStorey.ItfSlots { TypeId = 5, MethodIds = [3, 4] });
        var input = Save(f, p); var args = new List<string> { operation == "info-json" ? "info" : operation, input };
        if (operation == "info-json") args.Add("--json");
        var result = await Invoke(args.ToArray());
        Assert.Equal(0, result.Code); // Analysis commands retain useful sections without inventing unknown slot lengths.
        if (operation == "info-json")
        {
            using var json = JsonDocument.Parse(result.Output);
            Assert.False(json.RootElement.GetProperty("fullyParsed").GetBoolean());
            Assert.Contains("元数据", json.RootElement.GetProperty("parseNote").GetString());
        }
        else if (operation == "info")
        {
            Assert.Contains("完整解析到尾       : 否", result.Output);
            Assert.Contains("元数据源", result.Output);
        }
        else
            Assert.Contains("warn: 未完整解析到尾", result.Error);
    }

    [Fact]
    public async Task AnEmptyPatchHasAnEmptyFixListAndADefinedZeroMetadataProbeRate()
    {
        using var f = new DecompilerFixture();
        var result = await Invoke("info", Save(f, f.Patch()), "--dummy-dll", f.DirectoryPath);
        Assert.Equal(0, result.Code);
        Assert.Contains("（空）", result.Output);
        Assert.Contains("待解析 0：确定 0，重载歧义 0，未找到 0", result.Output);
        Assert.DoesNotContain("NaN", result.Output);
        Assert.DoesNotContain("Infinity", result.Output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InfoCanExplainUnmappedOrInvalidMetadataWithoutClaimingItIsExecutable(bool mapped)
    {
        using var f = new DecompilerFixture();
        var p = f.VirtualPatch(); p.InternStrings.Add("unused constant");
        foreach (var storey in p.AnonStoreys) storey.VTable = [-1, -1];
        p.AnonStoreys[0].FieldTypes = [-3]; // Deliberately malformed encoding: info diagnoses, transforms validate.
        p.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = -1, Name = "InvalidNegativeOwner" });
        p.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = p.ExternTypes.Count, Name = "InvalidLargeOwner" });
        for (var index = 0; index < 10; index++) p.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 0, Name = "Missing" + index });
        var args = new List<string> { "info", Save(f, p), "--dummy-dll", f.DirectoryPath };
        if (mapped) { var map = Path.Combine(f.DirectoryPath, "map.txt"); f.SaveMap(map); args.AddRange(["--opcode-map", map]); }
        var result = await Invoke(args.ToArray());
        Assert.Equal(0, result.Code);
        Assert.Contains("无 override", result.Output);
        Assert.Contains("f0:?-3", result.Output);
        Assert.Contains("?::InvalidNegativeOwner", result.Output);
        Assert.Contains("?::InvalidLargeOwner", result.Output);
        Assert.Contains("unused constant", result.Output);
        Assert.Contains(mapped ? "未被任何 Ldstr 引用" : "未提供 opcode 映射", result.Output);
    }

    [Fact]
    public async Task InfoTextReportsTrailingBytesAndTheAlignmentNote()
    {
        using var f = new DecompilerFixture(); var input = Save(f, f.Patch());
        using (var output = File.Open(input, FileMode.Append)) output.WriteByte(0x7F);
        var result = await Invoke("info", input);
        Assert.Equal(0, result.Code);
        Assert.Contains("剩余 1 字节", result.Output);
        Assert.Contains("错位", result.Output);
        Assert.Contains("说明", result.Output);
    }

    [Fact]
    public async Task DecompileReportsStackUnderflowWithInstructionContextAndAFailureExit()
    {
        using var f = new DecompilerFixture(); var p = f.Patch();
        f.Method(p, f.Op("StackSpace"), f.Op("Ret", 1)); f.Fix(p, 0, "ReadA");
        var map = Path.Combine(f.DirectoryPath, "map.txt"); f.SaveMap(map);
        var result = await Invoke("decompile", Save(f, p), "--dummy-dll", f.DirectoryPath, "--opcode-map", map, "--output", Path.Combine(f.DirectoryPath, "out.cs"));
        Assert.NotEqual(0, result.Code);
        Assert.Contains("栈平衡自查", result.Output);
        Assert.Contains("前 3 处失衡上下文", result.Output);
        Assert.Contains("IL_", result.Output);
        Assert.Contains("ret", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DecompileDistinguishesUnobservedClosureFieldsFromFailedTypeInference()
    {
        using var f = new DecompilerFixture(); var p = f.VirtualPatch(sharedImplementation: true);
        foreach (var storey in p.AnonStoreys) storey.FieldTypes.Add(0);
        var map = Path.Combine(f.DirectoryPath, "map.txt"); f.SaveMap(map);
        var result = await Invoke("decompile", Save(f, p), "--dummy-dll", f.DirectoryPath, "--opcode-map", map, "--output", Path.Combine(f.DirectoryPath, "out.cs"));
        Assert.Equal(0, result.Code);
        Assert.Contains("字段定型 2/2", result.Output);
        Assert.Contains("另有 2 个字段无读写现场", result.Output);
    }

    [Theory]
    [InlineData("UnknownUnusedOpcode=1000")]
    [InlineData("Break=-1")]
    public async Task ExportRejectsInvalidIrOpcodeMetadataEvenIfNoInstructionUsesIt(string mapEntry)
    {
        using var f = new DecompilerFixture(); var p = f.Patch();
        f.Method(p, f.Op("StackSpace", 1), f.Op("Ldc_I4", 42), f.Op("Ret", 1)); f.Fix(p, 0, "ReadA");
        var map = Path.Combine(f.DirectoryPath, "map.txt"); f.SaveMap(map); File.AppendAllText(map, mapEntry + "\n");
        var output = Path.Combine(f.DirectoryPath, "out.json");
        var result = await Invoke("export", Save(f, p), "--opcode-map", map, "--output", output);
        Assert.NotEqual(0, result.Code);
        Assert.Contains("Invalid or ambiguous opcode map", result.Error);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task DecompileExplainsAnIncompatibleStaticInitializerWithoutAMethodLocationPrefix()
    {
        using var f = new DecompilerFixture(); var p = f.Patch();
        p.StaticFields.Add(new StaticFieldEntry { FieldTypeId = 1, CctorId = 1 });
        f.Method(p, f.Op("StackSpace", 1), f.Op("Ldsfld", -1), f.Op("Ret", 1)); f.Fix(p, 0, "ReadA");
        f.Method(p, f.Op("StackSpace", 1), f.Op("Ldarg"), f.Op("Pop"), f.Op("Ret"));
        var map = Path.Combine(f.DirectoryPath, "map.txt"); f.SaveMap(map);
        var result = await Invoke("decompile", Save(f, p), "--dummy-dll", f.DirectoryPath, "--opcode-map", map, "--output", Path.Combine(f.DirectoryPath, "out.cs"));
        Assert.NotEqual(0, result.Code);
        Assert.Contains("降级点分布", result.Output);
        Assert.Contains("VM static initializer method#1 has an unresolved signature", result.Output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InfoReportsACompleteOpcodeBijectionInTextOrJson(bool json)
    {
        using var f = new DecompilerFixture();
        // The public validator exposes the supported opcode taxonomy. Use it to construct the
        // collaborator input; expected CLI status and documented count remain independent constants.
        var names = new OpCodeMap().Validate().Missing;
        var map = Path.Combine(f.DirectoryPath, "complete.map");
        File.WriteAllLines(map, names.Select((name, index) => $"{name}={index}"));
        var args = new List<string> { "info", Save(f, f.Patch()), "--opcode-map", map };
        if (json) args.Add("--json");
        var result = await Invoke(args.ToArray());
        Assert.Equal(0, result.Code);
        if (json)
        {
            using var document = JsonDocument.Parse(result.Output);
            Assert.Equal("complete", document.RootElement.GetProperty("opcodeMap").GetProperty("validation").GetString());
            Assert.Equal(181, document.RootElement.GetProperty("opcodeMap").GetProperty("count").GetInt32());
            Assert.Contains("完整双射", result.Error);
        }
        else
        {
            Assert.Contains("规范名 181 / 已映射 181", result.Output);
            Assert.Contains("完整双射", result.Output);
            Assert.DoesNotContain("未知名", result.Output);
        }
    }

    private static string Save(DecompilerFixture f, PatchFile p)
    {
        var path = Path.Combine(f.DirectoryPath, "input.bytes"); PatchWriter.WriteFile(path, p); return path;
    }

    private static async Task<(int Code, string Output, string Error)> Invoke(params string[] arguments)
    {
        var originalOutput = Console.Out; var originalError = Console.Error;
        using var output = new StringWriter(); using var error = new StringWriter(); Console.SetOut(output); Console.SetError(error);
        try
        {
            var invocation = typeof(PatchDecompiler).Assembly.EntryPoint!.Invoke(null, [arguments]);
            var code = invocation is Task<int> task ? await task : (int)invocation!;
            return (code, output.ToString(), error.ToString());
        }
        finally { Console.SetOut(originalOutput); Console.SetError(originalError); }
    }
}
