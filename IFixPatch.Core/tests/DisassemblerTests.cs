namespace IFixPatch.Core.Tests;

public sealed class DisassemblerTests
{
    [Fact]
    public void StringReferencesSkipBorrowedWideConstantAndSwitchSlots()
    {
        var patch = new PatchFile
        {
            InternStrings = ["first", "second"],
            Methods = [new(), new()
            {
                Code = [Op("Ldc_I8"), new() { Code = 4, Operand = 0 },
                    Op("Switch", 2), new() { Code = 4, Operand = 4 },
                    Op("Ldstr", 0), Op("Ldstr", 0), Op("Ldstr", 1), Op("Ldstr", -1), Op("Ldstr", 9)]
            }]
        };

        Assert.Equal(new[] { 2, 1 }, new Disassembler(patch, Map()).CountStringUses());
    }

    [Theory]
    [InlineData("Ldfld", "storeyField[2]")]
    [InlineData("Stfld", "storeyField[2]")]
    [InlineData("Ldflda", "storeyField[2]")]
    [InlineData("Ldsfld", "vmStaticField[2]")]
    [InlineData("Stsfld", "vmStaticField[2]")]
    [InlineData("Ldsflda", "vmStaticField[2]")]
    public void NegativeFieldOperandDistinguishesClosureAndGlobalStaticStorage(string mnemonic, string annotation)
    {
        var method = new IFixMethod { Code = [Op(mnemonic, -3)] };
        var text = new Disassembler(new PatchFile(), Map()).Format(method);

        Assert.Contains(annotation, text);
    }

    [Fact]
    public void AnnotationsIdentifyStackLayoutMetadataAndConstants()
    {
        var patch = new PatchFile
        {
            ExternTypes = ["Example.Owner, Synthetic", "System.Int32, mscorlib"],
            InternStrings = ["a\\b\"c\r\nd"],
            Fields = [new() { DeclaringTypeId = 0, Name = "Count", IsNew = true }]
        };
        var method = new IFixMethod
        {
            Code = [Op("StackSpace", (3 << 16) | 7), Op("Ldstr", 0), Op("Ldstr", 9),
                Op("Ldc_R4", 0x3FC00000), Op("Ldfld", 0), Op("Ldtype", 1), Op("Box", -2), Op("Newanon", 5)]
        };

        var text = new Disassembler(patch, Map()).Format(method);

        Assert.Contains("locals=3, maxStack=7", text);
        Assert.Contains("\"a\\\\b\\\"c\\r\\nd\"", text);
        Assert.Contains("str#9", text);
        Assert.Contains("= 1.5", text);
        Assert.Contains("Example.Owner.Count (new)", text);
        Assert.Contains("System.Int32", text);
        Assert.DoesNotContain("mscorlib", text);
        Assert.Contains("type#-2", text);
        Assert.Contains("anonStorey#5", text);
    }

