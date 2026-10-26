using IFixPatch.Core;
using Mono.Cecil;

namespace IFixPatch.Decompiler.Tests;

public sealed class VirtualDispatchArgumentTests
{
    [Theory]
    [InlineData(false, 1, 1, 18)]
    [InlineData(false, 1, 0, 29)]
    [InlineData(true, 1, 1, 18)]
    [InlineData(true, 1, 0, 29)]
    [InlineData(false, 5, 1, 46)]
    [InlineData(false, 5, 0, 57)]
    [InlineData(true, 5, 1, 46)]
    [InlineData(true, 5, 0, 57)]
    public void VirtualCallsForwardEveryArgumentToTheSelectedReceiver(bool shared, int argumentCount, int chooseFirst, int expected)
    {
        using var f = new DecompilerFixture();
        var p = ParameterizedPatch(f, shared, argumentCount);
        var (report, owner, _) = f.Decompile(p);
        Assert.Empty(report.Problems);
        // Captured receiver values are 11 and 22. The body adds the last supplied argument:
        // 7 for one argument, 35 for five. Earlier arguments deliberately differ.
        object?[] values = [chooseFirst, .. Enumerable.Range(1, argumentCount).Select(index => (object?)(index * 7))];
        Assert.Equal(expected, owner.GetMethod("Value__m0")!.Invoke(null, values));
    }

    [Theory]
    [InlineData(1, 38)]
    [InlineData(0, 60)]
    public void RepeatedVirtualCallsKeepTheReceiverAndEachCallsArgument(int chooseFirst, int expected)
    {
        using var f = new DecompilerFixture();
        var p = ParameterizedPatch(f, true, 1);
        p.Methods[0].Code = [f.Op("StackSpace", (1 << 16) | 3), f.Op("Ldarg"), f.Op("Brfalse", 3), f.Op("Newanon"), f.Op("Br", 2), f.Op("Newanon", 1),
            f.Op("Stloc"), f.Op("Ldloc"), f.Op("Ldc_I4", 7), f.Op("Callvirtvirt", (2 << 16) | DecompilerFixture.Slot),
            f.Op("Ldloc"), f.Op("Ldc_I4", 9), f.Op("Callvirtvirt", (2 << 16) | DecompilerFixture.Slot), f.Op("Add"), f.Op("Ret", 1)];
        var (report, owner, _) = f.Decompile(p);
        Assert.Empty(report.Problems);
        Assert.Equal(expected, owner.GetMethod("Value__m0")!.Invoke(null, [chooseFirst, 0]));
    }

    [Theory]
    [InlineData(1, 18)]
    [InlineData(0, 29)]
    public void SharedVirtualMethodPreservesArgumentMutationForEachReceiver(int chooseFirst, int expected)
    {
        using var f = new DecompilerFixture();
        var p = ParameterizedPatch(f, true, 1);
        p.Methods[3].Code = [f.Op("StackSpace", 2), f.Op("Ldarg", 1), f.Op("Ldc_I4", 2), f.Op("Add"), f.Op("Starg", 1),
            f.Op("Ldarg"), f.Op("Ldfld", -1), f.Op("Ldarg", 1), f.Op("Add"), f.Op("Ret", 1)];
        var (report, owner, _) = f.Decompile(p);
        Assert.Empty(report.Problems);
        Assert.Equal(expected, owner.GetMethod("Value__m0")!.Invoke(null, [chooseFirst, 5]));
    }

    [Theory]
    [InlineData(1, 18, 0)]
    [InlineData(0, 29, 11)]
    public void VirtualFunctionPointersBindTheReceiverAndAcceptDelegateArguments(int chooseFirst, int positiveResult, int negativeResult)
    {
        using var f = new DecompilerFixture();
        var p = ParameterizedPatch(f, true, 1);
        var path = Path.Combine(f.DirectoryPath, "SyntheticTarget.dll");
        using (var target = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { InMemory = true }))
        {
            var module = target.MainModule;
            var function = new GenericInstanceType(new TypeReference("System", "Func`2", module, module.TypeSystem.CoreLibrary));
            function.GenericArguments.Add(module.TypeSystem.Int32); function.GenericArguments.Add(module.TypeSystem.Int32);
            module.GetType("Example.Calculator").Methods.Single(method => method.Name == "Pointer").ReturnType = function;
            target.Write(path);
        }
        var functionType = p.ExternTypes.Count;
        p.ExternTypes.Add("System.Func`2[[System.Int32, mscorlib],[System.Int32, mscorlib]], mscorlib");
        var constructor = p.ExternMethods.Count;
        p.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = functionType, Name = ".ctor", Params = [new() { TypeId = 2 }, new() { TypeId = 4 }] });
        p.Methods[5].Code = [f.Op("StackSpace", 2), f.Op("Ldarg"), f.Op("Brfalse", 3), f.Op("Newanon"), f.Op("Br", 2), f.Op("Newanon", 1),
            f.Op("Ldc_I4"), f.Op("Ldvirtftn2", DecompilerFixture.Slot), f.Op("Newobj", (2 << 16) | constructor), f.Op("Ret", 1)];
        f.Fix(p, 5, "Pointer", 1);
        var (report, owner, _) = f.Decompile(p);
        Assert.Empty(report.Problems);
        var functionValue = Assert.IsType<Func<int, int>>(owner.GetMethod("Pointer__m5")!.Invoke(null, [chooseFirst]));
        Assert.Equal(positiveResult, functionValue(7));
        Assert.Equal(negativeResult, functionValue(-11));
        Assert.Equal(positiveResult, functionValue(7));
    }

    private static PatchFile ParameterizedPatch(DecompilerFixture f, bool shared, int argumentCount)
    {
        ChangeValueSignature(f, argumentCount);
        var p = f.VirtualPatch(sharedImplementation: true);
        // Pointer creation has its own tests. These cases exercise normal VM calls with arguments.
        p.Methods[5].Code = [f.Op("StackSpace"), f.Op("Ret")];
        p.FixList.RemoveAt(1);
        p.FixList[0].Method.Params = Enumerable.Range(0, argumentCount + 1).Select(_ => new ExternMethodSig.Param { TypeId = 1 }).ToList();
        p.Methods[0].Code = [f.Op("StackSpace", argumentCount + 1), f.Op("Ldarg"), f.Op("Brfalse", 3), f.Op("Newanon"), f.Op("Br", 2), f.Op("Newanon", 1),
            .. Enumerable.Range(1, argumentCount).Select(index => f.Op("Ldarg", index)), f.Op("Callvirtvirt", ((argumentCount + 1) << 16) | DecompilerFixture.Slot), f.Op("Ret", 1)];
        p.Methods[3].Code = [f.Op("StackSpace", 2), f.Op("Ldarg"), f.Op("Ldfld", -1), f.Op("Ldarg", argumentCount), f.Op("Add"), f.Op("Ret", 1)];
        if (!shared)
        {
            p.AnonStoreys[1].VTable[DecompilerFixture.Slot] = 4;
            p.Methods[4].Code = [.. p.Methods[3].Code];
        }
        return p;
    }

    private static void ChangeValueSignature(DecompilerFixture f, int argumentCount)
    {
        var path = Path.Combine(f.DirectoryPath, "SyntheticTarget.dll");
        using var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { InMemory = true });
        var value = assembly.MainModule.GetType("Example.Calculator").Methods.Single(method => method.Name == "Value");
        for (var index = 0; index < argumentCount; index++) value.Parameters.Add(new ParameterDefinition(assembly.MainModule.TypeSystem.Int32));
        assembly.Write(path);
    }
}
