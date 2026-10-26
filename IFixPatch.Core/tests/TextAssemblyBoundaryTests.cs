using IFixPatch.Core.IR;

namespace IFixPatch.Core.Tests;

public sealed class TextAssemblyBoundaryTests
{
    [Theory]
    [InlineData("Ldc_I8", "-9223372036854775808", 0, int.MinValue)]
    [InlineData("Ldc_R8", "-1.5", 0, unchecked((int)0xBFF80000))]
    public void AuthoredWideValuesEncodeTheSpecified64BitConstant(string mnemonic, string value, int lowBits, int highBits)
    {
        var text = $".method #0\nStackSpace 1\n{mnemonic} 0 ; = {value}\nPop\nRet 0\n";

        var patch = PatchTextAssembly.Assemble(text, PatchTestData.Patch(), PatchTestData.Map());

        Assert.Equal(5, patch.Methods[0].Code.Count);
        Assert.Equal(lowBits, patch.Methods[0].Code[2].Code);
        Assert.Equal(highBits, patch.Methods[0].Code[2].Operand);
    }

    [Theory]
    [InlineData("Example.Exception")]
    [InlineData("Example.Exception, Synthetic")]
    [InlineData("type#2")]
    public void CatchTypeNamesAndIndicesReferToTheSameExternalType(string catchType)
    {
        var basePatch = PatchTestData.Patch();
        basePatch.ExternTypes.Add("Example.Exception, Synthetic");
        var text = $"""
            .method #0
            .eh catch try[L0001..L0003) handler[L0003..L0005) catch {catchType}
            StackSpace 1
            L0001:
            Nop
            Leave L0005
            L0003:
            Pop
            Leave L0005
            L0005:
            Ret 0
            """;

        var result = PatchTextAssembly.Assemble(text, basePatch, PatchTestData.Map());

        var handler = Assert.Single(result.Methods[0].Handlers);
        Assert.Equal(0, handler.HandlerType);
        Assert.Equal(2, handler.CatchTypeId);
        Assert.Equal((1, 3, 3, 5), (handler.TryStart, handler.TryEnd, handler.HandlerStart, handler.HandlerEnd));
        Assert.Equal(5, result.Methods[0].Code[2].Operand);
    }

    [Theory]
    [InlineData(".unsupported 1")]
    [InlineData(".opcode_map -1")]
    [InlineData(".opcode_map 1\ninvalid mapping")]
    [InlineData(".opcode_map 2\nNop = 1\nNop = 2")]
    [InlineData(".opcode_map 1\nRet = 100")]
    [InlineData(".meta bad\n.meta bad")]
    [InlineData("unexpected preamble")]
    [InlineData(".method #0\n???")]
    [InlineData(".method #0\nL0001:\nL0001:\nRet 0")]
    [InlineData(".method #0\nStackSpace 1\nLdc_I8 0\nRet 0")]
    [InlineData(".method #0\nStackSpace 1\nSwitch -1\nRet 0")]
    [InlineData(".method #0\nStackSpace 1\nSwitch 1 ; -> []\nRet 0")]
    [InlineData(".method #0\n.eh invalid region\nRet 0")]
    public void MalformedTextCannotSilentlyReplaceAValidPatch(string text)
    {
        var basePatch = PatchTestData.Patch();

        Assert.Throws<InvalidDataException>(() => PatchTextAssembly.Assemble(text, basePatch, PatchTestData.Map()));
        Assert.Equal(42, basePatch.Methods[0].Code[1].Operand);
        Assert.Equal(3, basePatch.Methods[0].Code.Count);
    }

    [Fact]
    public void CatchTypeMustResolveUnambiguously()
    {
        var basePatch = PatchTestData.Patch();
        basePatch.ExternTypes.Add("Example.Exception, First");
        basePatch.ExternTypes.Add("Example.Exception, Second");
        const string text = ".method #0\n.eh catch try[0..1) handler[1..2) catch Example.Exception\nStackSpace 1\nRet 0";

        Assert.Throws<InvalidDataException>(() => PatchTextAssembly.Assemble(text, basePatch, PatchTestData.Map()));
    }
}
