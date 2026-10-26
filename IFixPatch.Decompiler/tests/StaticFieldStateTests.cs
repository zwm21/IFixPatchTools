using IFixPatch.Core;
using System.Reflection;

namespace IFixPatch.Decompiler.Tests;

public sealed class StaticFieldStateTests
{
    [Fact]
    public void FieldsWithSameInitializerShareInitializationAndPreserveMutation()
    {
        using var fixture = new DecompilerFixture();
        var patch = StaticPatch(fixture, separateInitializers: false);

        var (_, owner, _) = fixture.Decompile(patch);
        var readInitializations = Assert.Single(owner.GetMethods(), method => method.Name.StartsWith("ReadInitializations__", StringComparison.Ordinal));

        Assert.Equal(0, readInitializations.Invoke(null, null));
        Assert.Equal(10, owner.GetMethod("ReadA__m0")!.Invoke(null, null));
        Assert.Equal(1, readInitializations.Invoke(null, null));
        Assert.Equal(11, owner.GetMethod("Bump__m2")!.Invoke(null, null));
        Assert.Equal(20, owner.GetMethod("ReadB__m1")!.Invoke(null, null));
        Assert.Equal(11, owner.GetMethod("ReadA__m0")!.Invoke(null, null));
        Assert.Equal(1, readInitializations.Invoke(null, null));
    }

    [Fact]
    public void DifferentInitializersRemainLazyAndIndependent()
    {
        using var fixture = new DecompilerFixture();
        var patch = StaticPatch(fixture, separateInitializers: true);

        var (_, owner, _) = fixture.Decompile(patch);
        var readInitializations = Assert.Single(owner.GetMethods(), method => method.Name.StartsWith("ReadInitializations__", StringComparison.Ordinal));

        Assert.Equal(0, readInitializations.Invoke(null, null));
        Assert.Equal(10, owner.GetMethod("ReadA__m0")!.Invoke(null, null));
        Assert.Equal(1, readInitializations.Invoke(null, null));
        Assert.Equal(11, owner.GetMethod("Bump__m2")!.Invoke(null, null));
        Assert.Equal(20, owner.GetMethod("ReadB__m1")!.Invoke(null, null));
        Assert.Equal(99, owner.GetMethod("ReadA__m0")!.Invoke(null, null));
        Assert.Equal(2, readInitializations.Invoke(null, null));
    }

    [Theory]
    [InlineData("GetField")]
    [InlineData("SetField")]
    [InlineData("GetStaticField")]
    [InlineData("SetStaticField")]
    public void AddedSideTableAccessThrowsAndIdentifiesTheField(string name)
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch();
        patch.Fields.Add(new FieldEntry { IsNew = true, DeclaringTypeId = 0, Name = "Added", FieldTypeId = 1, CctorId = -1 });
        var (code, parameterTypes, arguments) = name switch
        {
            "GetField" => (new[] { fixture.Op("StackSpace", 1), fixture.Op("Ldarg"), fixture.Op("Ldfld"), fixture.Op("Ret", 1) }, new[] { 2 }, new object?[] { new object() }),
            "SetField" => (new[] { fixture.Op("StackSpace", 2), fixture.Op("Ldarg"), fixture.Op("Ldarg", 1), fixture.Op("Stfld"), fixture.Op("Ret") }, new[] { 2, 1 }, new object?[] { new object(), 8 }),
            "GetStaticField" => (new[] { fixture.Op("StackSpace", 1), fixture.Op("Ldsfld"), fixture.Op("Ret", 1) }, Array.Empty<int>(), Array.Empty<object?>()),
            _ => (new[] { fixture.Op("StackSpace", 1), fixture.Op("Ldarg"), fixture.Op("Stsfld"), fixture.Op("Ret") }, new[] { 1 }, new object?[] { 8 }),
        };
        fixture.Method(patch, code);
        fixture.Fix(patch, 0, name, parameterTypes);

        var (report, owner, _) = fixture.Decompile(patch);
        var error = Assert.Throws<TargetInvocationException>(() => owner.GetMethod(name + "__m0")!.Invoke(null, arguments));

        var unsupported = Assert.IsType<NotSupportedException>(error.InnerException);
        Assert.Contains("Added", unsupported.Message);
        Assert.Contains(report.Problems, problem => problem.Contains("Added") && problem.Contains("side-table"));
    }


    [Fact]
    public void UnknownObjectReceiverReportsThatItsClosureFieldCannotBeLocated()
    {
        using var f = new DecompilerFixture();
        var p = f.Patch();
        p.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 2, Name = ".ctor" });
        f.Method(p, f.Op("StackSpace", 1), f.Op("Ldarg"), f.Op("Ldfld", -1), f.Op("Ret", 1));
        f.Method(p, f.Op("StackSpace", 1), f.Op("Ldarg"), f.Op("CallExtern", 1 << 16), f.Op("Ret"));
        p.AnonStoreys.Add(new AnonStorey { CtorId = 1, FieldTypes = [0] });
        f.Fix(p, 0, "GetField", 2);
        PatchValidation.RequireValid(p, f.Map);
        using var source = new DummyDllSource(f.DirectoryPath);
        var report = IFixPatchDecompiler.PatchDecompiler.Run(p, f.Map, source);
        Assert.Contains(report.Problems, problem => problem.Contains("Ldfld") && problem.Contains("接收者") && problem.Contains("闭包"));
        // The opaque object parameter gives no receiver identity; generated placeholder IL is not executed.
    }

    private static PatchFile StaticPatch(DecompilerFixture fixture, bool separateInitializers)
    {
        var patch = fixture.Patch();
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldsfld", -1), fixture.Op("Ret", 1));
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldsfld", -2), fixture.Op("Ret", 1));
        fixture.Method(patch, fixture.Op("StackSpace", 2), fixture.Op("Ldsfld", -1), fixture.Op("Ldc_I4", 1),
            fixture.Op("Add"), fixture.Op("Stsfld", -1), fixture.Op("Ldsfld", -1), fixture.Op("Ret", 1));
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldc_I4", 10), fixture.Op("Stsfld", -1), fixture.Op("Ret"));
        if (separateInitializers)
            fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldc_I4", 99), fixture.Op("Stsfld", -1),
                fixture.Op("Ldc_I4", 20), fixture.Op("Stsfld", -2), fixture.Op("Ret"));
        else
            patch.Methods[3].Code = [fixture.Op("StackSpace", 1), fixture.Op("Ldc_I4", 10), fixture.Op("Stsfld", -1),
                fixture.Op("Ldc_I4", 20), fixture.Op("Stsfld", -2), fixture.Op("Ret")];
        foreach (var initializer in patch.Methods.Skip(3))
        {
            initializer.Code[0] = fixture.Op("StackSpace", 2);
            initializer.Code.InsertRange(1, [fixture.Op("Ldsfld", -3), fixture.Op("Ldc_I4", 1), fixture.Op("Add"), fixture.Op("Stsfld", -3)]);
        }
        patch.StaticFields.Add(new StaticFieldEntry { FieldTypeId = 1, CctorId = 3 });
        patch.StaticFields.Add(new StaticFieldEntry { FieldTypeId = 1, CctorId = separateInitializers ? 4 : 3 });
        patch.StaticFields.Add(new StaticFieldEntry { FieldTypeId = 1, CctorId = -1 });
        var readCount = fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldsfld", -3), fixture.Op("Ret", 1));
        fixture.Fix(patch, 0, "ReadA");
        fixture.Fix(patch, 1, "ReadB");
        fixture.Fix(patch, 2, "Bump");
        fixture.Fix(patch, readCount, "ReadInitializations");
        return patch;
    }
}
