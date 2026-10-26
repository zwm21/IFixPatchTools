using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IFixPatch.SourceBuilder.Core.Tests;

public sealed class ProtocolAndLimitTests
{
    [Fact]
    public void EmptyProcessConfigurationContainsFiveLittleEndianSectionCounts()
    {
        using var fixture = new SyntheticTargetFixture(); var path = Path.Combine(fixture.DirectoryPath, "empty.cfg");
        ProcessConfigWriter.Write(path, [], []);
        Assert.Equal(new byte[20], File.ReadAllBytes(path));
    }

    [Fact]
    public void ProcessConfigurationEncodesPatchHelperFieldPropertyAndClassSectionsInUpstreamOrder()
    {
        using var fixture = new SyntheticTargetFixture();
        using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Protocol", new Version(1, 0)), "Protocol", ModuleKind.Dll);
        var module = assembly.MainModule; var first = SyntheticTargetFixture.Class(module, "Example", "First"); var second = SyntheticTargetFixture.Class(module, "Example", "Second");
        var patch = SyntheticTargetFixture.Method(first, "Replace", true, module.TypeSystem.Int32, module.TypeSystem.Int32);
        var helper = SyntheticTargetFixture.Method(second, "Helper", true, module.TypeSystem.Void);
        helper.Parameters.Add(new ParameterDefinition("result", ParameterAttributes.Out,
            new RequiredModifierType(new TypeReference("System.Runtime.InteropServices", "InAttribute", module, module.TypeSystem.CoreLibrary), new ByReferenceType(module.TypeSystem.Int32))));
        var field = new FieldDefinition("State", FieldAttributes.Public, module.TypeSystem.Int64); first.Fields.Add(field);
        var property = new PropertyDefinition("Value", PropertyAttributes.None, module.TypeSystem.String); second.Properties.Add(property);
        var path = Path.Combine(fixture.DirectoryPath, "process.cfg"); ProcessConfigWriter.Write(path, [patch], [helper], [field], [property], [second]);
        // This decoder follows PatchGenerateConfigure's binary contract, independently of the writer's helpers.
        using var reader = new BinaryReader(File.OpenRead(path));
        Assert.Equal(1, reader.ReadInt32()); Assert.Equal("Example.First", reader.ReadString()); Assert.Equal(1, reader.ReadInt32());
        Assert.Equal("Replace", reader.ReadString()); Assert.Equal("System.Int32", reader.ReadString()); Assert.Equal(1, reader.ReadInt32());
        Assert.False(reader.ReadBoolean()); Assert.Equal("System.Int32", reader.ReadString());
        Assert.Equal(1, reader.ReadInt32()); Assert.Equal("Example.Second", reader.ReadString()); Assert.Equal(1, reader.ReadInt32());
        Assert.Equal("Helper", reader.ReadString()); Assert.Equal("System.Void", reader.ReadString()); Assert.Equal(1, reader.ReadInt32());
        Assert.True(reader.ReadBoolean()); Assert.Equal("System.Int32&", reader.ReadString());
        Assert.Equal(1, reader.ReadInt32()); Assert.Equal("Example.First", reader.ReadString()); Assert.Equal(1, reader.ReadInt32());
        Assert.Equal("State", reader.ReadString()); Assert.Equal("System.Int64", reader.ReadString());
        Assert.Equal(1, reader.ReadInt32()); Assert.Equal("Example.Second", reader.ReadString()); Assert.Equal(1, reader.ReadInt32());
        Assert.Equal("Value", reader.ReadString()); Assert.Equal("System.String", reader.ReadString());
        Assert.Equal(1, reader.ReadInt32()); Assert.Equal("Example.Second", reader.ReadString());
        Assert.Equal(reader.BaseStream.Length, reader.BaseStream.Position);
    }

