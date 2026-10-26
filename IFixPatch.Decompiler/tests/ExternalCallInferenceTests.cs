using IFixPatch.Core;
using IFixPatchDecompiler;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Runtime.Loader;

namespace IFixPatch.Decompiler.Tests;

public sealed class ExternalCallInferenceTests
{
    [Theory]
    [InlineData("array", 3)]
    [InlineData("list", 2)]
    [InlineData("byref", 42)]
    public void SelectsGenericOverloadsUsingClosedMethodArguments(string shape, int expected)
    {
        using var fixture = new DecompilerFixture();
        var library = WriteLibrary(fixture);
        var patch = fixture.Patch();
        patch.ExternTypes.AddRange([$"Example.Api, {library}", "System.Int32[], mscorlib",
            "System.Collections.Generic.List`1[[System.Int32, mscorlib]], mscorlib", "System.Int32&, mscorlib"]);
        var parameter = shape == "array" ? 6 : shape == "list" ? 7 : 8;
        patch.ExternMethods.Add(new()
        {
            DeclaringTypeId = 5,
            Name = "Read",
            IsGenericInstance = true,
            GenericArgTypeIds = [1],
            Params = [new() { TypeId = parameter }]
        });
        var code = new List<Instr> { fixture.Op("StackSpace", (1 << 16) | 3) };
        if (shape == "array") code.AddRange([fixture.Op("Ldc_I4", 3), fixture.Op("Newarr", 1)]);
        else if (shape == "list")
        {
            patch.ExternMethods.Add(new() { DeclaringTypeId = 7, Name = ".ctor" });
            patch.ExternMethods.Add(new() { DeclaringTypeId = 7, Name = "Add", Params = [new() { TypeId = 1 }] });
            code.Add(fixture.Op("Newobj", 1));
            foreach (var value in new[] { 40, 2 }) code.AddRange([fixture.Op("Dup"), fixture.Op("Ldc_I4", value), fixture.Op("CallExtern", (2 << 16) | 2)]);
        }
        else code.AddRange([fixture.Op("Ldc_I4", 42), fixture.Op("Stloc"), fixture.Op("Ldloca")]);
        code.AddRange([fixture.Op("CallExtern", 1 << 16), fixture.Op("Ret", 1)]);
        fixture.Method(patch, [.. code]); fixture.Fix(patch, 0, "ReadA");
        var (report, owner, _) = fixture.Decompile(patch);
        Assert.Empty(report.Problems);
        Assert.Equal(expected, owner.GetMethod("ReadA__m0")!.Invoke(null, null));
    }

    [Fact]
    public void InstanceOutParametersAccountForTheValueTypeReceiver()
    {
        using var fixture = new DecompilerFixture();
        var library = WriteLibrary(fixture);
        var patch = fixture.Patch();
        patch.ExternTypes.AddRange([$"Example.Reader, {library}", "System.Int32&, mscorlib"]);
        patch.ExternMethods.Add(new() { DeclaringTypeId = 5, Name = "TryRead", Params = [new() { TypeId = 6 }] });
        fixture.Method(patch, fixture.Op("StackSpace", (2 << 16) | 2), fixture.Op("Ldloca"), fixture.Op("Initobj", 5),
            fixture.Op("Ldloca"), fixture.Op("Ldloca", 1), fixture.Op("CallExtern", 2 << 16), fixture.Op("Pop"),
            fixture.Op("Ldloc", 1), fixture.Op("Ret", 1));
        fixture.Fix(patch, 0, "ReadA");
        var (report, owner, _) = fixture.Decompile(patch);
        Assert.Empty(report.Problems);
        Assert.Equal(42, owner.GetMethod("ReadA__m0")!.Invoke(null, null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingNativeMembersProduceResolutionDiagnostics(bool constructor)
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch();
        if (constructor)
        {
            patch.ExternMethods.Add(new() { DeclaringTypeId = 0, Name = ".ctor", Params = [new() { TypeId = 1 }] });
            fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldc_I4", 1), fixture.Op("Newobj", 1 << 16), fixture.Op("Ret", 1));
        }
        else
        {
            patch.Fields.Add(new() { DeclaringTypeId = 0, Name = "Absent" });
            fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldsfld"), fixture.Op("Ret", 1));
        }
        fixture.Fix(patch, 0, "ObjectValue");
        PatchValidation.RequireValid(patch, fixture.Map);
        using var source = new DummyDllSource(fixture.DirectoryPath);
        var report = PatchDecompiler.Run(patch, fixture.Map, source);
        Assert.NotEmpty(report.Problems);
        Assert.Contains(report.Problems, problem => problem.Contains(constructor ? "Newobj" : "field#0", StringComparison.Ordinal));
        // Missing metadata establishes a diagnostic requirement, not placeholder runtime equivalence.
    }

    private static string WriteLibrary(DecompilerFixture fixture)
    {
        var name = "InferenceContracts" + Guid.NewGuid().ToString("N");
        var path = Path.Combine(fixture.DirectoryPath, name + ".dll");
        using (var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name, new Version(1, 0)), name, ModuleKind.Dll))
        {
            var module = assembly.MainModule;
            var api = new TypeDefinition("Example", "Api", TypeAttributes.Public, module.TypeSystem.Object);
            module.Types.Add(api);
            foreach (var shape in new[] { "array", "list", "byref" })
            {
                var method = new MethodDefinition("Read", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32);
                api.Methods.Add(method);
                var argument = new GenericParameter("T", method); method.GenericParameters.Add(argument);
                TypeReference parameter = shape == "array" ? new ArrayType(argument) : new ByReferenceType(argument);
                if (shape == "list")
                {
                    var list = new GenericInstanceType(new TypeReference("System.Collections.Generic", "List`1", module, module.TypeSystem.CoreLibrary));
                    list.GenericArguments.Add(argument); parameter = list;
                }
                method.Parameters.Add(new ParameterDefinition(parameter));
                var il = method.Body.GetILProcessor(); il.Emit(OpCodes.Ldarg_0);
                if (shape == "array") { il.Emit(OpCodes.Ldlen); il.Emit(OpCodes.Conv_I4); }
                else if (shape == "list") il.Emit(OpCodes.Callvirt, new MethodReference("get_Count", module.TypeSystem.Int32, parameter) { HasThis = true });
                else
                {
                    il.Emit(OpCodes.Ldobj, argument); il.Emit(OpCodes.Box, argument);
                    var convert = new MethodReference("ToInt32", module.TypeSystem.Int32, new TypeReference("System", "Convert", module, module.TypeSystem.CoreLibrary));
                    convert.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object)); il.Emit(OpCodes.Call, convert);
                }
                il.Emit(OpCodes.Ret);
            }
            var reader = new TypeDefinition("Example", "Reader", TypeAttributes.Public | TypeAttributes.Sealed,
                new TypeReference("System", "ValueType", module, module.TypeSystem.CoreLibrary)); module.Types.Add(reader);
            var read = new MethodDefinition("TryRead", MethodAttributes.Public, module.TypeSystem.Boolean);
            reader.Methods.Add(read); read.Parameters.Add(new ParameterDefinition("result", ParameterAttributes.Out, new ByReferenceType(module.TypeSystem.Int32)));
            var body = read.Body.GetILProcessor(); body.Emit(OpCodes.Ldarg_1); body.Emit(OpCodes.Ldc_I4, 42); body.Emit(OpCodes.Stind_I4); body.Emit(OpCodes.Ldc_I4_1); body.Emit(OpCodes.Ret);
            assembly.Write(path);
        }
        AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        return name;
    }
}
