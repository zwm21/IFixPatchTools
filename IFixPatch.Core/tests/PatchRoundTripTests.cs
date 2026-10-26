using IFixPatch.Core.IR;

namespace IFixPatch.Core.Tests;

public sealed class PatchRoundTripTests
{
    [Fact]
    public void BinaryAndJsonRoundTripsPreserveSideTablesAndInstructions()
    {
        var patch = PatchTestData.Patch();
        patch.InternStrings.Add("quoted \"text\"\n中文");
        patch.Fields.Add(new() { IsNew = true, DeclaringTypeId = 0, Name = "State", FieldTypeId = 1, CctorId = -1 });
        patch.StaticFields.Add(new() { FieldTypeId = 1, CctorId = -1 });
        patch.ExternMethods.Add(new()
        {
            DeclaringTypeId = 0,
            Name = "Identity",
            IsGenericInstance = true,
            GenericArgTypeIds = [1],
            Params = [new() { IsGeneric = true, GenericName = "!!0" }]
        });
        patch.NewClasses.Add("Example.State");
        var bytes = PatchWriter.WriteToBytes(patch);
        // Independent wire vector: little-endian integers, 7-bit UTF-8 lengths and the
        // fixed upstream Serialize section order. This is not emitted by PatchWriter.
        var expected = Convert.FromHexString(
            "F0DEBC9A78563412194578616D706C652E4272696467652C2053796E746865746963020000001D45" +
            "78616D706C652E43616C63756C61746F722C2053796E7468657469631653797374656D2E496E7433" +
            "322C206D73636F726C6962010000000300000064000000010000006B0000002A0000008000000001" +
            "00000000000000010000000100000000084964656E74697479010000000100000001000000010321" +
            "2130010000001471756F746564202274657874220AE4B8ADE6968701000000010000000005537461" +
            "746501000000FFFFFFFF0100000001000000FFFFFFFF000000001A4578616D706C652E4D616E6167" +
            "65722C2053796E7468657469630B2C2053796E7468657469630100000000000000000556616C7565" +
            "0000000000000000010000000D4578616D706C652E5374617465");
        Assert.Equal(expected, bytes);

        using var stream = new MemoryStream(expected);
        var parsed = new PatchReader(stream).Read();
        Assert.True(parsed.FullyParsed);
        Assert.Equal(0, parsed.TrailingBytes);
        Assert.Equal(bytes, PatchWriter.WriteToBytes(parsed));
        Assert.Equal(42, parsed.Methods[0].Code[1].Operand);
        Assert.Equal("quoted \"text\"\n中文", Assert.Single(parsed.InternStrings));
        Assert.Equal("State", Assert.Single(parsed.Fields).Name);
        Assert.Equal("Identity", Assert.Single(parsed.ExternMethods).Name);

        var document = PatchDocument.FromJson(PatchDocument.FromPatchFile(parsed, PatchTestData.Map()).ToJson());
        Assert.Empty(document.Validate());
        Assert.Equal(bytes, PatchWriter.WriteToBytes(document.ToPatchFile(out _)));
    }

    [Theory]
    [InlineData("0000000000000000")]
    [InlineData("8000000000000000")]
    [InlineData("7FF0000000000000")]
    [InlineData("FFF0000000000000")]
    [InlineData("7FF8000000000001")]
    [InlineData("7FF0000000000001")]
    [InlineData("FFFFFFFFFFFFFFFF")]
    [InlineData("0000000000000001")]
    public void IlRoundTripPreservesFloatingPointPayloadBits(string hexadecimal)
    {
        var bits = Convert.ToUInt64(hexadecimal, 16);
        var patch = PatchTestData.Patch();
        patch.Methods[0].Code = [PatchTestData.Op("StackSpace", 1), PatchTestData.Op("Ldc_R8"),
            new() { Code = unchecked((int)bits), Operand = unchecked((int)(bits >> 32)) }, PatchTestData.Op("Ret", 1)];
        var map = PatchTestData.Map();

        var text = PatchTextAssembly.Disassemble(patch, map);
        var rebuilt = PatchTextAssembly.Assemble(text, patch, map);

        Assert.Contains("bits64=0x" + hexadecimal, text);
        Assert.Equal(PatchWriter.WriteToBytes(patch), PatchWriter.WriteToBytes(rebuilt));
    }

