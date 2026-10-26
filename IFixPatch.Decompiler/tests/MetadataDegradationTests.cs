using IFixPatch.Core;
using Mono.Cecil;
using System.Reflection;

namespace IFixPatch.Decompiler.Tests;

public sealed class MetadataDegradationTests
{
    [Fact]
    public void AVmStaticWithUnavailableTypeMetadataRemainsAnObjectAnalysisSlot()
    {
        using var f = new DecompilerFixture(); var p = f.Patch();
        p.ExternTypes.Add("Missing.ValueType, MissingMetadata");
        p.StaticFields.Add(new StaticFieldEntry { FieldTypeId = 5, CctorId = -1 });
        f.Method(p, f.Op("StackSpace", 1), f.Op("Ldsfld", -1), f.Op("Ret", 1)); f.Fix(p, 0, "ObjectValue");
        var (report, owner, _) = f.Decompile(p);
        using var generated = AssemblyDefinition.ReadAssembly(new MemoryStream(report.AssemblyImage));
        var field = Assert.Single(generated.MainModule.Types.SelectMany(type => type.Fields));
        Assert.Equal("System.Object", field.FieldType.FullName);
        Assert.Null(owner.GetMethod("ObjectValue__m0")!.Invoke(null, null));
        // This tests the documented analysis fallback, not the unavailable original value type's ABI.
    }

    [Fact]
    public void UnavailableUnobservedClosureFieldMetadataDoesNotPreventIndependentMethodExecution()
    {
        using var f = new DecompilerFixture(); var p = f.VirtualPatch();
        p.ExternTypes.Add("Missing.ValueType, MissingMetadata");
        foreach (var storey in p.AnonStoreys) storey.FieldTypes.Add(6);
        var (report, owner, _) = f.Decompile(p);
        Assert.Equal(2, report.UntouchedStoreyFields); Assert.Equal(0, report.TotalStoreyFields);
        Assert.Equal(11, owner.GetMethod("Value__m0")!.Invoke(null, [1]));
        Assert.Equal(22, owner.GetMethod("Value__m0")!.Invoke(null, [0]));
    }

    [Fact]
    public void AddedFieldWithMissingTypeMetadataAndPunctuationRetainsExplicitNonexecutableSemantics()
    {
        using var f = new DecompilerFixture(); var p = f.Patch();
        p.ExternTypes.Add("Missing.ValueType, MissingMetadata");
        p.Fields.Add(new FieldEntry { IsNew = true, DeclaringTypeId = 0, FieldTypeId = 5, CctorId = -1, Name = "Added_Name.With-Dash" });
        f.Method(p, f.Op("StackSpace", 1), f.Op("Ldsfld"), f.Op("Ret", 1)); f.Fix(p, 0, "ObjectValue");
        var (report, owner, _) = f.Decompile(p);
        Assert.Contains(report.Problems, problem => problem.Contains("Added_Name.With-Dash") && problem.Contains("side-table"));
        var error = Assert.Throws<TargetInvocationException>(() => owner.GetMethod("ObjectValue__m0")!.Invoke(null, null));
        Assert.Contains("Added_Name.With-Dash", Assert.IsType<NotSupportedException>(error.InnerException).Message);
    }

    [Fact]
    public void MissingCoreObjectConstructorIsAnExplicitMetadataFailure()
    {
        using var f = new DecompilerFixture(); File.Delete(Path.Combine(f.DirectoryPath, "mscorlib.dll"));
        using var source = new DummyDllSource(f.DirectoryPath);
        var error = Assert.Throws<InvalidOperationException>(() => IFixPatchDecompiler.PatchDecompiler.Run(f.Patch(), f.Map, source));
        Assert.Contains("object..ctor", error.Message);
    }