    [Fact]
    public void CallAnnotationsDecodeArgumentCountAndSeparateExternVmAndVirtualTargets()
    {
        var patch = new PatchFile
        {
            ExternTypes = ["Example.Owner, Synthetic", "System.Int32, mscorlib"],
            ExternMethods = [new()
            {
                DeclaringTypeId = 0, Name = "Read", IsGenericInstance = true, GenericArgTypeIds = [1],
                Params = [new() { TypeId = 1 }, new() { IsGeneric = true, GenericName = "!!0" }, new() { IsGeneric = true }]
            }]
        };
        var method = new IFixMethod
        {
            Code = [Op("CallExtern", 2 << 16), Op("Newobj", (1 << 16) | 9),
                Op("Call", (3 << 16) | 7), Op("Callvirtvirt", (4 << 16) | 6)]
        };
        var disassembler = new Disassembler(patch, Map());

        var text = disassembler.Format(method);

        Assert.Contains("argc=2, extern#0 = Example.Owner::Read<System.Int32>(System.Int32, !!0, T)", text);
        Assert.Contains("argc=1, extern#9 = <externMethod#9>", text);
        Assert.Contains("argc=3, method#7", text);
        Assert.Contains("argc=4, vtable[6]", text);
        Assert.Equal("<externMethod#-1>", disassembler.FormatExtern(-1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32767)]
    [InlineData(32768)]
    [InlineData(65535)]
    public void PackedCallArgumentCountUsesAllSixteenUnsignedBits(int count)
    {
        var method = new IFixMethod { Code = [Op("Call", (count << 16) | 7)] };
        var text = new Disassembler(new PatchFile(), Map()).Format(method);
        Assert.Contains($"argc={count}, method#7", text);
    }

    [Theory]
    [InlineData("Ldfld")]
    [InlineData("Stfld")]
    [InlineData("Ldflda")]
    [InlineData("Ldsfld")]
    [InlineData("Stsfld")]
    [InlineData("Ldsflda")]
    public void UnknownPositiveFieldIndexesAreNotDisplayedAsAnonymousOrStaticStorage(string operation)
    {
        var method = new IFixMethod { Code = [Op(operation, 99)] };
        var text = new Disassembler(new PatchFile(), Map()).Format(method);
        Assert.Contains("field#99", text);
        Assert.DoesNotContain("storeyField", text);
        Assert.DoesNotContain("vmStaticField", text);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-3)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    [InlineData(1)]
    [InlineData(3)]
    public void InvalidSwitchLengthsAreRejectedWithoutReadingBorrowedDataAsInstructions(int count)
    {
        var method = new IFixMethod { Code = [Op("Switch", count)] };
        var patch = new PatchFile { Methods = [method], InternStrings = ["unused"] };
        var disassembler = new Disassembler(patch, Map());

        Assert.Contains("Switch", Assert.Throws<InvalidDataException>(() => disassembler.Format(method)).Message);
        Assert.Contains("Switch", Assert.Throws<InvalidDataException>(() => disassembler.CountStringUses()).Message);
    }

    [Fact]
    public void GenericTypeAnnotationKeepsArgumentAssemblyCommasInsideBrackets()
    {
        var patch = new PatchFile { ExternTypes = ["Example.Box`1[[System.Int32, mscorlib]], Synthetic", "Plain.Type"] };
        var method = new IFixMethod { Code = [Op("Ldtype", 0), Op("Ldtype", 1)] };

        var text = new Disassembler(patch, Map()).Format(method);

        Assert.Contains("Example.Box`1[[System.Int32, mscorlib]]", text);
        Assert.DoesNotContain(", Synthetic", text);
        Assert.Contains("Plain.Type", text);
    }

    [Theory]
    [InlineData(0, "catch", " catch object")]
    [InlineData(1, "filter", "")]
    [InlineData(2, "finally", "")]
    [InlineData(4, "fault", "")]
    [InlineData(9, "type9", "")]
    public void ExceptionRegionsArePrintedWithExclusiveBoundariesAndHandlerKind(int kind, string name, string suffix)
    {
        var method = new IFixMethod
        {
            Code = [Op("Nop"), Op("Ret")],
            Handlers = [new() { HandlerType = kind, CatchTypeId = -1, TryStart = 0, TryEnd = 1, HandlerStart = 1, HandlerEnd = 2 }]
        };

        var text = new Disassembler(new PatchFile(), Map()).Format(method);

        Assert.Contains($".eh {name} try[L0000..L0001) handler[L0001..L0002){suffix}", text);
        Assert.Contains("L0000:", text);
        Assert.Contains("L0001:", text);
        Assert.EndsWith("  L0002:\n", text);
    }

    [Fact]
    public void MissingExceptionBoundariesRemainSentinelsAndTypedCatchUsesTypeIndex()
    {
        var method = new IFixMethod
        {
            Code = [Op("Ret")],
            Handlers = [new() { HandlerType = 0, CatchTypeId = 3, TryStart = -1, TryEnd = -1, HandlerStart = -1, HandlerEnd = -1 }]
        };
        var text = new Disassembler(new PatchFile(), Map()).Format(method);

        Assert.Contains("try[-1..-1) handler[-1..-1) catch type#3", text);
        Assert.DoesNotContain("L-001:", text);
    }

    [Fact]
    public void RelativeBranchesAndAbsoluteLeavePointToDifferentCoordinates()
    {
        var method = new IFixMethod { Code = [Op("Nop"), Op("Br", 2), Op("Leave", 3), Op("Ret")] };

        var text = new Disassembler(new PatchFile(), Map()).Format(method);

        Assert.Contains("→ L0003\n", text);
        Assert.Contains("→ L0003 (abs)", text);
        Assert.Contains("L0003:", text);
    }

    [Fact]
    public void UnknownOpcodesAndTruncatedWideConstantStillProduceInspectableOutput()
    {
        var method = new IFixMethod { Code = [new() { Code = 12345, Operand = -7 }, Op("Ldc_I8")] };

        var text = new Disassembler(new PatchFile(), Map()).Format(method);

        Assert.Contains("code#12345", text);
        Assert.Contains("-7", text);
        Assert.Contains("Ldc_I8", text);
        Assert.DoesNotContain("bits64", text);
    }

    private static readonly string[] Names = ["Nop", "Ldc_I8", "Switch", "Ldstr", "Ret", "Ldfld", "Stfld", "Ldflda", "Ldsfld", "Stsfld", "Ldsflda", "StackSpace", "Ldc_R4", "Ldtype", "Box", "Newanon", "CallExtern", "Newobj", "Call", "Callvirtvirt", "Leave", "Br"];
    private static OpCodeMap Map() => OpCodeMap.FromPairs(Names.Select((name, index) => (index + 1, name)));
    private static Instr Op(string name, int operand = 0) => new() { Code = Array.IndexOf(Names, name) + 1, Operand = operand };

    [Fact]
    public void ExistingFieldAnnotationDoesNotDescribeTheFieldAsNew()
    {
        var patch = PatchTestData.Patch();
        patch.Fields.Add(new() { DeclaringTypeId = 0, Name = "Existing", IsNew = false });
        var method = new IFixMethod { Code = [PatchTestData.Op("Ldfld", 0)] };
        var text = new Disassembler(patch, PatchTestData.Map()).Format(method);
        Assert.Contains("Example.Calculator.Existing", text);
        Assert.DoesNotContain("(new)", text);
    }
}
