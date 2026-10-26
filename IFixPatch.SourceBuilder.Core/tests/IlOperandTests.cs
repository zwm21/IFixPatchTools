using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IFixPatch.SourceBuilder.Core.Tests;

public sealed class IlOperandTests
{
    [Theory]
    [InlineData("pointer", "System.Int32*")]
    [InlineData("byref", "System.Int32&")]
    [InlineData("array", "System.Int32[]")]
    [InlineData("optional", "System.Int32 modopt(System.Runtime.CompilerServices.IsVolatile)")]
    [InlineData("required", "System.Int32 modreq(System.Runtime.CompilerServices.IsVolatile)")]
    public void PreservesEcma335LocalSignatureShapes(string kind, string expectedType)
    {
        using var fixture = new SyntheticTargetFixture(); var prepared = fixture.Prepare(); var path = Path.Combine(fixture.DirectoryPath, "implementation.dll");
        using (var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("SyntheticImplementation", new Version(1, 0)), "SyntheticImplementation", ModuleKind.Dll))
        {
            var module = assembly.MainModule; var root = SyntheticTargetFixture.Method(SyntheticTargetFixture.Class(module, "Patch", "Replacement"), "Compute", true, module.TypeSystem.Int32);
            var modifier = new TypeReference("System.Runtime.CompilerServices", "IsVolatile", module, module.TypeSystem.CoreLibrary);
            TypeReference local = kind switch
            {
                "pointer" => new PointerType(module.TypeSystem.Int32),
                "byref" => new ByReferenceType(module.TypeSystem.Int32),
                "array" => new ArrayType(module.TypeSystem.Int32),
                "optional" => new OptionalModifierType(modifier, module.TypeSystem.Int32),
                _ => new RequiredModifierType(modifier, module.TypeSystem.Int32)
            };
            // Unused locals are legal ECMA-335 signatures and do not need any unsafe pointer dereference.
            root.Body.InitLocals = true; root.Body.Variables.Add(new VariableDefinition(local)); root.Body.Instructions.Clear();
            root.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, 42)); root.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); assembly.Write(path);
        }
        var output = Path.Combine(fixture.DirectoryPath, "grafted.dll");
        new BodyGrafter().Graft(prepared, Manifest(), path, output, Path.Combine(fixture.DirectoryPath, "process.cfg"));
        using var grafted = AssemblyDefinition.ReadAssembly(output);
        var method = grafted.MainModule.GetType("Example.Calculator").Methods.Single(method => method.Name == "Compute");
        Assert.Equal(expectedType, Assert.Single(method.Body.Variables).VariableType.FullName);
        Assert.Equal(42, Assembly.Load(File.ReadAllBytes(output)).GetType("Example.Calculator")!.GetMethod("Compute")!.Invoke(null, null));
    }

    [Fact]
    public void RejectsIndirectFunctionPointerCallsBeforeWritingOutput()
    {
        using var fixture = new SyntheticTargetFixture(); var prepared = fixture.Prepare(); var path = Path.Combine(fixture.DirectoryPath, "implementation.dll");
        using (var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("SyntheticImplementation", new Version(1, 0)), "SyntheticImplementation", ModuleKind.Dll))
        {
            var module = assembly.MainModule; var type = SyntheticTargetFixture.Class(module, "Patch", "Replacement");
            var helper = SyntheticTargetFixture.Method(type, "Get", true, module.TypeSystem.Int32); helper.Body.Instructions.Clear();
            helper.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, 42)); helper.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            var root = SyntheticTargetFixture.Method(type, "Compute", true, module.TypeSystem.Int32); root.Body.Instructions.Clear();
            root.Body.Instructions.Add(Instruction.Create(OpCodes.Ldftn, helper));
            root.Body.Instructions.Add(Instruction.Create(OpCodes.Calli, new CallSite(module.TypeSystem.Int32)));
            root.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); assembly.Write(path);
        }
        var output = Path.Combine(fixture.DirectoryPath, "grafted.dll");
        Assert.Contains("calli", Assert.Throws<NotSupportedException>(() => new BodyGrafter().Graft(prepared, Manifest(), path, output, Path.Combine(fixture.DirectoryPath, "process.cfg"))).Message);
        Assert.False(File.Exists(output));
    }

    private static PatchManifest Manifest() => new() { Patches = [new PatchBinding { Target = "System.Int32 Example.Calculator::Compute()", Implementation = "System.Int32 Patch.Replacement::Compute()" }] };
}
