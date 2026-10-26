using IFixPatch.Core.IR;

namespace IFixPatch.Core.Tests;

public sealed class PatchValidationTests
{
    [Theory]
    [InlineData("{\"methods\":[],\"methods\":[]}")]
    [InlineData("{\"methods\":[null]}")]
    [InlineData("{\"methods\":[],\"externTypes\":[null]}")]
    public void JsonRejectsDuplicatePropertiesAndNullArrayEntries(string json) =>
        Assert.Throws<InvalidDataException>(() => PatchDocument.FromJson(json));

    [Fact]
    public void IrRejectsUnknownMnemonicsInsteadOfEncodingZero()
    {
        var document = PatchDocument.FromPatchFile(PatchTestData.Patch(), PatchTestData.Map());
        document.Methods[0].Code[1].Op = "Ldc_I44";

        Assert.Contains(document.Validate(), message => message.Contains("unknown instruction Ldc_I44"));
        Assert.Throws<InvalidDataException>(() => document.ToPatchFile(out _));
    }

    [Fact]
    public void IrRejectsDuplicateMethodIds()
    {
        var document = PatchDocument.FromPatchFile(PatchTestData.Patch(), PatchTestData.Map());
        document.Methods.Add(new() { Id = 0 });

        Assert.Contains(document.Validate(), message => message.Contains("unique and dense"));
    }

