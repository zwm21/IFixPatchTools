using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IFixPatch.SourceBuilder.Core.Tests;

public sealed class MemberAssemblyIdentityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TargetDefinedSignatureTypesMatchReferencesToTheSameAssembly(bool field)
    {
        using var fixture = new SyntheticTargetFixture();
        fixture.Change(module =>
        {
            var native = SyntheticTargetFixture.Class(module, "Example", "Native");
            var calculator = module.GetType("Example.Calculator");
            if (field)
                native.Fields.Add(new FieldDefinition("Owner", Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static, calculator));
            else
                SyntheticTargetFixture.Method(native, "Read", true, module.TypeSystem.Int32, calculator);
        });
        var body = field ? "return Example.Native.Owner == null ? 42 : 0;" : "return Example.Native.Read(null);";
        var manifest = CompilerPipelineTests.WriteSource(fixture,
            "namespace Patch { public static class Replacement {public static int Compute() {" + body + "}}}");
        var (_, output) = CompilerPipelineTests.CompileAndGraft(fixture, CompiledGraftingTests.Prepare(fixture), manifest);
        if (!field)
        {
            // Supply only the synthetic native API body removed by preparation. The emitted
            // replacement, call operand and signature comparison result remain unchanged.
            using var runtime = AssemblyDefinition.ReadAssembly(output, new ReaderParameters { InMemory = true });
            var read = runtime.MainModule.GetType("Example.Native").Methods.Single();
            read.Body.Instructions.Clear(); read.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, 42));
            read.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); runtime.Write(output);
        }
        Assert.Equal(42, Assembly.Load(File.ReadAllBytes(output)).GetType("Example.Calculator")!.GetMethod("Compute")!.Invoke(null, null));
        using var result = AssemblyDefinition.ReadAssembly(output);
        var type = result.MainModule.GetType("Example.Native");
        var signatureType = field ? type.Fields.Single().FieldType : type.Methods.Single().Parameters.Single().ParameterType;
        Assert.Equal("Example.Calculator", signatureType.FullName);
        Assert.Equal(result.Name.FullName, Assert.IsType<ModuleDefinition>(signatureType.Scope).Assembly.Name.FullName);
    }
}
