using IFixPatch.Core;

namespace IFixPatch.Decompiler.Tests;

public sealed class DelegateTypeInferenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DelegatePreservesConstructorCaptureThroughLocalOrVmStaticStorage(bool viaStatic)
    {
        using var f = new DecompilerFixture();
        var p = f.Patch();
        p.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 2, Name = ".ctor" });
        p.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 3, Name = ".ctor", Params = [new() { TypeId = 2 }, new() { TypeId = 4 }] });
        f.Method(p, f.Op("StackSpace", (1 << 16) | 2), f.Op("Ldarg"), f.Op("Newanon"),
            f.Op(viaStatic ? "Stsfld" : "Stloc", viaStatic ? -1 : 0),
            f.Op(viaStatic ? "Ldsfld" : "Ldloc", viaStatic ? -1 : 0), f.Op("Ldc_I4", 2), f.Op("Newobj", (2 << 16) | 1), f.Op("Ret", 1));
        f.Method(p, f.Op("StackSpace", 2), f.Op("Ldarg"), f.Op("CallExtern", 1 << 16), f.Op("Ldarg"), f.Op("Ldarg", 1), f.Op("Stfld", -1), f.Op("Ret"));
        f.Method(p, f.Op("StackSpace", 2), f.Op("Ldarg"), f.Op("Ldfld", -1), f.Op("Ldc_I4", 2), f.Op("Mul"), f.Op("Ret", 1));
        p.AnonStoreys.Add(new AnonStorey { CtorId = 1, CtorParamNum = 1, FieldTypes = [2] });
        if (viaStatic) p.StaticFields.Add(new StaticFieldEntry { FieldTypeId = 2, CctorId = -1 });
        f.Fix(p, 0, "Pointer", 1);
        var (report, owner, _) = f.Decompile(p);
        Assert.Empty(report.Problems);
        var capturedFirst = Assert.IsType<Func<int>>(owner.GetMethod("Pointer__m0")!.Invoke(null, [21]));
        var capturedSecond = Assert.IsType<Func<int>>(owner.GetMethod("Pointer__m0")!.Invoke(null, [5]));
        Assert.Equal(42, capturedFirst());
        Assert.Equal(10, capturedSecond());
        Assert.Equal(42, capturedFirst());
    }

    [Fact]
    public void DelegateInvokeContractPreservesUnusedArgumentSlots()
    {
        using var f = new DecompilerFixture();
        var p = f.Patch();
        p.ExternTypes.Add("System.Action`1[[System.Int32, mscorlib]], mscorlib");
        p.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 2, Name = ".ctor" });
        p.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 5, Name = ".ctor", Params = [new() { TypeId = 2 }, new() { TypeId = 4 }] });
        f.Method(p, f.Op("StackSpace", (1 << 16) | 2), f.Op("Newanon"), f.Op("Stloc"), f.Op("Ldloc"), f.Op("Ldc_I4", 2), f.Op("Newobj", (2 << 16) | 1), f.Op("Ret", 1));
        f.Method(p, f.Op("StackSpace", 1), f.Op("Ldarg"), f.Op("CallExtern", 1 << 16), f.Op("Ret"));
        f.Method(p, f.Op("StackSpace"), f.Op("Ret"));
        p.AnonStoreys.Add(new AnonStorey { CtorId = 1 });
        f.Fix(p, 0, "ObjectValue");
        var (report, owner, _) = f.Decompile(p);
        Assert.Empty(report.Problems);
        var action = Assert.IsType<Action<int>>(owner.GetMethod("ObjectValue__m0")!.Invoke(null, null));
        action(42);
        Assert.Equal(typeof(int), Assert.Single(action.Method.GetParameters()).ParameterType);
    }
}
