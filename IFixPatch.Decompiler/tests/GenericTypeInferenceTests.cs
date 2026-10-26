using IFixPatch.Core;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Runtime.Loader;

namespace IFixPatch.Decompiler.Tests;

public sealed class GenericTypeInferenceTests
{
    [Fact]
    public void MethodTypeArgumentIsNotConfusedWithTheEnclosingClassTypeArgument()
    {
        using var fixture = new DecompilerFixture();
        var library = WriteGenericLibrary(fixture);
        var patch = fixture.Patch();
        patch.ExternTypes.AddRange([$"Example.Box`1[[System.String, mscorlib]], {library}", "System.String, mscorlib"]);
        patch.InternStrings.Add("class argument is a string");
        patch.ExternMethods.Add(GenericMethod("Echo"));
        fixture.Method(patch, fixture.Op("StackSpace", (1 << 16) | 2), fixture.Op("Ldstr"), fixture.Op("Ldc_I4", 42),
            fixture.Op("CallExtern", 2 << 16), fixture.Op("Stloc"), fixture.Op("Ldloc"), fixture.Op("Ret", 1));
        fixture.Fix(patch, 0, "ReadA");
        var (report, owner, _) = fixture.Decompile(patch);
        Assert.Empty(report.Problems);
        Assert.Equal(42, owner.GetMethod("ReadA__m0")!.Invoke(null, null));
        AssertClosedLocals(report.AssemblyImage);
    }

    [Fact]
    public void MethodGenericArrayReturnIsClosedBeforeItBecomesALocalType()
    {
        using var fixture = new DecompilerFixture();
        var library = WriteGenericLibrary(fixture);
        var patch = fixture.Patch();
        patch.ExternTypes.AddRange([$"Example.Box`1[[System.String, mscorlib]], {library}", "System.String, mscorlib"]);
        patch.InternStrings.Add("unused class argument");
        patch.ExternMethods.Add(GenericMethod("Repeat"));
        fixture.Method(patch, fixture.Op("StackSpace", (1 << 16) | 2), fixture.Op("Ldstr"), fixture.Op("Ldc_I4", 21),
            fixture.Op("CallExtern", 2 << 16), fixture.Op("Stloc"), fixture.Op("Ldloc"), fixture.Op("Ldc_I4"), fixture.Op("Ldelem_I4"),
            fixture.Op("Ldloc"), fixture.Op("Ldc_I4", 1), fixture.Op("Ldelem_I4"), fixture.Op("Add"), fixture.Op("Ret", 1));
        fixture.Fix(patch, 0, "ReadA");
        var (report, owner, _) = fixture.Decompile(patch);
        Assert.Empty(report.Problems);
        Assert.Equal(42, owner.GetMethod("ReadA__m0")!.Invoke(null, null));
        AssertClosedLocals(report.AssemblyImage);
    }