    [Theory]
    [InlineData("Ldstr", 0)]
    [InlineData("Call", 99)]
    [InlineData("CallExtern", 0)]
    [InlineData("Ldfld", 0)]
    [InlineData("Ldsfld", -1)]
    [InlineData("Newanon", 0)]
    [InlineData("Ldloc", 0)]
    public void InvalidInstructionReferencesAreRejected(string opcode, int operand)
    {
        var patch = PatchTestData.Patch();
        patch.Methods[0].Code[1] = PatchTestData.Op(opcode, operand);

        Assert.Contains(PatchValidation.Validate(patch, PatchTestData.Map()), error => error.Contains(opcode, StringComparison.Ordinal) && error.Contains("index", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => PatchValidation.RequireValid(patch, PatchTestData.Map()));
    }

    [Fact]
    public void FixDeclaringTypeAndDuplicateBindingsAreValidated()
    {
        var patch = PatchTestData.Patch();
        patch.FixList[0].Method.DeclaringTypeId = 99;
        Assert.Contains(PatchValidation.Validate(patch), message => message.Contains("fixList declaring type"));
        patch.FixList[0].Method.DeclaringTypeId = 0;
        patch.FixList.Add(new() { MethodId = 0, Method = patch.FixList[0].Method });

        Assert.Contains(PatchValidation.Validate(patch), message => message.Contains("Duplicate fixList binding"));
    }

    [Fact]
    public void BranchCannotEnterBorrowedDataSlot()
    {
        var patch = PatchTestData.Patch();
        patch.Methods[0].Code = [PatchTestData.Op("StackSpace", 2), PatchTestData.Op("Br", 2),
            PatchTestData.Op("Ldc_I8"), new() { Code = 1234, Operand = 5678 }, PatchTestData.Op("Ret", 1)];

        Assert.Contains(PatchValidation.Validate(patch, PatchTestData.Map()), message => message.Contains("not an instruction boundary"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void InvalidSwitchBorrowedSlotCountsAreRejected(int count)
    {
        var patch = PatchTestData.Patch();
        patch.Methods[0].Code = [PatchTestData.Op("StackSpace", 1), PatchTestData.Op("Switch", count)];

        Assert.Contains(PatchValidation.Validate(patch, PatchTestData.Map()), message => message.Contains("invalid Switch"));
    }

    [Fact]
    public void ExceptionRegionsCannotStartInsideBorrowedData()
    {
        var patch = PatchTestData.Patch();
        patch.Methods[0].Code = [PatchTestData.Op("StackSpace", 1), PatchTestData.Op("Ldc_I8"),
            new() { Code = 0, Operand = 0 }, PatchTestData.Op("Pop"), PatchTestData.Op("Ret")];
        patch.Methods[0].Handlers.Add(new() { HandlerType = 2, CatchTypeId = -1, TryStart = 2, TryEnd = 3, HandlerStart = 3, HandlerEnd = 5 });

        Assert.Contains(PatchValidation.Validate(patch, PatchTestData.Map()), message => message.Contains("try start"));
    }

    [Fact]
    public void IlRejectsReadonlySideTableEdits()
    {
        var patch = PatchTestData.Patch();
        var map = PatchTestData.Map();
        var text = PatchTextAssembly.Disassemble(patch, map).Replace("Example.Calculator", "Example.Other", StringComparison.Ordinal);

        var error = Assert.Throws<InvalidDataException>(() => PatchTextAssembly.Assemble(text, patch, map));
        Assert.Contains("read-only", error.Message);
    }

    [Theory]
    [InlineData(".unknown 1\n")]
    [InlineData("unexpected text\n")]
    public void IlRejectsUnknownPreamble(string prefix)
    {
        var patch = PatchTestData.Patch();
        var map = PatchTestData.Map();

        Assert.Throws<InvalidDataException>(() => PatchTextAssembly.Assemble(prefix + PatchTextAssembly.Disassemble(patch, map), patch, map));
    }

    [Fact]
    public void IlRejectsUndefinedBranchLabels()
    {
        var patch = PatchTestData.Patch();

        Assert.Throws<InvalidDataException>(() => PatchTextAssembly.Assemble(".method #0\nStackSpace 1\nBr L999\nRet", patch, PatchTestData.Map()));
    }

    [Fact]
    public void ValidatedWritePreservesExistingOutputOnInvalidPatch()
    {
        using var directory = new TemporaryDirectory();
        var output = directory.File("patch.bytes");
        var expected = new byte[] { 15, 16, 17 };
        File.WriteAllBytes(output, expected);
        var patch = PatchTestData.Patch();
        patch.Methods[0].Code[1] = PatchTestData.Op("Br", 1000);

        Assert.Throws<InvalidDataException>(() => PatchWriter.WriteValidatedFile(output, patch, PatchTestData.Map()));
        Assert.Equal(expected, File.ReadAllBytes(output));
        Assert.Equal(new[] { output }, Directory.GetFiles(directory.Path));
    }

    [Fact]
    public void ValidatedWriteReplacesOutputAndCleansTemporaryFile()
    {
        using var directory = new TemporaryDirectory();
        var output = directory.File("patch.bytes");
        File.WriteAllText(output, "old output");
        var patch = PatchTestData.Patch();

        PatchWriter.WriteValidatedFile(output, patch, PatchTestData.Map());

        Assert.Equal(PatchWriter.WriteToBytes(patch), File.ReadAllBytes(output));
        Assert.Equal(new[] { output }, Directory.GetFiles(directory.Path));
    }

    [Fact]
    public void InternalSignaturesUseReturnFlagsAndVirtualTargets()
    {
        var patch = PatchTestData.Patch();
        patch.Methods[0].Code = [PatchTestData.Op("StackSpace", 3), PatchTestData.Op("Ldarg"), PatchTestData.Op("Ldarg", 1),
            PatchTestData.Op("Ldarg", 2), PatchTestData.Op("Callvirtvirt", (3 << 16) | 0), PatchTestData.Op("Pop"), PatchTestData.Op("Ret")];
        patch.Methods.Add(new() { Id = 1, Code = [PatchTestData.Op("StackSpace", 1), PatchTestData.Op("Ldc_I4", 7), PatchTestData.Op("Ret", 1)] });
        patch.Methods.Add(new()
        {
            Id = 2,
            Code = [PatchTestData.Op("StackSpace", 1), PatchTestData.Op("Ldc_I8"),
            new() { Code = PatchTestData.Op("Ret").Code, Operand = 1 }, PatchTestData.Op("Pop"), PatchTestData.Op("Ret")]
        });
        patch.Methods.Add(new() { Id = 3, Code = [PatchTestData.Op("StackSpace", 1), PatchTestData.Op("Ret")] });
        patch.AnonStoreys.Add(new() { CtorId = 3, VTable = [1] });

        var signatures = new InternalSignatures(patch, PatchTestData.Map());

        Assert.False(signatures.ReturnsValue[0]);
        Assert.True(signatures.ReturnsValue[1]);
        Assert.False(signatures.ReturnsValue[2]);
        Assert.Equal(3, signatures.ArgCount[1]);
        Assert.Equal(new[] { 1 }, PatchDispatch.Targets(patch, 0));
    }
}
