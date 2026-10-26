using System.Reflection;
using IFixPatch.Core;
using IFixPatch.Core.IR;
using Mono.Cecil;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace IFixPatch.Recompiler.Tests;

public sealed class CommandTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("ifix-recompiler-tests-").FullName;

    [Fact]
    public async Task ValidateReportsSuccessForValidDocument()
    {
        var result = await Invoke("validate", SaveDocument());
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ok", result.Output.Trim());
        Assert.Empty(result.Error);
    }

    [Theory]
    [InlineData("{\"methods\":null}")]
    [InlineData("{\"methods\":[],\"methods\":[]}")]
    [InlineData("{\"methods\":[null]}")]
    [InlineData("{\"unexpected\":true}")]
    [InlineData("{")]
    public async Task ValidateRejectsMalformedDocumentWithDiagnostic(string json)
    {
        var path = Path.Combine(_directory, "malformed.json");
        File.WriteAllText(path, json);
        var result = await Invoke("validate", path);
        Assert.NotEqual(0, result.ExitCode);
        Assert.StartsWith("error:", result.Error);
    }

    [Fact]
    public async Task InvalidReferenceDoesNotReplaceOutput()
    {
        var document = Document();
        document.FixList[0].Method.DeclaringTypeId = 20;
        var output = ProtectedOutput();
        var result = await Invoke("repatch", "--ir", SaveDocument(document), "-o", output);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("declaring type", result.Error);
        AssertProtected(output);
    }

    [Theory]
    [InlineData(-7)]
    [InlineData(42)]
    [InlineData(int.MaxValue)]
    public async Task RepatchPreservesTheRequestedIrValueAndBindings(int value)
    {
        var document = Document();
        document.Methods[0].Code[1].Operand = value;
        var output = Path.Combine(_directory, "output.bytes");
        var result = await Invoke("repatch", "--ir", SaveDocument(document), "-o", output);
        Assert.Equal(0, result.ExitCode);
        var rebuilt = PatchReader.ReadFile(output);
        Assert.True(rebuilt.FullyParsed);
        Assert.Equal(0, rebuilt.TrailingBytes);
        Assert.Equal(42UL, rebuilt.Magic);
        Assert.Equal("Value", Assert.Single(rebuilt.FixList).Method.Name);
        Assert.Equal("Example.Counter, Synthetic.Target", Assert.Single(rebuilt.ExternTypes));
        Assert.Equal(20, Assert.Single(rebuilt.Methods).Code[1].Code);
        Assert.Equal(value, rebuilt.Methods[0].Code[1].Operand);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepatchRequiresExactlyOneInput(bool both)
    {
        var output = ProtectedOutput();
        var arguments = new List<string> { "repatch", "-o", output };
        if (both) arguments.AddRange(["--ir", SaveDocument(), "--patch", SavePatch()]);
        var result = await Invoke(arguments.ToArray());
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("exactly one", result.Error);
        AssertProtected(output);
    }

    [Fact]
    public async Task TrailingBytesAreRejectedWithoutReplacingOutput()
    {
        var input = SavePatch();
        using (var stream = new FileStream(input, FileMode.Append)) stream.WriteByte(0xff);
        var output = ProtectedOutput();
        var result = await Invoke("repatch", "--patch", input, "-o", output);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("trailing bytes", result.Error);
        AssertProtected(output);
    }

    [Fact]
    public async Task MissingInterfaceMetadataIsRejectedWithoutReplacingOutput()
    {
        var patch = Document().ToPatchFile(out _);
        patch.AnonStoreys.Add(new AnonStorey
        {
            CtorId = 0,
            HasInterfaces = true,
            Interfaces = [new() { TypeId = 0, MethodIds = [0] }],
        });
        var input = Path.Combine(_directory, "incomplete.bytes");
        PatchWriter.WriteFile(input, patch);
        var output = ProtectedOutput();
        var result = await Invoke("repatch", "--patch", input, "-o", output);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("fully parsed", result.Error);
        AssertProtected(output);
    }

    [Fact]
    public async Task AssembleAppliesEditedMethodBody()
    {
        var il = Path.Combine(_directory, "patch.il");
        File.WriteAllText(il, """
            .opcode_map 3
              10 = StackSpace
              20 = Ldc_I4
              30 = Ret
            .method #0
              StackSpace 1
              Ldc_I4 43
              Ret 1
            """);
        var output = Path.Combine(_directory, "assembled.bytes");
        var result = await Invoke("assemble", il, "--base", SavePatch(), "-o", output);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(43, PatchReader.ReadFile(output).Methods[0].Code[1].Operand);
    }

    [Fact]
    public async Task AssembleRejectsUnknownInstructionWithoutReplacingOutput()
    {
        var patch = Document().ToPatchFile(out var map);
        var il = Path.Combine(_directory, "unknown.il");
        var text = System.Text.RegularExpressions.Regex.Replace(PatchTextAssembly.Disassemble(patch, map),
            @"(?m)^(\s+\d+\s+)Ldc_I4\b", "${1}Ldc_I44");
        File.WriteAllText(il, text);
        var output = ProtectedOutput();
        var result = await Invoke("assemble", il, "--base", SavePatch(), "-o", output);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Unknown instruction", result.Error);
        AssertProtected(output);
    }

    [Fact]
    public async Task InvocationDoesNotKeepPreviousFailureExitCode()
    {
        Assert.NotEqual(0, (await Invoke("validate", Path.Combine(_directory, "missing.json"))).ExitCode);
        Assert.Equal(0, (await Invoke("validate", SaveDocument())).ExitCode);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("enum")]
    [InlineData("empty")]
    public async Task BinaryRepatchPreservesKnownValuesWithOptionalMetadata(string metadata)
    {
        var output = Path.Combine(_directory, "repacked.bytes");
        var arguments = new List<string> { "repatch", "--patch", SavePatch(), "-o", output };
        if (metadata != "none") arguments.AddRange(["--dummy-dll", metadata == "enum" ? WriteMetadata() : Directory.CreateDirectory(Path.Combine(_directory, "empty-metadata")).FullName]);
        var result = await Invoke(arguments.ToArray());
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        Assert.Contains("repatched 1 methods", result.Output);
        var patch = PatchReader.ReadFile(output);
        Assert.Equal(42UL, patch.Magic);
        Assert.Equal(42, Assert.Single(patch.Methods).Code[1].Operand);
        Assert.Equal("Value", Assert.Single(patch.FixList).Method.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AssembleAcceptsMetadataDirectoriesWithAndWithoutAnOpcodeEnum(bool withOpcodeEnum)
    {
        var metadata = withOpcodeEnum ? WriteMetadata() : Directory.CreateDirectory(Path.Combine(_directory, "empty-metadata")).FullName;
        var il = Path.Combine(_directory, "metadata.il");
        File.WriteAllText(il, """
            .opcode_map 3
              10 = StackSpace
              20 = Ldc_I4
              30 = Ret
            .method #0
              StackSpace 1
              Ldc_I4 -19
              Ret 1
            """);
        var output = Path.Combine(_directory, "metadata.bytes");
        var result = await Invoke("assemble", il, "--base", SavePatch(), "--dummy-dll", metadata, "-o", output);
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        Assert.Equal(-19, Assert.Single(PatchReader.ReadFile(output).Methods).Code[1].Operand);
    }

    [Fact]
    public async Task BinaryInterfaceSlotsRequireAndUseTheDeclaredMetadataMethodCount()
    {
        var metadataDirectory = WriteMetadata();
        var patch = Document().ToPatchFile(out _);
        patch.ExternTypes.Add("Example.IValue, Synthetic.Target");
        patch.AnonStoreys.Add(new AnonStorey { CtorId = 0, HasInterfaces = true, Interfaces = [new() { TypeId = 1, MethodIds = [0] }] });
        var input = Path.Combine(_directory, "interfaces.bytes");
        PatchWriter.WriteFile(input, patch);
        var output = Path.Combine(_directory, "interface-output.bytes");
        var result = await Invoke("repatch", "--patch", input, "--dummy-dll", metadataDirectory, "-o", output);
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        using var metadata = new DummyDllSource(metadataDirectory);
        var rebuilt = PatchReader.ReadFile(output, metadata);
        Assert.True(rebuilt.FullyParsed);
        Assert.Equal(0, rebuilt.TrailingBytes);
        var slots = Assert.Single(Assert.Single(rebuilt.AnonStoreys).Interfaces);
        Assert.Equal(1, slots.TypeId);
        Assert.Equal(new[] { 0 }, slots.MethodIds);
        Assert.Equal("Value", Assert.Single(rebuilt.FixList).Method.Name);
    }

    [Fact]
    public async Task IrRepatchAcceptsExplicitMetadataAndPreservesTheIrOpcodeContract()
    {
        var output = Path.Combine(_directory, "ir-with-metadata.bytes");
        var result = await Invoke("repatch", "--ir", SaveDocument(), "--dummy-dll", WriteMetadata(), "-o", output);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(20, Assert.Single(PatchReader.ReadFile(output).Methods).Code[1].Code);
        Assert.Equal(42, PatchReader.ReadFile(output).Methods[0].Code[1].Operand);
    }

    [Theory]
    [InlineData("assemble")]
    [InlineData("repatch")]
    public async Task MissingInputFilesProduceDiagnosticsAndPreserveExistingOutput(string command)
    {
        var missing = Path.Combine(_directory, "missing-input");
        var output = ProtectedOutput();
        var result = command == "assemble"
            ? await Invoke("assemble", missing, "--base", SavePatch(), "-o", output)
            : await Invoke("repatch", "--patch", missing, "-o", output);
        Assert.NotEqual(0, result.ExitCode);
        Assert.StartsWith("error:", result.Error);
        Assert.Contains("missing-input", result.Error);
        AssertProtected(output);
    }

    [Fact]
    public async Task CliParserRejectsMissingRequiredOutputBeforeExecution()
    {
        var result = await Invoke("repatch", "--ir", SaveDocument());
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("--output", result.Error);
    }

    [Theory]
    [InlineData("validate")]
    [InlineData("assemble")]
    [InlineData("repatch")]
    public async Task DirectoryInputsProduceDiagnosticsAndPreserveExistingOutput(string command)
    {
        var output = ProtectedOutput();
        var result = command switch
        {
            "validate" => await Invoke("validate", _directory),
            "assemble" => await Invoke("assemble", _directory, "--base", SavePatch(), "-o", output),
            "repatch" => await Invoke("repatch", "--ir", _directory, "-o", output),
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };

        Assert.NotEqual(0, result.ExitCode);
        Assert.StartsWith("error:", result.Error);
        Assert.Contains(_directory, result.Error);
        AssertProtected(output);
    }

    private string WriteMetadata()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_directory, "metadata")).FullName;
        using var runtime = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("IFix.Core", new Version(1, 0)), "IFix.Core", ModuleKind.Dll);
        var module = runtime.MainModule;
        var code = new TypeDefinition("IFix.Core", "Code", Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Sealed,
            new TypeReference("System", "Enum", module, module.TypeSystem.CoreLibrary));
        module.Types.Add(code);
        code.Fields.Add(new FieldDefinition("value__", Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.SpecialName | Mono.Cecil.FieldAttributes.RTSpecialName, module.TypeSystem.Int32));
        foreach (var (name, value) in new[] { ("StackSpace", 10), ("Ldc_I4", 20), ("Ret", 30) })
            code.Fields.Add(new FieldDefinition(name, Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static | Mono.Cecil.FieldAttributes.Literal | Mono.Cecil.FieldAttributes.HasDefault, code) { Constant = value });
        runtime.Write(Path.Combine(directory, "IFix.Core.dll"));
        using var target = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Synthetic.Target", new Version(1, 0)), "Synthetic.Target", ModuleKind.Dll);
        var itf = new TypeDefinition("Example", "IValue", Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Interface | Mono.Cecil.TypeAttributes.Abstract);
        itf.Methods.Add(new MethodDefinition("Value", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Abstract | Mono.Cecil.MethodAttributes.Virtual | Mono.Cecil.MethodAttributes.NewSlot, target.MainModule.TypeSystem.Int32));
        target.MainModule.Types.Add(itf);
        target.Write(Path.Combine(directory, "Synthetic.Target.dll"));
        return directory;
    }

    [Fact]
    public async Task ValidateRejectsStructurallyValidJsonWithInvalidReferences()
    {
        var document = Document();
        document.FixList[0].Method.DeclaringTypeId = 20;
        var result = await Invoke("validate", SaveDocument(document));
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("declaring type", result.Error);
        Assert.DoesNotContain("ok", result.Output);
    }

    private static PatchDocument Document() => new()
    {
        Magic = 42,
        OpCodeMap = new() { ["StackSpace"] = 10, ["Ldc_I4"] = 20, ["Ret"] = 30 },
        ExternTypes = ["Example.Counter, Synthetic.Target"],
        Methods = [new() { Id = 0, Code = [new() { Op = "StackSpace", Operand = 1 }, new() { Op = "Ldc_I4", Operand = 42 }, new() { Op = "Ret", Operand = 1 }] }],
        FixList = [new() { MethodId = 0, Method = new() { DeclaringTypeId = 0, Name = "Value" } }],
    };

    private string SaveDocument(PatchDocument? document = null)
    {
        var path = Path.Combine(_directory, "patch.ir.json");
        (document ?? Document()).SaveJson(path);
        return path;
    }

    private string SavePatch()
    {
        var path = Path.Combine(_directory, "input.bytes");
        PatchWriter.WriteFile(path, Document().ToPatchFile(out _));
        return path;
    }

    private string ProtectedOutput()
    {
        var path = Path.Combine(_directory, "protected.bytes");
        File.WriteAllText(path, "original");
        return path;
    }

    private void AssertProtected(string path)
    {
        Assert.Equal("original", File.ReadAllText(path));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp.*"));
    }

    private static async Task<(int ExitCode, string Output, string Error)> Invoke(params string[] args)
    {
        var previousOutput = Console.Out;
        var previousError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output); Console.SetError(error);
            var entry = Assembly.Load("IFixPatch.Recompiler").EntryPoint!;
            var result = entry.Invoke(null, [args]);
            var exitCode = result is Task<int> pending ? await pending : (int)result!;
            return (exitCode, output.ToString(), error.ToString());
        }
        finally { Console.SetOut(previousOutput); Console.SetError(previousError); }
    }

    public void Dispose() => Directory.Delete(_directory, true);
}
