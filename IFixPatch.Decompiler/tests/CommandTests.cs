using IFixPatch.Core;
using IFixPatch.Core.IR;
using IFixPatchDecompiler;
using System.Reflection;
using System.Text.Json;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace IFixPatch.Decompiler.Tests;

public sealed class CommandTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InfoJsonPreservesHeaderCountsSignaturesAndUnicode(bool outputFile)
    {
        using var f = new CliFixture();
        var arguments = new List<string> { "info", f.PatchPath, "--json", "--opcode-map", f.MapPath, "--dummy-dll", f.DirectoryPath };
        if (outputFile) arguments.AddRange(["-o", f.OutputPath]);
        var result = await Invoke(arguments.ToArray());
        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(outputFile ? File.ReadAllText(f.OutputPath) : result.Output);
        var root = json.RootElement;
        Assert.Equal("0x123456789ABCDEF0", root.GetProperty("magic").GetString());
        Assert.Equal(1, root.GetProperty("counts").GetProperty("methods").GetInt32());
        Assert.Equal(1, root.GetProperty("counts").GetProperty("fixList").GetInt32());
        Assert.True(root.GetProperty("fullyParsed").GetBoolean());
        Assert.Equal(0, root.GetProperty("trailingBytes").GetInt64());
        Assert.Equal("你好\npatch", root.GetProperty("internStrings")[0].GetProperty("value").GetString());
        Assert.Equal("Example.Calculator, SyntheticTarget", root.GetProperty("fixList")[0].GetProperty("decl").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InfoSummaryDescribesInputWithOrWithoutOpcodeMapping(bool mapped)
    {
        using var f = new CliFixture();
        var args = new List<string> { "info", f.PatchPath };
        if (mapped) args.AddRange(["--opcode-map", f.MapPath, "--dummy-dll", f.DirectoryPath]);
        var result = await Invoke(args.ToArray());
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("123456789ABCDEF0", result.Output);
        Assert.Contains("ReadA", result.Output);
        Assert.Contains("你好\\npatch", result.Output);
        Assert.Contains("Example.Added", result.Output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisasmOutputsEditableInstructionsAndOriginalOperands(bool outputFile)
    {
        using var f = new CliFixture();
        var args = new List<string> { "disasm", f.PatchPath, "--opcode-map", f.MapPath };
        if (outputFile) args.AddRange(["--output", f.OutputPath]);
        var result = await Invoke(args.ToArray());
        Assert.Equal(0, result.ExitCode);
        var text = outputFile ? File.ReadAllText(f.OutputPath) : result.Output;
        var patch = PatchTextAssembly.Assemble(text, f.Patch, new OpCodeMap());
        Assert.Equal(0x123456789ABCDEF0UL, patch.Magic);
        Assert.Equal(42, patch.Methods[0].Code[1].Operand);
        Assert.Equal("ReadA", patch.FixList[0].Method.Name);
        Assert.Equal("你好\npatch", patch.InternStrings[0]);
    }

    [Fact]
    public async Task ExportEmbedsOpcodeSemanticsAndPatchMetadataInIr()
    {
        using var f = new CliFixture();
        var result = await Invoke("export", f.PatchPath, "--opcode-map", f.MapPath, "-o", f.OutputPath);
        Assert.Equal(0, result.ExitCode);
        var document = PatchDocument.LoadJson(f.OutputPath);
        Assert.Empty(document.Validate());
        var patch = document.ToPatchFile(out var map);
        Assert.Equal(0x123456789ABCDEF0UL, patch.Magic);
        Assert.Equal("Ldc_I4", map.Name(patch.Methods[0].Code[1].Code));
        Assert.Equal(42, patch.Methods[0].Code[1].Operand);
        Assert.Equal("ReadA", patch.FixList[0].Method.Name);
        Assert.Equal("你好\npatch", patch.InternStrings[0]);
    }

    [Fact]
    public async Task DecompileWritesCsharpWithTheFixedMethodBehavior()
    {
        using var f = new CliFixture();
        var result = await Invoke("decompile", f.PatchPath, "--dummy-dll", f.DirectoryPath, "--opcode-map", f.MapPath, "-o", f.OutputPath);
        Assert.Equal(0, result.ExitCode);
        var text = File.ReadAllText(f.OutputPath);
        Assert.Contains("ReadA", text);
        Assert.Contains("return 42;", text);
        Assert.Empty(result.Error);
    }

    [Theory]
    [InlineData("info")]
    [InlineData("disasm")]
    [InlineData("export")]
    [InlineData("decompile")]
    public async Task MissingInputIsRejectedWithoutProducingASuccessfulArtifact(string command)
    {
        using var f = new CliFixture();
        var args = f.Args(command, Path.Combine(f.DirectoryPath, "missing.bytes"));
        var result = await Invoke(args);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("missing.bytes", result.Error);
        Assert.False(File.Exists(f.OutputPath));
    }

    [Theory]
    [InlineData("info", "missing-directory")]
    [InlineData("disasm", "missing-directory")]
    [InlineData("export", "missing-directory")]
    [InlineData("decompile", "missing-directory")]
    [InlineData("info", "missing-map")]
    [InlineData("disasm", "missing-map")]
    [InlineData("export", "missing-map")]
    [InlineData("decompile", "missing-map")]
    [InlineData("info", "invalid-map")]
    [InlineData("disasm", "invalid-map")]
    [InlineData("export", "invalid-map")]
    [InlineData("decompile", "invalid-map")]
    public async Task MetadataAndOpcodeInputErrorsAreDiagnosed(string command, string problem)
    {
        using var f = new CliFixture();
        var args = f.Args(command, f.PatchPath).ToList();
        if (problem == "missing-directory") args[args.IndexOf("--dummy-dll") + 1] = Path.Combine(f.DirectoryPath, "absent");
        else if (problem == "missing-map") args[args.IndexOf("--opcode-map") + 1] = Path.Combine(f.DirectoryPath, "absent.map");
        else
        {
            var invalid = Path.Combine(f.DirectoryPath, "invalid.cs");
            File.WriteAllText(invalid, "// This is not an IFix opcode enum.");
            args[args.IndexOf("--opcode-map") + 1] = invalid;
        }
        var result = await Invoke(args.ToArray());
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("error:", result.Error);
        Assert.False(File.Exists(f.OutputPath));
    }

    [Theory]
    [InlineData("info")]
    [InlineData("disasm")]
    [InlineData("export")]
    public async Task MutuallyExclusiveMetadataSourcesAreRejected(string command)
    {
        using var f = new CliFixture();
        var dump = Path.Combine(f.DirectoryPath, "dump.cs");
        File.WriteAllText(dump, "");
        var result = await Invoke([.. f.Args(command, f.PatchPath), "--dump", dump]);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("--dummy-dll", result.Error);
        Assert.Contains("--dump", result.Error);
    }

    [Theory]
    [InlineData("info", false)]
    [InlineData("disasm", false)]
    [InlineData("export", false)]
    [InlineData("decompile", false)]
    [InlineData("info", true)]
    [InlineData("disasm", true)]
    [InlineData("export", true)]
    [InlineData("decompile", true)]
    public async Task TruncatedOrNegativeCountBinaryIsRejected(string command, bool negativeCount)
    {
        using var f = new CliFixture();
        // Header is uint64 magic, one-byte empty bridge string, then signed extern-type count.
        byte[] bytes = negativeCount ? [0, 0, 0, 0, 0, 0, 0, 0, 0, 255, 255, 255, 255] : [1, 2, 3];
        File.WriteAllBytes(f.PatchPath, bytes);
        var result = await Invoke(f.Args(command, f.PatchPath));
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("error:", result.Error);
        Assert.False(File.Exists(f.OutputPath));
    }

    [Theory]
    [InlineData("export")]
    [InlineData("decompile")]
    public async Task InvalidInstructionReferenceCannotBeExportedOrDecompiled(string command)
    {
        using var f = new CliFixture();
        f.Patch.Methods[0].Code[1] = f.Fixture.Op("Ldstr", 3); // Only string index 0 exists.
        PatchWriter.WriteFile(f.PatchPath, f.Patch);
        var result = await Invoke(f.Args(command, f.PatchPath));
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("error:", result.Error);
        Assert.False(File.Exists(f.OutputPath));
    }

    [Theory]
    [InlineData("export")]
    [InlineData("disasm")]
    public async Task OutputWriteFailureReturnsFailureAndADiagnostic(string command)
    {
        using var f = new CliFixture();
        Directory.CreateDirectory(f.OutputPath);
        var result = await Invoke(f.Args(command, f.PatchPath));
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("error:", result.Error);
    }

    [Fact]
    public async Task DecompileRequiresANonemptyOpcodeMap()
    {
        using var f = new CliFixture();
        var args = f.Args("decompile", f.PatchPath).ToList();
        var index = args.IndexOf("--opcode-map"); args.RemoveRange(index, 2);
        var result = await Invoke(args.ToArray());
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("opcode", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(f.OutputPath));
    }

    [Theory]
    [InlineData("decompile")]
    [InlineData("export")]
    public async Task RequiredOutputOptionIsEnforcedByArgumentParser(string command)
    {
        using var f = new CliFixture();
        var result = await Invoke(command, f.PatchPath);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("--output", result.Error);
    }


    [Theory]
    [InlineData("info")]
    [InlineData("disasm")]
    [InlineData("export")]
    public async Task DumpMetadataSourceSupportsAnalysisCommands(string command)
    {
        using var f = new CliFixture();
        var dump = Path.Combine(f.DirectoryPath, "dump.cs");
        File.WriteAllText(dump, "// Namespace: Example\npublic class Calculator\n{\n    public static int ReadA() { }\n}\n");
        var args = f.Args(command, f.PatchPath).ToList();
        var index = args.IndexOf("--dummy-dll"); args.RemoveRange(index, 2);
        args.AddRange(["--dump", dump]);
        var result = await Invoke(args.ToArray());
        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("error:", result.Error);
        if (command != "info") Assert.True(File.Exists(f.OutputPath));
    }

    [Theory]
    [InlineData("info")]
    [InlineData("disasm")]
    [InlineData("export")]
    public async Task MissingDumpSourceReportsItsPath(string command)
    {
        using var f = new CliFixture();
        var args = f.Args(command, f.PatchPath).ToList();
        var index = args.IndexOf("--dummy-dll"); args.RemoveRange(index, 2);
        args.AddRange(["--dump", Path.Combine(f.DirectoryPath, "missing.cs")]);
        var result = await Invoke(args.ToArray());
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("missing.cs", result.Error);
    }

    [Fact]
    public async Task DecompileReportsClosuresWithoutLosingTheirReceiverBehavior()
    {
        using var f = new CliFixture();
        var patch = f.Fixture.VirtualPatch(sharedImplementation: true);
        PatchWriter.WriteFile(f.PatchPath, patch);
        var result = await Invoke(f.Args("decompile", f.PatchPath));
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Pointer", File.ReadAllText(f.OutputPath));
        Assert.Contains("Value", File.ReadAllText(f.OutputPath));
    }

    [Fact]
    public async Task DecompileReachableIncompatibleVirtualCandidatesAreAFailure()
    {
        using var f = new CliFixture();
        var patch = f.Fixture.VirtualPatch();
        patch.Methods[4].Code = [f.Fixture.Op("StackSpace", 1), f.Fixture.Op("Ret")];
        PatchWriter.WriteFile(f.PatchPath, patch);
        var result = await Invoke(f.Args("decompile", f.PatchPath));
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("error:", result.Error);
        Assert.False(File.Exists(f.OutputPath));
    }

    [Fact]
    public async Task DecompileExplicitSideTablePlaceholderReturnsFailureAndExplainsTheField()
    {
        using var f = new CliFixture();
        f.Patch.Fields.Add(new FieldEntry { IsNew = true, DeclaringTypeId = 0, Name = "Added", FieldTypeId = 1 });
        f.Patch.Methods[0].Code = [f.Fixture.Op("StackSpace", 1), f.Fixture.Op("Ldsfld"), f.Fixture.Op("Ret", 1)];
        PatchWriter.WriteFile(f.PatchPath, f.Patch);
        var result = await Invoke(f.Args("decompile", f.PatchPath));
        Assert.NotEqual(0, result.ExitCode);
        var csharp = File.ReadAllText(f.OutputPath);
        Assert.Contains("side-table", csharp);
        Assert.Contains("Added", csharp);
        Assert.Contains("NotSupportedException", csharp);
    }

    [Fact]
    public async Task MissingExternalMemberIsReportedAsDegradedOutput()
    {
        using var f = new CliFixture();
        f.Patch.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 0, Name = "MissingExternal" });
        f.Patch.Methods[0].Code.Insert(1, f.Fixture.Op("CallExtern"));
        PatchWriter.WriteFile(f.PatchPath, f.Patch);
        var result = await Invoke(f.Args("decompile", f.PatchPath));
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("MissingExternal", result.Output);
        Assert.True(File.Exists(f.OutputPath));
    }


    [Fact]
    public async Task InfoReportsUnconsumedBytesAsPartOfItsJsonContract()
    {
        using var f = new CliFixture();
        using (var stream = File.Open(f.PatchPath, FileMode.Append)) stream.WriteByte(0x7f);
        var result = await Invoke("info", f.PatchPath, "--json");
        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        Assert.True(json.RootElement.GetProperty("fullyParsed").GetBoolean());
        Assert.Equal(1, json.RootElement.GetProperty("trailingBytes").GetInt64());
    }

    [Fact]
    public async Task DisasmWarnsWhenTheBinaryContainsUnconsumedBytes()
    {
        using var f = new CliFixture();
        using (var stream = File.Open(f.PatchPath, FileMode.Append)) stream.WriteByte(0x7f);
        var result = await Invoke(f.Args("disasm", f.PatchPath));
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("warn:", result.Error);
        Assert.True(File.Exists(f.OutputPath));
    }

    [Theory]
    [InlineData("export")]
    [InlineData("decompile")]
    public async Task TransformationsRejectUnconsumedBinaryBytes(string command)
    {
        using var f = new CliFixture();
        using (var stream = File.Open(f.PatchPath, FileMode.Append)) stream.WriteByte(0x7f);
        var result = await Invoke(f.Args(command, f.PatchPath));
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("error:", result.Error);
        Assert.False(File.Exists(f.OutputPath));
    }


    [Fact]
    public async Task InfoJsonPreservesAnonymousInterfaceSlotsAndFieldTypeEncodings()
    {
        using var f = new CliFixture();
        AddInterfaceStorey(f);
        var result = await Invoke([.. f.Args("info", f.PatchPath), "--json"]);
        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        var root = json.RootElement;
        Assert.True(root.GetProperty("fullyParsed").GetBoolean());
        Assert.Equal(0, root.GetProperty("trailingBytes").GetInt64());
        Assert.Equal(4, root.GetProperty("counts").GetProperty("methods").GetInt32());
        Assert.Equal(1, root.GetProperty("counts").GetProperty("anonStoreys").GetInt32());
        var storey = Assert.Single(root.GetProperty("anonStoreys").EnumerateArray());
        Assert.Equal(new[] { 0, -2, 2 }, storey.GetProperty("fieldTypes").EnumerateArray().Select(value => value.GetInt32()));
        Assert.Equal(1, storey.GetProperty("ctorId").GetInt32());
        Assert.Equal(0, storey.GetProperty("ctorParams").GetInt32());
        var contract = Assert.Single(storey.GetProperty("interfaces").EnumerateArray());
        Assert.Equal("Example.IReader, SyntheticTarget", contract.GetProperty("type").GetString());
        Assert.Equal(new[] { 2, 3 }, contract.GetProperty("methodIds").EnumerateArray().Select(value => value.GetInt32()));
        Assert.Equal(6, storey.GetProperty("vtableSize").GetInt32());
        Assert.Equal(new[] { -1, -1, -1, -1, 2, 3 }, storey.GetProperty("vtable").EnumerateArray().Select(value => value.GetInt32()));
    }

    [Fact]
    public async Task InfoSummaryExplainsAnonymousPrimitiveReferenceValueFieldsAndInterfaceSlots()
    {
        using var f = new CliFixture();
        AddInterfaceStorey(f);
        var result = await Invoke(f.Args("info", f.PatchPath));
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("f0:基元", result.Output);
        Assert.Contains("f1:引用", result.Output);
        Assert.Contains("f2:System.Int32", result.Output);
        Assert.Contains("Example.IReader", result.Output);
        Assert.Contains("method#2", result.Output);
        Assert.Contains("method#3", result.Output);
        Assert.Contains("[4]=method#2", result.Output);
        Assert.Contains("[5]=method#3", result.Output);
    }

    private static void AddInterfaceStorey(CliFixture f)
    {
        f.Patch.ExternTypes.Add("Example.IReader, SyntheticTarget");
        f.Patch.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 2, Name = ".ctor" });
        f.Fixture.Method(f.Patch, f.Fixture.Op("StackSpace", 1), f.Fixture.Op("Ldarg"), f.Fixture.Op("CallExtern", 1 << 16), f.Fixture.Op("Ret"));
        f.Fixture.Method(f.Patch, f.Fixture.Op("StackSpace", 1), f.Fixture.Op("Ldarg", 1), f.Fixture.Op("Ret", 1));
        f.Fixture.Method(f.Patch, f.Fixture.Op("StackSpace"), f.Fixture.Op("Ret"));
        f.Patch.AnonStoreys.Add(new AnonStorey
        {
            CtorId = 1,
            HasInterfaces = true,
            FieldTypes = [0, -2, 2],
            Interfaces = [new() { TypeId = 5, MethodIds = [2, 3] }],
            VTable = [-1, -1, -1, -1, 2, 3],
        });
        PatchValidation.RequireValid(f.Patch, f.Fixture.Map);
        PatchWriter.WriteFile(f.PatchPath, f.Patch);
    }

    private static async Task<(int ExitCode, string Output, string Error)> Invoke(params string[] arguments)
    {
        var originalOutput = Console.Out; var originalError = Console.Error;
        using var output = new StringWriter(); using var error = new StringWriter();
        Console.SetOut(output); Console.SetError(error);
        try
        {
            var entry = typeof(PatchDecompiler).Assembly.EntryPoint!;
            var invocation = entry.Invoke(null, [arguments]);
            var code = invocation is Task<int> task ? await task : (int)invocation!;
            return (code, output.ToString(), error.ToString());
        }
        finally { Console.SetOut(originalOutput); Console.SetError(originalError); }
    }

    private sealed class CliFixture : IDisposable
    {
        public DecompilerFixture Fixture { get; } = new();
        public string DirectoryPath => Fixture.DirectoryPath;
        public string PatchPath => Path.Combine(DirectoryPath, "input.bytes");
        public string MapPath => Path.Combine(DirectoryPath, "opcodes.map");
        public string OutputPath => Path.Combine(DirectoryPath, "output.txt");
        public PatchFile Patch { get; }
        public CliFixture()
        {
            Patch = Fixture.Patch(); Patch.Magic = 0x123456789ABCDEF0;
            Patch.InternStrings.Add("你好\npatch"); Patch.NewClasses.Add("Example.Added, SyntheticTarget");
            Fixture.Method(Patch, Fixture.Op("StackSpace", 1), Fixture.Op("Ldc_I4", 42), Fixture.Op("Ret", 1));
            Fixture.Fix(Patch, 0, "ReadA");
            PatchWriter.WriteFile(PatchPath, Patch); Fixture.SaveMap(MapPath);
        }
        public string[] Args(string command, string patch)
        {
            var args = new List<string> { command, patch, "--dummy-dll", DirectoryPath, "--opcode-map", MapPath };
            if (command is "export" or "decompile" or "disasm") args.AddRange(["--output", OutputPath]);
            return args.ToArray();
        }
        public void Dispose() => Fixture.Dispose();
    }
}
