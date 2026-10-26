using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using FieldAttributes = Mono.Cecil.FieldAttributes;

namespace IFixPatch.SourceBuilder.Core.Tests;

public sealed class AbiAndReferenceTests
{
    [Fact]
    public void InstanceReplacementMapsExplicitSelfAndLongFormArgumentsBackToTheOriginalAbi()
    {
        using var fixture = new SyntheticTargetFixture();
        fixture.Change(module =>
        {
            var type = module.GetType("Example.Calculator"); var target = type.Methods.Single(method => method.Name == "Compute");
            target.IsStatic = false; target.HasThis = true;
            for (int i = 0; i < 4; i++) target.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
            var constructor = SyntheticTargetFixture.Method(type, ".ctor", false, module.TypeSystem.Void);
            constructor.Body.Instructions.Clear(); constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
            constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Call, new MethodReference(".ctor", module.TypeSystem.Void, module.TypeSystem.Object) { HasThis = true }));
            constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        });
        var manifest = CompilerPipelineTests.WriteSource(fixture, """
            namespace Patch { public static class Replacement {
              public static int Compute(Example.Calculator self, int a,int b,int c,int d) {
                self.Offset += a+b+c+d; return self.Offset;
              }
            } }
            """);
        manifest.Patches[0].Target = "System.Int32 Example.Calculator::Compute(System.Int32,System.Int32,System.Int32,System.Int32)";
        manifest.Patches[0].Implementation = "System.Int32 Patch.Replacement::Compute(Example.Calculator,System.Int32,System.Int32,System.Int32,System.Int32)";
        var (_, output) = CompilerPipelineTests.CompileAndGraft(fixture, fixture.Prepare(), manifest);
        var type = Assembly.Load(File.ReadAllBytes(output)).GetType("Example.Calculator")!;
        // Prepared shells intentionally replace original constructors with stubs.
        var instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
        Assert.Equal(10, type.GetMethod("Compute")!.Invoke(instance, [1, 2, 3, 4]));
        Assert.Equal(20, type.GetMethod("Compute")!.Invoke(instance, [1, 2, 3, 4]));
    }

    [Theory]
    [InlineData("return", "Return type mismatch")]
    [InlineData("count", "Parameter count mismatch")]
    [InlineData("instance", "Implementation must be static")]
    [InlineData("self", "Explicit self mismatch")]
    [InlineData("parameter-type", "Parameter 0 mismatch")]
    [InlineData("parameter-out", "Parameter 0 mismatch")]
    [InlineData("generic", "Generic targets and implementations")]
    [InlineData("no-body", "must both have method bodies")]
    public void RejectsIncompatibleReplacementAbisBeforeWritingOutput(string mismatch, string requirement)
    {
        using var fixture = new SyntheticTargetFixture();
        fixture.Change(module =>
        {
            var target = module.GetType("Example.Calculator").Methods.Single(method => method.Name == "Compute");
            if (mismatch == "self") { target.IsStatic = false; target.HasThis = true; }
            if (mismatch.StartsWith("parameter")) target.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
            if (mismatch == "generic") target.GenericParameters.Add(new GenericParameter("T", target));
            if (mismatch == "no-body")
            {
                target.DeclaringType.IsAbstract = true; target.IsStatic = false; target.HasThis = true;
                target.Attributes |= MethodAttributes.Abstract | MethodAttributes.Virtual; target.Body = null;
            }
        });
        var prepared = fixture.Prepare(); var path = Path.Combine(fixture.DirectoryPath, "implementation.dll");
        string implementationSignature;
        using (var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("SyntheticImplementation", new Version(1, 0)), "SyntheticImplementation", ModuleKind.Dll))
        {
            var module = assembly.MainModule; var type = SyntheticTargetFixture.Class(module, "Patch", "Replacement");
            var root = SyntheticTargetFixture.Method(type, "Compute", mismatch != "instance", mismatch == "return" ? module.TypeSystem.String : module.TypeSystem.Int32);
            if (mismatch == "count" || mismatch == "self" || mismatch == "parameter-type") root.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
            if (mismatch == "parameter-out") root.Parameters.Add(new ParameterDefinition("value", Mono.Cecil.ParameterAttributes.Out, module.TypeSystem.Int32));
            implementationSignature = root.FullName; assembly.Write(path);
        }
        using var targetAssembly = AssemblyDefinition.ReadAssembly(prepared.CleanAssemblyPath);
        var targetSignature = targetAssembly.MainModule.GetType("Example.Calculator").Methods.Single(method => method.Name == "Compute").FullName;
        var manifest = new PatchManifest { Patches = [new PatchBinding { Target = targetSignature, Implementation = implementationSignature }] };
        var output = Path.Combine(fixture.DirectoryPath, "grafted.dll");
        var error = Record.Exception(() => new BodyGrafter().Graft(prepared, manifest, path, output, Path.Combine(fixture.DirectoryPath, "process.cfg")));
        Assert.NotNull(error); Assert.True(error is InvalidDataException or NotSupportedException); Assert.Contains(requirement, error.Message);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void RebindsMembersOnAClosedGenericTargetUsingTheirDefinitionSignatures()
    {
        using var fixture = new SyntheticTargetFixture(); AddGenericTarget(fixture);
        var manifest = CompilerPipelineTests.WriteSource(fixture, """
            namespace Patch { public static class Replacement {
              public static int Compute() {
                var box=(Example.Box<int>)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Example.Box<int>));
                box.Value=42;
                if(box.Value<0) return box.Read(); // DummyDll method bodies are stubs, but this MemberRef must still bind correctly.
                return box.Value;
              }
            } }
            """);
        var (_, output) = CompilerPipelineTests.CompileAndGraft(fixture, fixture.Prepare(), manifest);
        Assert.Equal(42, Assembly.Load(File.ReadAllBytes(output)).GetType("Example.Calculator")!.GetMethod("Compute")!.Invoke(null, null));
        using var assembly = AssemblyDefinition.ReadAssembly(output);
        Assert.DoesNotContain(assembly.MainModule.AssemblyReferences, reference => reference.Name == "SyntheticImplementation");
        var call = assembly.MainModule.GetType("Example.Calculator").Methods.Single(method => method.Name == "Compute").Body.Instructions
            .Select(instruction => instruction.Operand).OfType<MethodReference>().Single(method => method.Name == "Read");
        var declaring = Assert.IsType<GenericInstanceType>(call.DeclaringType);
        Assert.Equal("System.Int32", Assert.Single(declaring.GenericArguments).FullName);
        Assert.Equal("Example.Box`1", call.Resolve().DeclaringType.FullName);
    }

    [Fact]
    public void RejectsNestedGenericFieldTypesThatTheInjectFixSerializerCannotResolve()
    {
        using var fixture = new SyntheticTargetFixture(); AddGenericTarget(fixture);
        var manifest = CompilerPipelineTests.WriteSource(fixture, """
            namespace Patch { public static class Replacement {
              public static int Compute() { var box=new Example.Box<int>(); return box.Items.Length; }
            } }
            """);
        var error = Assert.Throws<NotSupportedException>(() => CompilerPipelineTests.CompileAndGraft(fixture, fixture.Prepare(), manifest));
        Assert.Contains("only resolves a bare type parameter", error.Message);
        Assert.Contains("PatchImpl.cs:", error.Message);
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, "grafted.dll")));
    }

    [Fact]
    public void RejectsCallingAPatchImplementationAsAnInternalHelper()
    {
        using var fixture = new SyntheticTargetFixture();
        var manifest = CompilerPipelineTests.WriteSource(fixture, "namespace Patch { public static class Replacement { public static int Compute()=>Compute(); } }");
        var error = Assert.Throws<NotSupportedException>(() => CompilerPipelineTests.CompileAndGraft(fixture, fixture.Prepare(), manifest));
        Assert.Contains("patch entry point", error.Message);
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, "grafted.dll")));
    }

    [Fact]
    public void RequiresAtLeastOnePatchBinding()
    {
        Assert.Throws<InvalidDataException>(() => new BodyGrafter().Graft(new PreparedProfile(), new PatchManifest(), "missing.dll", "out.dll", "process.cfg"));
    }

    [Fact]
    public void RequiresANativeSlotForEveryReplacementEntry()
    {
        using var fixture = new SyntheticTargetFixture();
        fixture.Change(module =>
        {
            var map = module.GetType("IFix.IDMAP0");
            foreach (var field in map.Fields.Where(field => field.IsLiteral).ToArray()) map.Fields.Remove(field);
        });
        var manifest = CompilerPipelineTests.WriteSource(fixture, "namespace Patch { public static class Replacement { public static int Compute()=>42; } }");
        Assert.Contains("Unable to resolve native IFix IDMAP slot", Assert.Throws<InvalidDataException>(() =>
            CompilerPipelineTests.CompileAndGraft(fixture, fixture.Prepare(), manifest)).Message);
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, "grafted.dll")));
    }

    private static void AddGenericTarget(SyntheticTargetFixture fixture) => fixture.Change(module =>
    {
        var box = SyntheticTargetFixture.Class(module, "Example", "Box`1"); var parameter = new GenericParameter("T", box); box.GenericParameters.Add(parameter);
        var field = new FieldDefinition("Value", FieldAttributes.Public, parameter); box.Fields.Add(field);
        box.Fields.Add(new FieldDefinition("Items", FieldAttributes.Public, new ArrayType(parameter)));
        var constructor = SyntheticTargetFixture.Method(box, ".ctor", false, module.TypeSystem.Void); constructor.Body.Instructions.Clear();
        constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Call, new MethodReference(".ctor", module.TypeSystem.Void, module.TypeSystem.Object) { HasThis = true }));
        constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        var read = SyntheticTargetFixture.Method(box, "Read", false, parameter); read.Body.Instructions.Clear();
        var self = new GenericInstanceType(box); self.GenericArguments.Add(parameter);
        read.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        read.Body.Instructions.Add(Instruction.Create(OpCodes.Ldfld, new FieldReference("Value", parameter, self)));
        read.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
    });
}
