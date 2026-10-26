using IFixPatch.Core;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Runtime.Loader;

namespace IFixPatch.Decompiler.Tests;

public sealed class TypeInferenceExecutionTests
{
    [Fact]
    public void TwoArgumentNondelegateConstructorDoesNotTurnALiteralIntoAMethodPointer()
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch();
        patch.ExternTypes.Add("System.Tuple`2[[System.Int32, mscorlib],[System.Int32, mscorlib]], mscorlib");
        patch.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 5, Name = ".ctor", Params = [new() { TypeId = 1 }, new() { TypeId = 1 }] });
        patch.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 5, Name = "get_Item1" });
        patch.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 5, Name = "get_Item2" });
        // The second constructor argument equals existing internal method id 1. It is ordinary
        // tuple data, whereas the later Call really does invoke that method with the tuple sum.
        fixture.Method(patch, fixture.Op("StackSpace", (1 << 16) | 3), fixture.Op("Ldc_I4", 42), fixture.Op("Ldc_I4", 1),
            fixture.Op("Newobj", 2 << 16), fixture.Op("Stloc"), fixture.Op("Ldloc"), fixture.Op("CallExtern", (1 << 16) | 1),
            fixture.Op("Ldloc"), fixture.Op("CallExtern", (1 << 16) | 2), fixture.Op("Add"), fixture.Op("Call", (1 << 16) | 1), fixture.Op("Ret", 1));
        fixture.Method(patch, fixture.Op("StackSpace", 2), fixture.Op("Ldarg"), fixture.Op("Ldc_I4", 1), fixture.Op("Add"), fixture.Op("Ret", 1));
        fixture.Fix(patch, 0, "ReadA");
        var (report, owner, _) = fixture.Decompile(patch);
        Assert.Empty(report.Problems);
        Assert.Equal(44, owner.GetMethod("ReadA__m0")!.Invoke(null, null));
    }

    [Theory]
    [InlineData(false, 42)]
    [InlineData(true, 7)]
    public void ReassigningOneLocalFromByteAndI4ValuesPreservesTheLastValue(bool literalFirst, int expected)
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch();
        patch.ExternTypes.Add("System.Convert, mscorlib");
        patch.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 5, Name = "ToByte", Params = [new() { TypeId = 1 }] });
        var literal = new[] { fixture.Op("Ldc_I4", 42), fixture.Op("Stloc") };
        var narrow = new[] { fixture.Op("Ldc_I4", 7), fixture.Op("CallExtern", 1 << 16), fixture.Op("Stloc") };
        var code = new List<Instr> { fixture.Op("StackSpace", (1 << 16) | 1) };
        code.AddRange(literalFirst ? literal : narrow); code.AddRange(literalFirst ? narrow : literal);
        code.AddRange([fixture.Op("Ldloc"), fixture.Op("Ret", 1)]);
        fixture.Method(patch, [.. code]); fixture.Fix(patch, 0, "ReadA");
        var (report, owner, _) = fixture.Decompile(patch);
        Assert.Empty(report.Problems);
        Assert.Equal(expected, owner.GetMethod("ReadA__m0")!.Invoke(null, null));
    }

    [Theory]
    [InlineData(false, 2.5)]
    [InlineData(true, 1.25)]
    public void ReassigningOneLocalFromSingleAndDoublePreservesExactRepresentableValues(bool doubleFirst, double expected)
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch();
        var bits = BitConverter.DoubleToInt64Bits(2.5);
        var wide = new[] { fixture.Op("Ldc_R8"), new Instr { Code = unchecked((int)bits), Operand = (int)(bits >> 32) }, fixture.Op("Stloc") };
        var narrow = new[] { fixture.Op("Ldc_R4", BitConverter.SingleToInt32Bits(1.25f)), fixture.Op("Stloc") };
        var code = new List<Instr> { fixture.Op("StackSpace", (1 << 16) | 1) };
        code.AddRange(doubleFirst ? wide : narrow); code.AddRange(doubleFirst ? narrow : wide);
        code.AddRange([fixture.Op("Ldloc"), fixture.Op("Ret", 1)]);
        fixture.Method(patch, [.. code]); fixture.Fix(patch, 0, "DoubleValue");
        var (report, owner, _) = fixture.Decompile(patch);
        Assert.Empty(report.Problems);
        Assert.Equal(expected, owner.GetMethod("DoubleValue__m0")!.Invoke(null, null));
    }

    [Fact]
    public void OutObjectParameterInitializesAnUnassignedReferenceLocal()
    {
        using var fixture = new DecompilerFixture();
        var library = WriteRuntimeLibrary(fixture);
        var patch = fixture.Patch();
        patch.ExternTypes.AddRange([$"Example.Capture, {library}", "System.Object&, mscorlib"]);
        patch.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 5, Name = "Produce", Params = [new() { TypeId = 6 }] });
        fixture.Method(patch, fixture.Op("StackSpace", (1 << 16) | 1), fixture.Op("Ldloca"), fixture.Op("CallExtern", 1 << 16), fixture.Op("Ldloc"), fixture.Op("Ret", 1));
        fixture.Fix(patch, 0, "ObjectValue");
        var (report, owner, _) = fixture.Decompile(patch);
        Assert.Empty(report.Problems);
        Assert.Equal("produced reference", owner.GetMethod("ObjectValue__m0")!.Invoke(null, null));
    }

    [Theory]
    [InlineData(0, 11)]
    [InlineData(1, 22)]
    public void VmStaticStorageWidensWhenDistinctAnonymousTypesShareOneObjectSlot(int lastReceiver, int expected)
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.VirtualPatch(sharedImplementation: true);
        patch.StaticFields.Add(new StaticFieldEntry { FieldTypeId = 2, CctorId = -1 });
        var code = new List<Instr> { fixture.Op("StackSpace", 1) };
        foreach (var receiver in new[] { 1 - lastReceiver, lastReceiver, 1 - lastReceiver, lastReceiver })
            code.AddRange([fixture.Op("Newanon", receiver), fixture.Op("Stsfld", -1)]);
        code.AddRange([fixture.Op("Ldsfld", -1), fixture.Op("Castclass", ~lastReceiver), fixture.Op("Ldfld", -1), fixture.Op("Ret", 1)]);
        patch.Methods[0].Code = code;
        patch.FixList.Clear(); fixture.Fix(patch, 0, "ReadA");
        var (report, owner, _) = fixture.Decompile(patch);
        Assert.Empty(report.Problems);
        Assert.Equal(expected, owner.GetMethod("ReadA__m0")!.Invoke(null, null));
        using var stream = new MemoryStream(report.AssemblyImage);
        using var assembly = AssemblyDefinition.ReadAssembly(stream);
        // The generated receiver classes have no common declared interface or base other than
        // Object. One storage slot accepting both must not have either concrete receiver type.
        var storage = Assert.Single(assembly.MainModule.Types.SelectMany(type => type.Fields), field => field.IsStatic);
        Assert.Equal("System.Object", storage.FieldType.FullName);
    }

    [Fact]
    public void DelegateCapturingAnOriginalObjectKeepsEachInstancesRealField()
    {
        using var fixture = new DecompilerFixture();
        var library = WriteRuntimeLibrary(fixture);
        var patch = fixture.Patch();
        patch.ExternTypes.Add($"Example.Capture, {library}");
        patch.Fields.Add(new FieldEntry { DeclaringTypeId = 5, Name = "Number" });
        patch.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 5, Name = ".ctor", Params = [new() { TypeId = 1 }] });
        patch.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 3, Name = ".ctor", Params = [new() { TypeId = 2 }, new() { TypeId = 4 }] });
        fixture.Method(patch, fixture.Op("StackSpace", 2), fixture.Op("Ldarg"), fixture.Op("Newobj", 1 << 16),
            fixture.Op("Ldc_I4", 1), fixture.Op("Newobj", (2 << 16) | 1), fixture.Op("Ret", 1));
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldarg"), fixture.Op("Ldfld"), fixture.Op("Ret", 1));
        fixture.Fix(patch, 0, "Pointer", 1);
        var (report, owner, _) = fixture.Decompile(patch);
        Assert.Empty(report.Problems);
        var create = owner.GetMethod("Pointer__m0")!;
        var first = Assert.IsType<Func<int>>(create.Invoke(null, [42]));
        var second = Assert.IsType<Func<int>>(create.Invoke(null, [7]));
        Assert.Equal(42, first());
        Assert.Equal(7, second());
        Assert.Equal(42, first());
    }

    private static string WriteRuntimeLibrary(DecompilerFixture fixture)
    {
        var name = "InferenceRuntime" + Guid.NewGuid().ToString("N");
        var path = Path.Combine(fixture.DirectoryPath, name + ".dll");
        using (var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name, new Version(1, 0)), name, ModuleKind.Dll))
        {
            var module = assembly.MainModule;
            var type = new TypeDefinition("Example", "Capture", TypeAttributes.Public, module.TypeSystem.Object);
            module.Types.Add(type);
            var number = new FieldDefinition("Number", FieldAttributes.Public, module.TypeSystem.Int32); type.Fields.Add(number);
            var constructor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, module.TypeSystem.Void);
            constructor.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32)); type.Methods.Add(constructor);
            var objectConstructor = new MethodReference(".ctor", module.TypeSystem.Void, module.TypeSystem.Object) { HasThis = true };
            var il = constructor.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, objectConstructor); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Stfld, number); il.Emit(OpCodes.Ret);
            var produce = new MethodDefinition("Produce", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
            produce.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.Out, new ByReferenceType(module.TypeSystem.Object))); type.Methods.Add(produce);
            il = produce.Body.GetILProcessor(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldstr, "produced reference"); il.Emit(OpCodes.Stind_Ref); il.Emit(OpCodes.Ret);
            assembly.Write(path);
        }
        AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        return name;
    }
}