    [Fact]
    public void NestedGenericReturnSubstitutesBothClassAndMethodTypeArguments()
    {
        using var fixture = new DecompilerFixture();
        var library = WriteGenericLibrary(fixture);
        var patch = fixture.Patch();
        patch.ExternTypes.AddRange([$"Example.Box`1[[System.String, mscorlib]], {library}", "System.String, mscorlib",
            "System.Tuple`2[[System.String, mscorlib],[System.Int32, mscorlib]], mscorlib"]);
        patch.InternStrings.Add("first");
        patch.ExternMethods.Add(GenericMethod("Pair"));
        patch.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 7, Name = "get_Item2" });
        fixture.Method(patch, fixture.Op("StackSpace", (1 << 16) | 2), fixture.Op("Ldstr"), fixture.Op("Ldc_I4", 42),
            fixture.Op("CallExtern", 2 << 16), fixture.Op("Stloc"), fixture.Op("Ldloc"), fixture.Op("CallExtern", (1 << 16) | 1), fixture.Op("Ret", 1));
        fixture.Fix(patch, 0, "ReadA");
        var (report, owner, _) = fixture.Decompile(patch);
        Assert.Empty(report.Problems);
        Assert.Equal(42, owner.GetMethod("ReadA__m0")!.Invoke(null, null));
        AssertClosedLocals(report.AssemblyImage);
    }

    [Theory]
    [InlineData("integer", 42)]
    [InlineData("array", 3)]
    public void GenericOutParameterDeclaresTheCorrectLocalElementType(string shape, int expected)
    {
        using var fixture = new DecompilerFixture();
        var library = WriteGenericLibrary(fixture);
        var patch = fixture.Patch();
        patch.ExternTypes.AddRange([$"Example.Box`1[[System.Int32, mscorlib]], {library}", "System.Int32&, mscorlib", "System.Int32[], mscorlib", "System.Int32[]&, mscorlib"]);
        patch.ExternMethods.Add(new ExternMethodSig
        {
            DeclaringTypeId = 5,
            Name = shape == "array" ? "CopyArray" : "Copy",
            Params = [new() { TypeId = shape == "array" ? 7 : 1 }, new() { TypeId = shape == "array" ? 8 : 6 }]
        });
        var code = new List<Instr> { fixture.Op("StackSpace", (1 << 16) | 2), fixture.Op("Ldc_I4", shape == "array" ? 3 : 42) };
        if (shape == "array") code.Add(fixture.Op("Newarr", 1));
        code.AddRange([fixture.Op("Ldloca"), fixture.Op("CallExtern", 2 << 16), fixture.Op("Ldloc")]);
        if (shape == "array") code.AddRange([fixture.Op("Ldlen"), fixture.Op("Conv_I4")]);
        code.Add(fixture.Op("Ret", 1));
        fixture.Method(patch, [.. code]); fixture.Fix(patch, 0, "ReadA");
        var (report, owner, _) = fixture.Decompile(patch);
        Assert.Empty(report.Problems);
        Assert.Equal(expected, owner.GetMethod("ReadA__m0")!.Invoke(null, null));
        AssertClosedLocals(report.AssemblyImage);
    }

    [Fact]
    public void GenericByrefReturnKeepsManagedReferenceIdentityThroughALocal()
    {
        using var fixture = new DecompilerFixture();
        var library = WriteGenericLibrary(fixture);
        var patch = fixture.Patch();
        patch.ExternTypes.AddRange([$"Example.Box`1[[System.Int32, mscorlib]], {library}", "System.Int32[], mscorlib"]);
        patch.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 5, Name = "First", Params = [new() { TypeId = 6 }] });
        fixture.Method(patch, fixture.Op("StackSpace", (2 << 16) | 3), fixture.Op("Ldc_I4", 1), fixture.Op("Newarr", 1), fixture.Op("Stloc"),
            fixture.Op("Ldloc"), fixture.Op("CallExtern", 1 << 16), fixture.Op("Stloc", 1), fixture.Op("Ldloc", 1), fixture.Op("Ldc_I4", 42), fixture.Op("Stind_I4"),
            fixture.Op("Ldloc"), fixture.Op("Ldc_I4"), fixture.Op("Ldelem_I4"), fixture.Op("Ret", 1));
        fixture.Fix(patch, 0, "ReadA");
        var (report, owner, _) = fixture.Decompile(patch);
        Assert.Empty(report.Problems);
        Assert.Equal(42, owner.GetMethod("ReadA__m0")!.Invoke(null, null));
        AssertClosedLocals(report.AssemblyImage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConstructedGenericFieldAccessPreservesStorageAndLocalType(bool address)
    {
        using var fixture = new DecompilerFixture();
        var library = WriteGenericLibrary(fixture);
        var patch = fixture.Patch();
        patch.ExternTypes.Add($"Example.Box`1[[System.Int32, mscorlib]], {library}");
        patch.Fields.Add(new FieldEntry { DeclaringTypeId = 5, Name = "Value" });
        var code = new List<Instr> { fixture.Op("StackSpace", (1 << 16) | 2) };
        if (address) code.Add(fixture.Op("Ldsflda"));
        code.AddRange([fixture.Op("Ldc_I4", 42), fixture.Op(address ? "Stind_I4" : "Stsfld"), fixture.Op("Ldsfld"), fixture.Op("Stloc"), fixture.Op("Ldloc"), fixture.Op("Ret", 1)]);
        fixture.Method(patch, [.. code]); fixture.Fix(patch, 0, "ReadA");
        var (report, owner, _) = fixture.Decompile(patch);
        Assert.Empty(report.Problems);
        Assert.Equal(42, owner.GetMethod("ReadA__m0")!.Invoke(null, null));
        AssertClosedLocals(report.AssemblyImage);
    }

    private static ExternMethodSig GenericMethod(string name) => new()
    {
        DeclaringTypeId = 5,
        Name = name,
        IsGenericInstance = true,
        GenericArgTypeIds = [1],
        Params = [new() { TypeId = 6 }, new() { IsGeneric = true, GenericName = "U" }],
    };

    private static void AssertClosedLocals(byte[] image)
    {
        using var stream = new MemoryStream(image);
        using var assembly = AssemblyDefinition.ReadAssembly(stream);
        Assert.All(assembly.MainModule.Types.SelectMany(type => type.Methods).Where(method => method.HasBody).SelectMany(method => method.Body.Variables),
            variable => Assert.False(variable.VariableType.ContainsGenericParameter));
    }

    private static string WriteGenericLibrary(DecompilerFixture fixture)
    {
        var name = "InferenceLibrary" + Guid.NewGuid().ToString("N");
        var path = Path.Combine(fixture.DirectoryPath, name + ".dll");
        using (var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name, new Version(1, 0)), name, ModuleKind.Dll))
        {
            var module = assembly.MainModule;
            var box = new TypeDefinition("Example", "Box`1", TypeAttributes.Public, module.TypeSystem.Object);
            module.Types.Add(box);
            var classParameter = new GenericParameter("T", box); box.GenericParameters.Add(classParameter);
            box.Fields.Add(new FieldDefinition("Value", FieldAttributes.Public | FieldAttributes.Static, classParameter));
            MethodDefinition Method(string methodName)
            {
                var method = new MethodDefinition(methodName, MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
                box.Methods.Add(method); return method;
            }
            // Equivalent to: U Echo<U>(T first, U second) => second.
            var echo = Method("Echo"); var methodParameter = new GenericParameter("U", echo); echo.GenericParameters.Add(methodParameter);
            echo.ReturnType = methodParameter;
            echo.Parameters.Add(new ParameterDefinition(classParameter)); echo.Parameters.Add(new ParameterDefinition(methodParameter));
            echo.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1)); echo.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            // Equivalent to: U[] Repeat<U>(T first, U second) => new[] { second, second }.
            var repeat = Method("Repeat"); var repeatParameter = new GenericParameter("U", repeat); repeat.GenericParameters.Add(repeatParameter);
            repeat.ReturnType = new ArrayType(repeatParameter);
            repeat.Parameters.Add(new ParameterDefinition(classParameter)); repeat.Parameters.Add(new ParameterDefinition(repeatParameter));
            var il = repeat.Body.GetILProcessor(); il.Emit(OpCodes.Ldc_I4_2); il.Emit(OpCodes.Newarr, repeatParameter);
            for (var index = 0; index < 2; index++) { il.Emit(OpCodes.Dup); il.Emit(OpCodes.Ldc_I4, index); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Stelem_Any, repeatParameter); }
            il.Emit(OpCodes.Ret);
            var pair = Method("Pair"); var pairParameter = new GenericParameter("U", pair); pair.GenericParameters.Add(pairParameter);
            var tuple = new TypeReference("System", "Tuple`2", module, module.TypeSystem.CoreLibrary);
            tuple.GenericParameters.Add(new GenericParameter("T1", tuple)); tuple.GenericParameters.Add(new GenericParameter("T2", tuple));
            var constructedTuple = new GenericInstanceType(tuple); constructedTuple.GenericArguments.Add(classParameter); constructedTuple.GenericArguments.Add(pairParameter);
            pair.ReturnType = constructedTuple;
            pair.Parameters.Add(new ParameterDefinition(classParameter)); pair.Parameters.Add(new ParameterDefinition(pairParameter));
            var tupleConstructor = new MethodReference(".ctor", module.TypeSystem.Void, constructedTuple) { HasThis = true };
            tupleConstructor.Parameters.Add(new ParameterDefinition(tuple.GenericParameters[0])); tupleConstructor.Parameters.Add(new ParameterDefinition(tuple.GenericParameters[1]));
            pair.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); pair.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1));
            pair.Body.Instructions.Add(Instruction.Create(OpCodes.Newobj, tupleConstructor)); pair.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            foreach (var array in new[] { false, true })
            {
                var copy = Method(array ? "CopyArray" : "Copy"); TypeReference valueType = array ? new ArrayType(classParameter) : classParameter;
                copy.Parameters.Add(new ParameterDefinition(valueType)); copy.Parameters.Add(new ParameterDefinition("destination", ParameterAttributes.Out, new ByReferenceType(valueType)));
                copy.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1)); copy.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                copy.Body.Instructions.Add(Instruction.Create(OpCodes.Stobj, valueType)); copy.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            }
            var first = Method("First"); first.ReturnType = new ByReferenceType(classParameter); first.Parameters.Add(new ParameterDefinition(new ArrayType(classParameter)));
            first.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); first.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
            first.Body.Instructions.Add(Instruction.Create(OpCodes.Ldelema, classParameter)); first.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            assembly.Write(path);
        }
        AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        return name;
    }
}
