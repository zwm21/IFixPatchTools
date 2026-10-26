using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Reflection;
using FieldAttributes = Mono.Cecil.FieldAttributes;

namespace IFixPatch.SourceBuilder.Core.Tests;

public sealed class GraftingTests
{
    [Fact]
    public void GraftsIntoTheMetadataTargetAndReportsTheOriginalNativeSlot()
    {
        using var fixture = new SyntheticTargetFixture("Custom.Game");
        var prepared = fixture.Prepare();
        var implementation = WriteImplementation(fixture, state: false);
        var output = Path.Combine(fixture.DirectoryPath, "grafted.dll");
        var grafter = new BodyGrafter();
        var bindings = grafter.Graft(prepared, Manifest(), implementation, output, Path.Combine(fixture.DirectoryPath, "process.cfg"));
        var binding = Assert.Single(bindings);
        Assert.Equal("IFix.IDMAP0", binding.NativeIdMapType);
        Assert.Equal("Example-Calculator-Compute0", binding.NativeIdMapField);
        Assert.Equal(3, binding.NativeSlot);
        using var grafted = AssemblyDefinition.ReadAssembly(output);
        Assert.Equal(prepared.TargetAssemblyIdentity, grafted.Name.FullName);
        var executable = Assembly.Load(File.ReadAllBytes(output));
        Assert.Equal(7, executable.GetType("Example.Calculator")!.GetMethod("Compute")!.Invoke(null, null));
        Assert.False(grafter.HasTypeGraph);
    }

    [Fact]
    public void CarriesOwnedStateTypesAndFieldsWithoutReferencingTheImplementationAssembly()
    {
        using var fixture = new SyntheticTargetFixture();
        var prepared = fixture.Prepare();
        var implementation = WriteImplementation(fixture, state: true);
        var output = Path.Combine(fixture.DirectoryPath, "grafted.dll");
        var grafter = new BodyGrafter();
        grafter.Graft(prepared, Manifest(), implementation, output, Path.Combine(fixture.DirectoryPath, "process.cfg"));
        Assert.True(grafter.HasTypeGraph);
        using var grafted = AssemblyDefinition.ReadAssembly(output);
        Assert.DoesNotContain(grafted.MainModule.AssemblyReferences, reference => reference.Name == "SyntheticImplementation");
        var root = grafted.MainModule.GetType("Example.Calculator").Methods.Single(method => method.Name == "Compute");
        var creation = Assert.Single(root.Body.Instructions, instruction => instruction.OpCode == OpCodes.Newobj);
        var stateName = Assert.IsAssignableFrom<MethodReference>(creation.Operand).DeclaringType.FullName;
        var state = Assert.Single(grafted.MainModule.Types, type => type.FullName == stateName);
        Assert.Equal("System.Int32", Assert.Single(state.Fields).FieldType.FullName);
        Assert.Contains(state.FullName, grafter.NewTypes);
        var executable = Assembly.Load(File.ReadAllBytes(output));
        Assert.Equal(7, executable.GetType("Example.Calculator")!.GetMethod("Compute")!.Invoke(null, null));
    }

    [Fact]
    public void RejectsOwnedValueTypesBeforeWritingAGraftedAssembly()
    {
        using var fixture = new SyntheticTargetFixture();
        var prepared = fixture.Prepare();
        var implementation = WriteImplementation(fixture, state: true, valueType: true);
        var output = Path.Combine(fixture.DirectoryPath, "grafted.dll");
        Assert.Contains("non-generic reference classes", Assert.Throws<NotSupportedException>(() =>
            new BodyGrafter().Graft(prepared, Manifest(), implementation, output, Path.Combine(fixture.DirectoryPath, "process.cfg"))).Message);
        Assert.False(File.Exists(output));
    }

    private static PatchManifest Manifest() => new()
    {
        AssemblyName = "SyntheticImplementation",
        Patches = [new PatchBinding { Target = "System.Int32 Example.Calculator::Compute()", Implementation = "System.Int32 Patch.Replacement::Compute()" }],
    };

    private static string WriteImplementation(SyntheticTargetFixture fixture, bool state, bool valueType = false)
    {
        var path = Path.Combine(fixture.DirectoryPath, "implementation.dll");
        using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("SyntheticImplementation", new Version(1, 0)), "SyntheticImplementation", ModuleKind.Dll);
        var module = assembly.MainModule;
        var owner = SyntheticTargetFixture.Class(module, "Patch", "Replacement");
        var root = SyntheticTargetFixture.Method(owner, "Compute", true, module.TypeSystem.Int32);
        root.Body.Instructions.Clear();
        if (state)
        {
            var type = SyntheticTargetFixture.Class(module, "Patch", "State");
            if (valueType) type.BaseType = new TypeReference("System", "ValueType", module, module.TypeSystem.CoreLibrary);
            var field = new FieldDefinition("Value", FieldAttributes.Public, module.TypeSystem.Int32);
            type.Fields.Add(field);
            var constructor = SyntheticTargetFixture.Method(type, ".ctor", false, module.TypeSystem.Void);
            constructor.Body.Instructions.Clear();
            if (!valueType)
            {
                constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Call, new MethodReference(".ctor", module.TypeSystem.Void, module.TypeSystem.Object) { HasThis = true }));
            }
            constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
            constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, 7));
            constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Stfld, field));
            constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            root.Body.Instructions.Add(Instruction.Create(OpCodes.Newobj, constructor));
            root.Body.Instructions.Add(Instruction.Create(OpCodes.Ldfld, field));
        }
        else root.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, 7));
        root.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        assembly.Write(path);
        return path;
    }
}