    [Fact]
    public void IlRoundTripPreservesWideConstantsSwitchPaddingAndExceptionBoundaries()
    {
        var patch = PatchTestData.Patch();
        patch.Methods.Add(new()
        {
            Id = 1,
            Code = [
            PatchTestData.Op("StackSpace", 2), PatchTestData.Op("Ldc_I8"), new() { Code = -1, Operand = int.MinValue }, PatchTestData.Op("Pop"),
            PatchTestData.Op("Leave", 9), PatchTestData.Op("Ldc_R8"), new() { Code = int.MaxValue, Operand = -123 },
            PatchTestData.Op("Pop"), PatchTestData.Op("Endfinally"), PatchTestData.Op("Ret")],
            Handlers = [new() { HandlerType = 2, CatchTypeId = -1, TryStart = 1, TryEnd = 5, HandlerStart = 5, HandlerEnd = 9 }]
        });
        patch.Methods.Add(new()
        {
            Id = 2,
            Code = [
            PatchTestData.Op("StackSpace", 1), PatchTestData.Op("Ldc_I4"), PatchTestData.Op("Switch", 3),
            new() { Code = 3, Operand = 5 }, new() { Code = 7, Operand = unchecked((int)0xDEADBEEF) },
            PatchTestData.Op("Ldc_I4", 10), PatchTestData.Op("Br", 5), PatchTestData.Op("Ldc_I4", 20), PatchTestData.Op("Br", 3),
            PatchTestData.Op("Ldc_I4", 30), PatchTestData.Op("Br", 1), PatchTestData.Op("Ret", 1)]
        });
        var map = PatchTestData.Map();
        Assert.Empty(PatchValidation.Validate(patch, map));

        var text = PatchTextAssembly.Disassemble(patch, map);
        Assert.Contains("padding=0xDEADBEEF", text);
        Assert.Equal(PatchWriter.WriteToBytes(patch), PatchWriter.WriteToBytes(PatchTextAssembly.Assemble(text, patch, map)));
    }

    [Fact]
    public void LabelEditsRecalculateBranchDistances()
    {
        var patch = PatchTestData.Patch();
        var rebuilt = PatchTextAssembly.Assemble("""
            .method #0
              StackSpace 1
              Br L10
              Ldc_I4 100
              Pop
            L10:
              Ldc_I4 200
              Ret 1
            """, patch, PatchTestData.Map());

        Assert.Equal(3, rebuilt.Methods[0].Code[1].Operand);
        Assert.Equal(200, rebuilt.Methods[0].Code[4].Operand);
    }

    [Fact]
    public void ExportWithoutOpcodeMapPreservesOpaqueInstructionNumbers()
    {
        var patch = PatchTestData.Patch();
        patch.Methods[0].Code[1] = new() { Code = int.MinValue, Operand = -83 };

        var document = PatchDocument.FromPatchFile(patch, null);

        Assert.Equal("code#-2147483648", document.Methods[0].Code[1].Op);
        Assert.Equal(PatchWriter.WriteToBytes(patch), PatchWriter.WriteToBytes(document.ToPatchFile(out _)));
    }

    [Fact]
    public void ReaderReportsTrailingBytesInsteadOfLosingThemSilently()
    {
        var bytes = PatchWriter.WriteToBytes(PatchTestData.Patch()).Concat(new byte[] { 10, 20 }).ToArray();
        using var stream = new MemoryStream(bytes);

        var patch = new PatchReader(stream).Read();

        Assert.True(patch.FullyParsed);
        Assert.Equal(2, patch.TrailingBytes);
        Assert.Contains(PatchValidation.Validate(patch), message => message.Contains("trailing bytes"));
    }

    [Fact]
    public void ReaderStopsAtInterfaceSlotsWhenMetadataIsUnavailable()
    {
        var patch = PatchTestData.Patch();
        patch.AnonStoreys.Add(new() { CtorId = 0, HasInterfaces = true, Interfaces = [new() { TypeId = 0, MethodIds = [0] }] });
        using var stream = new MemoryStream(PatchWriter.WriteToBytes(patch));

        var partial = new PatchReader(stream).Read();

        Assert.False(partial.FullyParsed);
        Assert.NotNull(partial.ParseNote);
        Assert.Throws<InvalidDataException>(() => PatchValidation.RequireValid(partial));
    }

    [Fact]
    public void ReaderRejectsNegativeTableCounts()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
        { writer.Write(1UL); writer.Write(""); writer.Write(-1); }
        stream.Position = 0;

        Assert.Throws<InvalidDataException>(() => new PatchReader(stream).Read());
    }
}
