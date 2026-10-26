using IFixPatch.Core.IR;
namespace IFixPatch.Core.Tests;

public sealed class TextAssemblyContractTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void TextEditsRequireACompleteBaseWithoutTrailingBytes(bool parsed, int trailing)
    {
        var patch = PatchTestData.Patch();
        patch.FullyParsed = parsed; patch.TrailingBytes = trailing;
        Assert.Throws<InvalidDataException>(() => PatchTextAssembly.Assemble(".method #0\nStackSpace 1\nRet 0", patch, PatchTestData.Map()));
        Assert.Equal(42, patch.Methods[0].Code[1].Operand);
    }

    [Theory]
    [InlineData(".method #0 unexpected\nRet 0")]
    [InlineData(".method #1\nStackSpace 1\nRet 0")]
    [InlineData(".method #0\nStackSpace 1\nRet 0\n.method #0\nStackSpace 1\nRet 0")]
    [InlineData(".method #0\nStackSpace 1\ncode#9999999999999999999\nRet 0")]
    [InlineData(".opcode_map 1\nSpellingMistake = 300\n.method #0\nRet 0")]
    [InlineData(".opcode_map 2\nStackSpace = 0\nRet = 0\n.method #0\nRet 0")]
    public void MalformedOrAmbiguousTextEditsCannotAlterTheOriginal(string text)
    {
        var patch = PatchTestData.Patch();
        Assert.Throws<InvalidDataException>(() => PatchTextAssembly.Assemble(text, patch, new OpCodeMap()));
        Assert.Equal(42, patch.Methods[0].Code[1].Operand);
    }

    [Fact]
    public void MaplessTextRoundTripPreservesRawOpcodeNumbersAndOperands()
    {
        const string text = ".method #0\ncode#100 1\ncode#107 42\ncode#128 1";
        var result = PatchTextAssembly.Assemble(text, PatchTestData.Patch(), new OpCodeMap());
        Assert.Equal(new[] { (100, 1), (107, 42), (128, 1) },
            result.Methods[0].Code.Select(instruction => (instruction.Code, instruction.Operand)));
    }

    [Fact]
    public void AuthoredHexadecimalConstantsKeepTheirNumericMeaning()
    {
        var result = PatchTextAssembly.Assemble(".method #0\nStackSpace 1\nLdc_I4 0x2A\nRet 1", PatchTestData.Patch(), PatchTestData.Map());
        Assert.Equal(42, result.Methods[0].Code[1].Operand);
    }

    [Fact]
    public void ABranchAnnotationCanSupplyTheDestinationOfAnAuthoredNumericOperand()
    {
        var result = PatchTextAssembly.Assemble(".method #0\nStackSpace 1\nBr 0 ; -> L0002\nL0002:\nRet 0", PatchTestData.Patch(), PatchTestData.Map());
        Assert.Equal(1, result.Methods[0].Code[1].Operand);
    }

    [Theory]
    [InlineData("finally", 2)]
    [InlineData("fault", 4)]
    public void NumericExceptionBoundsAndOpenHandlerEndsPreserveTheEncodedRegion(string kind, int encoding)
    {
        var text = $".method #0\n.eh {kind} try[1..3) handler[4..-1)\nStackSpace 1\nNop\nLeave 3\nRet 0\nEndfinally";
        var result = PatchTextAssembly.Assemble(text, PatchTestData.Patch(), PatchTestData.Map());
        var handler = Assert.Single(result.Methods[0].Handlers);
        Assert.Equal(encoding, handler.HandlerType);
        Assert.Equal((1, 3, 4, -1), (handler.TryStart, handler.TryEnd, handler.HandlerStart, handler.HandlerEnd));
    }

    [Fact]
    public void FilterHandlerMetadataSurvivesTextAndBinaryRoundTrips()
    {
        // Filter flags are part of the stored format; this does not claim VM filter execution.
        const string text = ".method #0\n.eh filter try[L0001..L0003) handler[L0003..L0005)\nStackSpace 1\nL0001:\nNop\nLeave L0005\nL0003:\nPop\nLeave L0005\nL0005:\nRet 0";
        var patch = PatchTextAssembly.Assemble(text, PatchTestData.Patch(), PatchTestData.Map());
        var expected = (Kind: 1, CatchType: -1, TryStart: 1, TryEnd: 3, HandlerStart: 3, HandlerEnd: 5);
        var handler = Assert.Single(patch.Methods[0].Handlers);
        Assert.Equal(expected, (handler.HandlerType, handler.CatchTypeId, handler.TryStart, handler.TryEnd, handler.HandlerStart, handler.HandlerEnd));

        var rendered = PatchTextAssembly.Disassemble(patch, PatchTestData.Map());
        Assert.Contains(".eh filter", rendered);
        var assembled = PatchTextAssembly.Assemble(rendered, patch, PatchTestData.Map());
        using var stream = new MemoryStream(PatchWriter.WriteToBytes(assembled));
        var read = new PatchReader(stream).Read();

        Assert.True(read.FullyParsed);
        Assert.Equal(0, read.TrailingBytes);
        var roundTripped = Assert.Single(read.Methods[0].Handlers);
        Assert.Equal(expected, (roundTripped.HandlerType, roundTripped.CatchTypeId, roundTripped.TryStart, roundTripped.TryEnd, roundTripped.HandlerStart, roundTripped.HandlerEnd));
    }

    [Fact]
    public void ZeroCaseSwitchNeedsNoBorrowedOffsetsOrTargetAnnotation()
    {
        var result = PatchTextAssembly.Assemble(".method #0\nStackSpace 1\nLdc_I4 0\nSwitch 0\nRet 0", PatchTestData.Patch(), PatchTestData.Map());
        Assert.Equal(4, result.Methods[0].Code.Count);
        Assert.Equal(0, result.Methods[0].Code[2].Operand);
    }

    [Fact]
    public void CatchTypeLookupFindsTheOuterAssemblySeparatorAfterGenericArgumentAssemblies()
    {
        var patch = PatchTestData.Patch();
        patch.ExternTypes.Add("Example.Error`1[[System.Int32, mscorlib]], Synthetic");
        patch.ExternTypes.Add("Example.Unqualified");
        var text = ".method #0\n.eh catch try[1..3) handler[3..5) catch Example.Error`1[[System.Int32, mscorlib]]\nStackSpace 1\nNop\nLeave 5\nPop\nLeave 5\nRet 0";
        var result = PatchTextAssembly.Assemble(text, patch, PatchTestData.Map());
        Assert.Equal(2, Assert.Single(result.Methods[0].Handlers).CatchTypeId);
    }
}
