using IFixPatch.Core.IR;

namespace IFixPatch.Core.Tests;

public sealed class PatchBoundaryTests
{
    [Fact]
    public void JsonAndBinaryFilesPreserveExceptionAndClosureMetadata()
    {
        using var directory = new TemporaryDirectory();
        var patch = PatchTestData.Patch();
        patch.Methods[0].Code = [PatchTestData.Op("StackSpace"), PatchTestData.Op("Nop"), PatchTestData.Op("Leave", 5), PatchTestData.Op("Nop"), PatchTestData.Op("Endfinally"), PatchTestData.Op("Ret")];
        patch.Methods[0].Handlers.Add(new Eh { HandlerType = 2, CatchTypeId = -1, TryStart = 1, TryEnd = 3, HandlerStart = 3, HandlerEnd = 5 });
        patch.AnonStoreys.Add(new AnonStorey
        {
            FieldTypes = [0, -2, 2],
            CtorId = 0,
            CtorParamNum = 0,
            HasInterfaces = true,
            Interfaces = [new() { TypeId = 0, MethodIds = [0] }],
            VTable = [-1, 0]
        });
        patch.ExternMethods.Add(new ExternMethodSig
        {
            DeclaringTypeId = 0,
            Name = "Generic",
            IsGenericInstance = true,
            GenericArgTypeIds = [1],
            Params = [new() { TypeId = 1 }]
        });
        patch.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 0, Name = "Plain", Params = [new() { TypeId = 1 }] });
        PatchDocument.FromPatchFile(patch, PatchTestData.Map()).SaveJson(directory.File("patch.ir.json"));

        var restored = PatchDocument.LoadJson(directory.File("patch.ir.json")).ToPatchFile(out var map);

        Assert.Empty(PatchValidation.Validate(restored, map));
        Assert.Equal(new[] { 0, -2, 2 }, Assert.Single(restored.AnonStoreys).FieldTypes);
        Assert.True(restored.AnonStoreys[0].HasInterfaces);
        Assert.Equal(new[] { 0 }, Assert.Single(restored.AnonStoreys[0].Interfaces).MethodIds);
        Assert.Equal(new[] { -1, 0 }, restored.AnonStoreys[0].VTable);
        var handler = Assert.Single(restored.Methods[0].Handlers);
        Assert.Equal(2, handler.HandlerType);
        Assert.Equal((1, 3, 3, 5), (handler.TryStart, handler.TryEnd, handler.HandlerStart, handler.HandlerEnd));
        PatchWriter.WriteFile(directory.File("patch.bytes"), restored);
        File.WriteAllText(directory.File("dump.cs"), "// Namespace: Example\npublic interface Calculator\n{\n // Methods\n public abstract void Run();\n}\n");

        var decoded = PatchReader.ReadFile(directory.File("patch.bytes"), new DumpCsSource(directory.File("dump.cs")));

        Assert.True(decoded.FullyParsed);
        Assert.Equal(0, decoded.TrailingBytes);
        Assert.Equal(2, Assert.Single(decoded.Methods[0].Handlers).HandlerType);
        Assert.Equal(new[] { 0 }, Assert.Single(decoded.AnonStoreys[0].Interfaces).MethodIds);
        Assert.Equal(1, Assert.Single(decoded.ExternMethods[0].Params).TypeId);
        Assert.False(decoded.ExternMethods[0].Params[0].IsGeneric);
        Assert.Equal(1, Assert.Single(decoded.ExternMethods[1].Params).TypeId);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("negative-code")]
    [InlineData("conflicting-code")]
    [InlineData("unknown-code")]
    public void IrValidationRejectsUnsupportedVersionAndAmbiguousOpcodeIdentity(string defect)
    {
        var document = PatchDocument.FromPatchFile(PatchTestData.Patch(), PatchTestData.Map());
        switch (defect)
        {
            case "version": document.Version = -1; break;
            case "negative-code": document.OpCodeMap["Nop"] = -1; break;
            case "conflicting-code": document.OpCodeMap["Nop"] = document.OpCodeMap["Ret"]; break;
            case "unknown-code": document.OpCodeMap["SpellingMistake"] = 123456; break;
        }

        Assert.NotEmpty(document.Validate());
        Assert.Throws<InvalidDataException>(() => document.ToPatchFile(out _));
    }

    [Theory]
    [InlineData("extern-type")]
    [InlineData("method-name")]
    [InlineData("nongeneric-args")]
    [InlineData("invalid-generic-param")]
    [InlineData("field-name")]
    [InlineData("constructor-args")]
    [InlineData("storey-field")]
    [InlineData("new-class")]
    [InlineData("missing-body")]
    public void InvalidSideTableEntriesAreReported(string defect)
    {
        var patch = PatchTestData.Patch();
        switch (defect)
        {
            case "extern-type": patch.ExternTypes[1] = " "; break;
            case "method-name": patch.FixList[0].Method.Name = " "; break;
            case "nongeneric-args": patch.FixList[0].Method.GenericArgTypeIds.Add(1); break;
            case "invalid-generic-param": patch.FixList[0].Method.Params.Add(new() { IsGeneric = true, GenericName = "" }); break;
            case "field-name": patch.Fields.Add(new() { DeclaringTypeId = 0, Name = "" }); break;
            case "constructor-args": patch.AnonStoreys.Add(new() { CtorId = 0, CtorParamNum = -1 }); break;
            case "storey-field": patch.AnonStoreys.Add(new() { CtorId = 0, FieldTypes = [-3] }); break;
            case "new-class": patch.NewClasses.Add(""); break;
            case "missing-body": patch.Methods[0].Code.Clear(); break;
        }

        Assert.NotEmpty(PatchValidation.Validate(patch, PatchTestData.Map()));
        Assert.Throws<InvalidDataException>(() => PatchValidation.RequireValid(patch, PatchTestData.Map()));
    }
    [Theory]
    [InlineData("method-id", "dense")]
    [InlineData("opcode", "unknown opcode")]
    [InlineData("preamble", "StackSpace")]
    [InlineData("truncated-wide", "borrowed slots")]
    [InlineData("handler-kind", "handler kind")]
    [InlineData("handler-order", "inverted exception region")]
    [InlineData("catch-type", "catch type")]
    public void InvalidInstructionIdentityAndExceptionMetadataAreRejected(string defect, string diagnostic)
    {
        var patch = PatchTestData.Patch();
        switch (defect)
        {
            case "method-id": patch.Methods[0].Id = 7; break;
            case "opcode": patch.Methods[0].Code[1] = new Instr { Code = int.MaxValue }; break;
            case "preamble": patch.Methods[0].Code.RemoveAt(0); break;
            case "truncated-wide": patch.Methods[0].Code = [PatchTestData.Op("StackSpace"), PatchTestData.Op("Ldc_I8")]; break;
            default:
                patch.Methods[0].Handlers.Add(new Eh
                {
                    HandlerType = defect == "handler-kind" ? 9 : 0,
                    CatchTypeId = defect == "catch-type" ? 99 : -1,
                    TryStart = 0,
                    TryEnd = defect == "handler-order" ? 0 : 1,
                    HandlerStart = 1,
                    HandlerEnd = 3
                });
                break;
        }

        Assert.Contains(PatchValidation.Validate(patch, PatchTestData.Map()), error => error.Contains(diagnostic));
    }

    [Fact]
    public void MethodTablesMustFitTheSixteenBitCallIndex()
    {
        var patch = PatchTestData.Patch();
        for (var id = 1; id <= 65536; id++) patch.Methods.Add(new IFixMethod { Id = id });

        Assert.Contains(PatchValidation.Validate(patch), error => error.Contains("16-bit call index limit"));
    }

}
