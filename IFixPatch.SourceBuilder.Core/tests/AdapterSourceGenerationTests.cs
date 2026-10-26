using Mono.Cecil;

namespace IFixPatch.SourceBuilder.Core.Tests;

public sealed class AdapterSourceGenerationTests
{
    [Theory]
    [InlineData("missing-magic", "no constant")]
    [InlineData("null-magic", "no constant")]
    [InlineData("missing-opcode", "181")]
    [InlineData("extra-opcode", "181")]
    [InlineData("duplicate-opcode-value", "not unique")]
    public void RejectsRuntimeMetadataThatCannotDefineAnUnambiguousInstructionFormat(string change, string diagnostic)
    {
        using var fixture = new SyntheticTargetFixture();
        using (var runtime = AssemblyDefinition.ReadAssembly(fixture.RuntimePath, new ReaderParameters { InMemory = true }))
        {
            var module = runtime.MainModule;
            var magic = module.GetType("IFix.Core.Instruction").Fields.Single();
            var code = module.GetType("IFix.Core.Code");
            var opcodes = code.Fields.Where(field => field.IsLiteral).ToArray();
            switch (change)
            {
                case "missing-magic": magic.HasConstant = false; magic.Attributes &= ~FieldAttributes.HasDefault; break;
                case "null-magic": magic.Constant = null; break;
                case "missing-opcode": code.Fields.Remove(opcodes[^1]); break;
                case "extra-opcode": code.Fields.Add(new FieldDefinition("UnknownExtension", FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault, code) { Constant = 181 }); break;
                case "duplicate-opcode-value": opcodes[^1].Constant = opcodes[0].Constant; break;
            }
            runtime.Write(fixture.RuntimePath);
        }
        var output = Path.Combine(fixture.DirectoryPath, "adapter", "Instruction.g.cs");
        var report = new PrepareReport();
        var error = Assert.Throws<InvalidDataException>(() => new AdapterCoreSourceGenerator().Generate(
            fixture.RuntimePath, [fixture.DirectoryPath], output, report));
        Assert.Contains(diagnostic, error.Message);
        Assert.False(File.Exists(output));
        Assert.Equal(0, report.AdapterOpcodeCount);
    }

    [Fact]
    public void UsesNonsequentialRuntimeOpcodeValuesRatherThanSubstitutingTheCanonicalOrder()
    {
        using var fixture = new SyntheticTargetFixture();
        using (var runtime = AssemblyDefinition.ReadAssembly(fixture.RuntimePath, new ReaderParameters { InMemory = true }))
        {
            foreach (var field in runtime.MainModule.GetType("IFix.Core.Code").Fields.Where(field => field.IsLiteral))
                field.Constant = 1000 + (int)field.Constant;
            runtime.MainModule.GetType("IFix.Core.Instruction").Fields.Single().Constant = ulong.MaxValue;
            runtime.Write(fixture.RuntimePath);
        }
        var output = Path.Combine(fixture.DirectoryPath, "Instruction.g.cs");
        var report = new PrepareReport();
        new AdapterCoreSourceGenerator().Generate(fixture.RuntimePath, [fixture.DirectoryPath], output, report);
        var source = File.ReadAllText(output);
        // The adapter has to preserve the runtime ABI; canonical opcode numbers are not authoritative.
        Assert.Contains("INSTRUCTION_FORMAT_MAGIC = 18446744073709551615UL;", source);
        using var runtimeRead = AssemblyDefinition.ReadAssembly(fixture.RuntimePath);
        foreach (var field in runtimeRead.MainModule.GetType("IFix.Core.Code").Fields.Where(field => field.IsLiteral))
            Assert.Contains($"{field.Name} = {field.Constant},", source);
        Assert.Equal(181, report.AdapterOpcodeCount);
        Assert.Equal(ulong.MaxValue, report.AdapterMagic);
    }
}
