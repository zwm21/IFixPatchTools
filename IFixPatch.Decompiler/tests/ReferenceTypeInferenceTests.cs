using IFixPatch.Core;

namespace IFixPatch.Decompiler.Tests;

public sealed class ReferenceTypeInferenceTests
{
    [Theory]
    [InlineData("42", 42)]
    [InlineData("not an integer", 0)]
    [InlineData("-7", -7)]
    public void OutParameterSignatureRecoversALocalWithoutAnInitialAssignment(string text, int expected)
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch();
        patch.ExternTypes.AddRange(["System.String, mscorlib", "System.Int32&, mscorlib"]);
        patch.InternStrings.Add(text);
        patch.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 1, Name = "TryParse", Params = [new() { TypeId = 5 }, new() { TypeId = 6 }] });
        fixture.Method(patch, fixture.Op("StackSpace", (1 << 16) | 2), fixture.Op("Ldstr"), fixture.Op("Ldloca"),
            fixture.Op("CallExtern", 2 << 16), fixture.Op("Pop"), fixture.Op("Ldloc"), fixture.Op("Ret", 1));
        fixture.Fix(patch, 0, "ReadA");
        var (report, owner, _) = fixture.Decompile(patch);
        Assert.Empty(report.Problems);
        Assert.Equal(expected, owner.GetMethod("ReadA__m0")!.Invoke(null, null));
    }

    [Theory]
    [InlineData(42, "42")]
    [InlineData(-7, "-7")]
    public void ValueTypeInstanceCallOnALocalAddressRecoversTheReceiverDeclaration(int number, string expected)
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch();
        patch.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 1, Name = "ToString" });
        fixture.Method(patch, fixture.Op("StackSpace", (1 << 16) | 1), fixture.Op("Ldc_I4", number), fixture.Op("Stloc"),
            fixture.Op("Ldloca"), fixture.Op("CallExtern", 1 << 16), fixture.Op("Ret", 1));
        fixture.Fix(patch, 0, "TextValue");
        var (report, owner, _) = fixture.Decompile(patch);
        Assert.Empty(report.Problems);
        Assert.Equal(expected, owner.GetMethod("TextValue__m0")!.Invoke(null, null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullAndStringArgumentObservationsRemainCallableInEitherOrder(bool nullFirst)
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch(); patch.InternStrings.Add("text");
        var nullable = new[] { fixture.Op("Ldnull"), fixture.Op("Call", (1 << 16) | 1) };
        var text = new[] { fixture.Op("Ldstr"), fixture.Op("Call", (1 << 16) | 1) };
        var code = new List<Instr> { fixture.Op("StackSpace", 1) };
        code.AddRange(nullFirst ? nullable : text); code.Add(fixture.Op("Pop"));
        code.AddRange(nullFirst ? text : nullable); code.Add(fixture.Op("Ret", 1));
        fixture.Method(patch, [.. code]);
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldarg"), fixture.Op("Ret", 1));
        fixture.Fix(patch, 0, "ObjectValue");
        var (report, owner, _) = fixture.Decompile(patch);
        Assert.Empty(report.Problems);
        Assert.Equal(nullFirst ? "text" : null, owner.GetMethod("ObjectValue__m0")!.Invoke(null, null));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void UnrelatedReferenceOrArrayArgumentsUseAValidSharedReferenceContract(bool firstIsArray, bool secondIsArray)
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch();
        patch.ExternTypes.AddRange(["System.String, mscorlib", "System.Text.StringBuilder, mscorlib", "System.InvalidOperationException, mscorlib"]);
        patch.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 6, Name = ".ctor" });
        patch.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 7, Name = ".ctor" });
        var code = new List<Instr> { fixture.Op("StackSpace", 1) };
        if (firstIsArray) code.AddRange([fixture.Op("Ldc_I4", 1), fixture.Op("Newarr", 1)]);
        else code.Add(fixture.Op("Newobj"));
        code.AddRange([fixture.Op("Call", (1 << 16) | 1), fixture.Op("Pop")]);
        if (secondIsArray) code.AddRange([fixture.Op("Ldc_I4", 2), fixture.Op("Newarr", 5)]);
        else code.Add(fixture.Op("Newobj", 1));
        code.AddRange([fixture.Op("Call", (1 << 16) | 1), fixture.Op("Ret", 1)]);
        fixture.Method(patch, [.. code]);
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldarg"), fixture.Op("Ret", 1));
        fixture.Fix(patch, 0, "ObjectValue");
        var (report, owner, _) = fixture.Decompile(patch);
        Assert.Empty(report.Problems);
        var result = owner.GetMethod("ObjectValue__m0")!.Invoke(null, null);
        if (secondIsArray) Assert.Equal(2, Assert.IsType<string[]>(result).Length);
        else Assert.IsType<InvalidOperationException>(result);
    }

    [Theory]
    [InlineData(0, 11)]
    [InlineData(1, 22)]
    public void ConditionalReferenceLoadReadsTheSelectedLocal(int condition, int expected)
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch();
        // ref source = ref (condition != 0 ? ref second : ref first); return source.
        // The two addresses join on the evaluation stack before one common indirect load.
        fixture.Method(patch, fixture.Op("StackSpace", (2 << 16) | 2), fixture.Op("Ldc_I4", 11), fixture.Op("Stloc"),
            fixture.Op("Ldc_I4", 22), fixture.Op("Stloc", 1), fixture.Op("Ldarg"), fixture.Op("Brtrue", 3),
            fixture.Op("Ldloca"), fixture.Op("Br", 2), fixture.Op("Ldloca", 1), fixture.Op("Ldobj", 1), fixture.Op("Ret", 1));
        fixture.Fix(patch, 0, "Value", 1);
        var (report, owner, _) = fixture.Decompile(patch);
        Assert.Empty(report.Problems);
        Assert.Equal(expected, owner.GetMethod("Value__m0")!.Invoke(null, [condition]));
    }
}