    [Fact]
    public void AZeroArgumentStaticVoidFixCanAlsoInitializeAVmStaticField()
    {
        using var f = new DecompilerFixture(); var p = f.Patch();
        var targetPath = Path.Combine(f.DirectoryPath, "SyntheticTarget.dll");
        using (var target = AssemblyDefinition.ReadAssembly(targetPath, new ReaderParameters { InMemory = true }))
        {
            var initializer = new MethodDefinition("Initialize", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, target.MainModule.TypeSystem.Void);
            initializer.Body.Instructions.Add(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ret));
            target.MainModule.GetType("Example.Calculator").Methods.Add(initializer); target.Write(targetPath);
        }
        p.StaticFields.Add(new StaticFieldEntry { FieldTypeId = 1, CctorId = 1 });
        f.Method(p, f.Op("StackSpace", 1), f.Op("Ldsfld", -1), f.Op("Ret", 1)); f.Fix(p, 0, "ReadA");
        f.Method(p, f.Op("StackSpace", 1), f.Op("Ldc_I4", 42), f.Op("Stsfld", -1), f.Op("Ret")); f.Fix(p, 1, "Initialize");
        // CctorId and FixList both name method-table entry 1. Giving that entry another role
        // does not change its static/void/no-argument invocation contract.
        var (report, owner, _) = f.Decompile(p);
        Assert.Empty(report.Problems);
        Assert.Equal(42, owner.GetMethod("ReadA__m0")!.Invoke(null, null));
    }

    [Theory]
    [InlineData("argument")]
    [InlineData("return")]
    [InlineData("instance")]
    public void StaticInitializersWithIncompatibleSignaturesRemainExplicitlyNonexecutable(string incompatibility)
    {
        using var f = new DecompilerFixture(); var p = f.Patch();
        p.StaticFields.Add(new StaticFieldEntry { FieldTypeId = 1, CctorId = 1 });
        f.Method(p, f.Op("StackSpace", 1), f.Op("Ldsfld", -1), f.Op("Ret", 1)); f.Fix(p, 0, "ReadA");
        if (incompatibility == "argument")
            f.Method(p, f.Op("StackSpace", 1), f.Op("Ldarg"), f.Op("Pop"), f.Op("Ret"));
        else if (incompatibility == "return")
            f.Method(p, f.Op("StackSpace", 1), f.Op("Ldc_I4", 42), f.Op("Ret", 1));
        else
        {
            p.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 2, Name = ".ctor" });
            f.Method(p, f.Op("StackSpace", 1), f.Op("Ldarg"), f.Op("CallExtern", 1 << 16), f.Op("Ret"));
            p.AnonStoreys.Add(new AnonStorey { CtorId = 1 });
        }
        var (report, owner, _) = f.Decompile(p);
        Assert.Contains(report.Problems, problem => problem.Contains("initializer method#1") && problem.Contains("unresolved signature"));
        var invocation = Assert.Throws<TargetInvocationException>(() => owner.GetMethod("ReadA__m0")!.Invoke(null, null));
        var initialization = Assert.IsType<TypeInitializationException>(invocation.InnerException);
        Assert.Contains("initializer method#1", Assert.IsType<NotSupportedException>(initialization.InnerException).Message);
    }

    [Theory]
    [InlineData("Ldtype", "TypeValue")]
    [InlineData("Castclass", "ObjectValue")]
    [InlineData("Sizeof", "ReadA")]
    public void RequiredTypeOperandsCannotSilentlyBecomeSystemObject(string operation, string target)
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch();
        patch.ExternTypes.Add("Missing.RequiredType, MissingMetadata");
        var code = new List<Instr> { fixture.Op("StackSpace", 1) };
        if (operation == "Castclass") code.Add(fixture.Op("Ldnull"));
        code.AddRange([fixture.Op(operation, 5), fixture.Op("Ret", 1)]);
        fixture.Method(patch, [.. code]); fixture.Fix(patch, 0, target);
        using var source = new DummyDllSource(fixture.DirectoryPath);
        var report = IFixPatchDecompiler.PatchDecompiler.Run(patch, fixture.Map, source);
        Assert.Contains(report.Problems, problem => problem.Contains("Missing.RequiredType"));
    }

    [Fact]
    public void MissingGetTypeFromHandleHasAnExplicitTypeOperationDiagnostic()
    {
        using var fixture = new DecompilerFixture();
        var core = Path.Combine(fixture.DirectoryPath, "mscorlib.dll");
        using (var metadata = AssemblyDefinition.ReadAssembly(core, new ReaderParameters { InMemory = true }))
        {
            var type = metadata.MainModule.GetType("System.Type");
            foreach (var method in type.Methods.Where(method => method.Name == "GetTypeFromHandle").ToArray()) type.Methods.Remove(method);
            metadata.Write(core);
        }
        var patch = fixture.Patch();
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldtype", 1), fixture.Op("Ret", 1)); fixture.Fix(patch, 0, "TypeValue");
        using var source = new DummyDllSource(fixture.DirectoryPath);
        var report = IFixPatchDecompiler.PatchDecompiler.Run(patch, fixture.Map, source);
        Assert.Contains(report.Problems, problem => problem.Contains("GetTypeFromHandle"));
    }

    [Fact]
    public void UnmappedCustomMnemonicsProduceDiagnosticsRatherThanClaimingTranslation()
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch();
        patch.Methods.Add(new() { Id = 0, Code = [new() { Code = 0, Operand = 1 }, new() { Code = 1 }, new() { Code = 2 }] });
        fixture.Fix(patch, 0, "SetStaticField", 1);
        var map = OpCodeMap.FromPairs([(0, "StackSpace"), (1, "NotAnIFixOpcode"), (2, "Ret")]);
        using var source = new DummyDllSource(fixture.DirectoryPath);
        var report = IFixPatchDecompiler.PatchDecompiler.Run(patch, map, source);
        Assert.Contains(report.Problems, problem => problem.Contains("NotAnIFixOpcode"));
    }
}
