using IFixPatch.Core;
using IFixPatchDecompiler;
using System.Reflection;

namespace IFixPatch.Decompiler.Tests;

public sealed class FunctionPointerTests
{
    [Fact]
    public void ExternalFunctionPointerConstructsADelegateToTheSelectedInstanceMethod()
    {
        using var fixture = new DecompilerFixture();
        var map = PointerMap(fixture);
        var patch = fixture.Patch();
        patch.ExternTypes.Add("System.String, mscorlib"); patch.InternStrings.Add("seven!!");
        patch.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 5, Name = "get_Length" });
        patch.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 3, Name = ".ctor", Params = [new() { TypeId = 2 }, new() { TypeId = 4 }] });
        fixture.Method(patch, fixture.Op("StackSpace", 2), fixture.Op("Ldstr"), new Instr { Code = 1000, Operand = 0 }, fixture.Op("Newobj", (2 << 16) | 1), fixture.Op("Ret", 1));
        fixture.Fix(patch, 0, "Pointer", 1);
        var (report, owner) = Decompile(fixture, patch, map);
        Assert.Empty(report.Problems);
        var function = Assert.IsType<Func<int>>(owner.GetMethod("Pointer__m0")!.Invoke(null, [0]));
        Assert.Equal(7, function());
    }

    [Fact]
    public void ExternalVirtualFunctionPointerBindsTheRuntimeOverride()
    {
        using var fixture = new DecompilerFixture();
        var map = PointerMap(fixture);
        var patch = fixture.Patch();
        patch.ExternTypes.Add("System.Func`1[[System.String, mscorlib]], mscorlib"); patch.InternStrings.Add("runtime string override");
        patch.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 2, Name = "ToString" });
        patch.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 5, Name = ".ctor", Params = [new() { TypeId = 2 }, new() { TypeId = 4 }] });
        fixture.Method(patch, fixture.Op("StackSpace", 2), fixture.Op("Ldstr"), fixture.Op("Dup"), new Instr { Code = 1001, Operand = 0 },
            fixture.Op("Newobj", (2 << 16) | 1), fixture.Op("Ret", 1));
        fixture.Fix(patch, 0, "ObjectValue");
        var (report, owner) = Decompile(fixture, patch, map);
        Assert.Empty(report.Problems);
        var function = Assert.IsType<Func<string>>(owner.GetMethod("ObjectValue__m0")!.Invoke(null, null));
        Assert.Equal("runtime string override", function());
    }

    [Theory]
    [InlineData(1000, "Ldftn")]
    [InlineData(1001, "Ldvirtftn")]
    public void UnresolvedExternalFunctionPointerIsReportedInsteadOfClaimingResolution(int code, string operation)
    {
        using var fixture = new DecompilerFixture();
        var map = PointerMap(fixture);
        var patch = fixture.Patch();
        patch.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 2, Name = "MissingFromMetadata" });
        var body = new List<Instr> { fixture.Op("StackSpace", 1) };
        if (operation == "Ldvirtftn") body.Add(fixture.Op("Ldnull"));
        body.AddRange([new Instr { Code = code, Operand = 0 }, fixture.Op("Pop"), fixture.Op("Ret")]);
        fixture.Method(patch, [.. body]); fixture.Fix(patch, 0, "SetStaticField", 1);
        PatchValidation.RequireValid(patch, map);
        using var source = new DummyDllSource(fixture.DirectoryPath);
        var report = PatchDecompiler.Run(patch, map, source);
        Assert.Contains(report.Problems, problem => problem.Contains(operation) && problem.Contains("extern#0"));
        Assert.Equal(0, report.ExternResolved);
        Assert.Equal(1, report.ExternTotal);
        // The diagnostic fallback does not execute a replacement for the absent method.
    }

    [Fact]
    public void ArrayElementAddressUpdatesTheSelectedElementOnly()
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch();
        fixture.Method(patch, fixture.Op("StackSpace", (1 << 16) | 3), fixture.Op("Ldc_I4", 2), fixture.Op("Newarr", 1), fixture.Op("Stloc"),
            fixture.Op("Ldloc"), fixture.Op("Ldc_I4", 1), fixture.Op("Ldelema", 1), fixture.Op("Ldc_I4", 42), fixture.Op("Stind_I4"),
            fixture.Op("Ldloc"), fixture.Op("Ldc_I4"), fixture.Op("Ldelem_I4"), fixture.Op("Ldloc"), fixture.Op("Ldc_I4", 1), fixture.Op("Ldelem_I4"),
            fixture.Op("Sub"), fixture.Op("Ret", 1));
        fixture.Fix(patch, 0, "ReadA");
        var (report, owner, _) = fixture.Decompile(patch);
        Assert.Empty(report.Problems);
        // The untouched element is 0, and the element addressed at index 1 becomes 42.
        Assert.Equal(-42, owner.GetMethod("ReadA__m0")!.Invoke(null, null));
    }

    private static OpCodeMap PointerMap(DecompilerFixture fixture)
    {
        var path = Path.Combine(fixture.DirectoryPath, "pointers.map"); fixture.SaveMap(path);
        File.AppendAllLines(path, ["Ldftn=1000", "Ldvirtftn=1001"]);
        return OpCodeMap.FromFile(path);
    }

    private static (DecompileReport, Type) Decompile(DecompilerFixture fixture, PatchFile patch, OpCodeMap map)
    {
        PatchValidation.RequireValid(patch, map);
        using var source = new DummyDllSource(fixture.DirectoryPath);
        var report = PatchDecompiler.Run(patch, map, source);
        Assert.Empty(report.Balance);
        return (report, Assembly.Load(report.AssemblyImage).GetType("Example.Calculator__Patch", true)!);
    }
}