    [Theory]
    [InlineData(2558, null)]
    [InlineData(2559, "warn")]
    [InlineData(8190, "warn")]
    [InlineData(8191, "fail")]
    public void EvaluationStackThresholdsCountFrameLocalsAndOperandStackWithInclusivePercentages(int locals, string? severity)
    {
        using var fixture = new SyntheticTargetFixture();
        var (grafter, _) = GraftWithLocals(fixture, locals);
        Assert.Equal(10240, grafter.Size.EvaluationStackLimit);
        Assert.Equal(locals + 1, grafter.Size.DeepestChainSlots);
        Assert.Equal(Math.Round((locals + 1) * 100.0 / 10240, 2), grafter.Size.DeepestChainPercent);
        var diagnostic = grafter.Diagnostics.Where(item => item.Rule == "evaluation-stack").ToList();
        if (severity is null) Assert.Empty(diagnostic);
        else Assert.Equal(severity, Assert.Single(diagnostic).Severity);
    }

    [Theory]
    [InlineData(65535, false)]
    [InlineData(65536, true)]
    public void StackSpaceLocalCountMustFitItsUnsignedSixteenBitField(int locals, bool fail)
    {
        using var fixture = new SyntheticTargetFixture();
        var (grafter, _) = GraftWithLocals(fixture, locals);
        Assert.Equal(locals, grafter.Size.MaxLocalCount);
        if (fail) Assert.Contains(grafter.Diagnostics, item => item.Rule == "locals" && item.Severity == "fail");
        else Assert.DoesNotContain(grafter.Diagnostics, item => item.Rule == "locals");
    }

    [Fact]
    public void InstructionAndHelperThresholdsAreWarningsOnlyAndDoNotIncludeUnreachableHelpers()
    {
        using var fixture = new SyntheticTargetFixture();
        var manifest = CompilerPipelineTests.WriteSource(fixture, "namespace Patch { public static class Replacement { public static int Compute()=>Helper(); static int Helper()=>42; static int Unreachable()=>999; } }");
        manifest.Limits.WarnInstructionCount = 1; manifest.Limits.WarnHelperCount = 0;
        var (grafter, _) = CompilerPipelineTests.CompileAndGraft(fixture, fixture.Prepare(), manifest);
        Assert.Equal(1, grafter.Size.HelperCount);
        Assert.Contains(grafter.Diagnostics, item => item.Rule == "helper-count" && item.Severity == "warn");
        Assert.Equal(2, grafter.Diagnostics.Count(item => item.Rule == "instruction-count" && item.Severity == "warn"));
        Assert.DoesNotContain(grafter.Diagnostics, item => item.Severity == "fail");
    }

    private static (BodyGrafter Grafter, string Output) GraftWithLocals(SyntheticTargetFixture fixture, int locals)
    {
        var prepared = fixture.Prepare(); var path = Path.Combine(fixture.DirectoryPath, "implementation.dll");
        using (var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("SyntheticImplementation", new Version(1, 0)), "SyntheticImplementation", ModuleKind.Dll))
        {
            var module = assembly.MainModule; var owner = SyntheticTargetFixture.Class(module, "Patch", "Replacement");
            var method = SyntheticTargetFixture.Method(owner, "Compute", true, module.TypeSystem.Int32);
            method.Body.Instructions.Clear(); method.Body.InitLocals = true;
            for (int index = 0; index < locals; index++) method.Body.Variables.Add(new VariableDefinition(module.TypeSystem.Int32));
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, 42)); method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); assembly.Write(path);
        }
        var manifest = new PatchManifest { Patches = [new PatchBinding { Target = "System.Int32 Example.Calculator::Compute()", Implementation = "System.Int32 Patch.Replacement::Compute()" }] };
        var grafter = new BodyGrafter(); var output = Path.Combine(fixture.DirectoryPath, "grafted.dll");
        grafter.Graft(prepared, manifest, path, output, Path.Combine(fixture.DirectoryPath, "process.cfg")); return (grafter, output);
    }
}
