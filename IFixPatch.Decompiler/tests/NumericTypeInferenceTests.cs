using IFixPatch.Core;
using Mono.Cecil;

namespace IFixPatch.Decompiler.Tests;

public sealed class NumericTypeInferenceTests
{
    // CLR evaluation-stack I4 accepts these narrower integer return types. The two calls
    // deliberately arrive in both orders; an object fallback would make the generated IL invalid.
    [Theory]
    [InlineData("ToBoolean", false, 1, 1, 2)]
    [InlineData("ToBoolean", true, 1, 1, 2)]
    [InlineData("ToChar", false, 7, 35, 42)]
    [InlineData("ToChar", true, 7, 35, 42)]
    [InlineData("ToSByte", false, -7, 49, 42)]
    [InlineData("ToSByte", true, -7, 49, 42)]
    [InlineData("ToByte", false, 7, 35, 42)]
    [InlineData("ToByte", true, 7, 35, 42)]
    [InlineData("ToInt16", false, -7, 49, 42)]
    [InlineData("ToInt16", true, -7, 49, 42)]
    [InlineData("ToUInt16", false, 7, 35, 42)]
    [InlineData("ToUInt16", true, 7, 35, 42)]
    [InlineData("ToUInt32", false, 7, 35, 42)]
    [InlineData("ToUInt32", true, 7, 35, 42)]
    public void NarrowIntegerAndLiteralCallsKeepExecutableStackTypes(string conversion, bool literalFirst, int converted, int literal, int expected)
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch();
        patch.ExternTypes.Add("System.Convert, mscorlib");
        patch.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 5, Name = conversion, Params = [new() { TypeId = 1 }] });
        var convertedCode = new[] { fixture.Op("Ldc_I4", converted), fixture.Op("CallExtern", 1 << 16), fixture.Op("Call", (1 << 16) | 1) };
        var literalCode = new[] { fixture.Op("Ldc_I4", literal), fixture.Op("Call", (1 << 16) | 1) };
        var code = new List<Instr> { fixture.Op("StackSpace", 2) };
        code.AddRange(literalFirst ? literalCode : convertedCode);
        code.AddRange(literalFirst ? convertedCode : literalCode);
        code.AddRange([fixture.Op("Add"), fixture.Op("Ret", 1)]);
        fixture.Method(patch, [.. code]);
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldarg"), fixture.Op("Ret", 1));
        fixture.Fix(patch, 0, "ReadA");
        var (report, owner, _) = fixture.Decompile(patch);
        Assert.Empty(report.Problems);
        Assert.Equal(expected, owner.GetMethod("ReadA__m0")!.Invoke(null, null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DistinctConcreteIntegerWidthsShareTheI4StackWithoutBoxing(bool byteFirst)
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch();
        patch.ExternTypes.Add("System.Convert, mscorlib");
        patch.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 5, Name = "ToByte", Params = [new() { TypeId = 1 }] });
        patch.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 5, Name = "ToInt16", Params = [new() { TypeId = 1 }] });
        var byteCode = new[] { fixture.Op("Ldc_I4", 7), fixture.Op("CallExtern", 1 << 16), fixture.Op("Call", (1 << 16) | 1) };
        var shortCode = new[] { fixture.Op("Ldc_I4", 35), fixture.Op("CallExtern", (1 << 16) | 1), fixture.Op("Call", (1 << 16) | 1) };
        var code = new List<Instr> { fixture.Op("StackSpace", 2) };
        code.AddRange(byteFirst ? byteCode : shortCode); code.AddRange(byteFirst ? shortCode : byteCode);
        code.AddRange([fixture.Op("Add"), fixture.Op("Ret", 1)]);
        fixture.Method(patch, [.. code]);
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldarg"), fixture.Op("Ret", 1));
        fixture.Fix(patch, 0, "ReadA");
        var (report, owner, _) = fixture.Decompile(patch);
        Assert.Empty(report.Problems);
        Assert.Equal(42, owner.GetMethod("ReadA__m0")!.Invoke(null, null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnsignedAndSignedI8CallsPreserveSmallValues(bool literalFirst)
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch();
        patch.ExternTypes.Add("System.Convert, mscorlib");
        patch.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 5, Name = "ToUInt64", Params = [new() { TypeId = 1 }] });
        var converted = new[] { fixture.Op("Ldc_I4", 7), fixture.Op("CallExtern", 1 << 16), fixture.Op("Call", (1 << 16) | 1) };
        var literal = new[] { fixture.Op("Ldc_I8"), new Instr { Code = 35, Operand = 0 }, fixture.Op("Call", (1 << 16) | 1) };
        var code = new List<Instr> { fixture.Op("StackSpace", 2) };
        code.AddRange(literalFirst ? literal : converted); code.AddRange(literalFirst ? converted : literal);
        code.AddRange([fixture.Op("Add"), fixture.Op("Ret", 1)]);
        fixture.Method(patch, [.. code]);
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldarg"), fixture.Op("Ret", 1));
        fixture.Fix(patch, 0, "LongValue");
        var (report, owner, _) = fixture.Decompile(patch);
        Assert.Empty(report.Problems);
        Assert.Equal(42L, owner.GetMethod("LongValue__m0")!.Invoke(null, null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SingleAndDoubleCallsShareTheFloatingEvaluationStack(bool singleFirst)
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch();
        var single = new[] { fixture.Op("Ldc_R4", BitConverter.SingleToInt32Bits(1.25f)), fixture.Op("Call", (1 << 16) | 1) };
        var bits = BitConverter.DoubleToInt64Bits(2.5);
        var wide = new[] { fixture.Op("Ldc_R8"), new Instr { Code = unchecked((int)bits), Operand = (int)(bits >> 32) }, fixture.Op("Call", (1 << 16) | 1) };
        var code = new List<Instr> { fixture.Op("StackSpace", 2) };
        code.AddRange(singleFirst ? single : wide); code.AddRange(singleFirst ? wide : single);
        code.AddRange([fixture.Op("Add"), fixture.Op("Ret", 1)]);
        fixture.Method(patch, [.. code]);
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldarg"), fixture.Op("Ret", 1));
        fixture.Fix(patch, 0, "DoubleValue");
        var (report, owner, _) = fixture.Decompile(patch);
        Assert.Empty(report.Problems);
        Assert.Equal(3.75, owner.GetMethod("DoubleValue__m0")!.Invoke(null, null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnumAndIntegerCallsUseTheEnumUnderlyingI4Stack(bool literalFirst)
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch();
        patch.ExternTypes.AddRange(["System.DayOfWeek, mscorlib", "System.Enum, mscorlib", "System.Type, mscorlib"]);
        patch.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 6, Name = "ToObject", Params = [new() { TypeId = 7 }, new() { TypeId = 1 }] });
        var enumeration = new[] { fixture.Op("Ldtype", 5), fixture.Op("Ldc_I4", 5), fixture.Op("CallExtern", 2 << 16), fixture.Op("Unbox_Any", 5), fixture.Op("Call", (1 << 16) | 1) };
        var literal = new[] { fixture.Op("Ldc_I4", 1), fixture.Op("Call", (1 << 16) | 1) };
        var code = new List<Instr> { fixture.Op("StackSpace", 3) };
        code.AddRange(literalFirst ? literal : enumeration); code.AddRange(literalFirst ? enumeration : literal);
        code.AddRange([fixture.Op("Add"), fixture.Op("Ret", 1)]);
        fixture.Method(patch, [.. code]);
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldarg"), fixture.Op("Ret", 1));
        fixture.Fix(patch, 0, "ReadA");
        var (report, owner, _) = fixture.Decompile(patch);
        Assert.Empty(report.Problems);
        Assert.Equal(6, owner.GetMethod("ReadA__m0")!.Invoke(null, null));
    }
}
