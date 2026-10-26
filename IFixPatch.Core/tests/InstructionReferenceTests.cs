namespace IFixPatch.Core.Tests;

public sealed class InstructionReferenceTests
{
    // IFix encodes an anonymous type as the bitwise complement of its table index.
    // Castclass and Isinst use that index to inspect anonymous-type compatibility.
    [Theory]
    [InlineData("Castclass", false)]
    [InlineData("Castclass", true)]
    [InlineData("Isinst", false)]
    [InlineData("Isinst", true)]
    public void AnonymousTypeOperandsMustReferToAnExistingAnonymousType(string name, bool present)
    {
        var (patch, map) = WithInstruction(name, -1);
        if (present) patch.AnonStoreys.Add(new() { CtorId = 0 });

        if (present) Assert.Empty(PatchValidation.Validate(patch, map));
        else Assert.Contains(PatchValidation.Validate(patch, map), error => error.Contains(name) && error.Contains("index 0 outside"));
    }

    [Theory]
    [InlineData("Castclass", 1)]
    [InlineData("Castclass", 2)]
    [InlineData("Isinst", 1)]
    [InlineData("Isinst", 2)]
    [InlineData("Box", 1)]
    [InlineData("Box", 2)]
    [InlineData("Unbox", 1)]
    [InlineData("Unbox", 2)]
    [InlineData("Unbox_Any", 1)]
    [InlineData("Unbox_Any", 2)]
    public void ExternalTypeOperandsMustBeWithinTheExternalTypeTable(string name, int typeId)
    {
        var (patch, map) = WithInstruction(name, typeId);
        // The fixture has two external types: index 1 exists; index 2 is just outside.
        if (typeId == 1) Assert.Empty(PatchValidation.Validate(patch, map));
        else Assert.Contains(PatchValidation.Validate(patch, map), error => error.Contains(name) && error.Contains("index 2 outside"));
    }

    [Theory]
    [InlineData("Box")]
    [InlineData("Unbox")]
    [InlineData("Unbox_Any")]
    public void ReferenceLikeAnonymousTypeBoxingEncodingsAreStructurallyAccepted(string name)
    {
        // The upstream VM treats these negative-operand operations as no-ops. This
        // accepts the compiler's consistent anonymous-type encoding without asserting
        // that the VM dereferences its type index or specifying invalid-index behavior.
        var (patch, map) = WithInstruction(name, -1);
        patch.AnonStoreys.Add(new() { CtorId = 0 });
        Assert.Empty(PatchValidation.Validate(patch, map));
    }

    [Fact]
    public void ArrayElementTypesRequireAnExternalTypeEvenWhenAnonymousTypesExist()
    {
        var (patch, map) = WithInstruction("Newarr", -1);
        patch.AnonStoreys.Add(new() { CtorId = 0 });
        Assert.Contains(PatchValidation.Validate(patch, map), error => error.Contains("Newarr") && error.Contains("index -1 outside"));
    }

    [Theory]
    [InlineData("Ldftn", -1)]
    [InlineData("Ldftn", 1)]
    [InlineData("Ldvirtftn", -1)]
    [InlineData("Ldvirtftn", 1)]
    public void FunctionPointerReferencesMustBeWithinTheExternalMethodTable(string name, int index)
    {
        var (patch, map) = WithInstruction(name, index);
        patch.ExternMethods.Add(new() { DeclaringTypeId = 0, Name = "Run" });

        Assert.Contains(PatchValidation.Validate(patch, map), error => error.Contains(name) && error.Contains("index " + index + " outside"));
    }

    [Theory]
    [InlineData("Ldftn")]
    [InlineData("Ldvirtftn")]
    public void FunctionPointerReferencesAcceptTheFirstExistingExternalMethod(string name)
    {
        var (patch, map) = WithInstruction(name, 0);
        patch.ExternMethods.Add(new() { DeclaringTypeId = 0, Name = "Run" });

        Assert.Empty(PatchValidation.Validate(patch, map));
    }

    [Theory]
    [InlineData("Callvirtvirt", false)]
    [InlineData("Callvirtvirt", true)]
    [InlineData("Ldvirtftn2", false)]
    [InlineData("Ldvirtftn2", true)]
    public void VirtualReferencesRequireTheSlotToExistInAtLeastOneAnonymousTable(string name, bool present)
    {
        var (patch, map) = WithInstruction(name, 1);
        patch.AnonStoreys.Add(new() { CtorId = 0, VTable = present ? [-1, 0] : [-1] });

        if (present) Assert.Empty(PatchValidation.Validate(patch, map));
        else Assert.Contains(PatchValidation.Validate(patch, map), error => error.Contains("slot"));
    }

    [Theory]
    [InlineData("Ldfld", false)]
    [InlineData("Ldfld", true)]
    [InlineData("Stfld", false)]
    [InlineData("Stfld", true)]
    [InlineData("Ldflda", false)]
    [InlineData("Ldflda", true)]
    public void AnonymousInstanceFieldReferencesRequireAnExistingField(string name, bool present)
    {
        var (patch, map) = WithInstruction(name, -1);
        patch.AnonStoreys.Add(new() { CtorId = 0, FieldTypes = present ? [0] : [] });

        if (present) Assert.Empty(PatchValidation.Validate(patch, map));
        else Assert.Contains(PatchValidation.Validate(patch, map), error => error.Contains("anonymous field 0 does not exist"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void ReturnFlagMustDenoteZeroOrOneResult(int flag)
    {
        var (patch, map) = WithInstruction("Ret", flag);
        Assert.Contains(PatchValidation.Validate(patch, map), error => error.Contains("invalid return flag " + flag));
    }

    [Theory]
    [InlineData(-1, new int[0])]
    [InlineData(0, new[] { 0 })]
    [InlineData(1, new[] { 1 })]
    [InlineData(2, new int[0])]
    public void VirtualTargetInventoryReturnsOnlyDistinctExistingMethods(int slot, int[] expected)
    {
        var patch = PatchTestData.Patch();
        patch.Methods.Add(new() { Id = 1 });
        patch.AnonStoreys.Add(new() { VTable = [0, 1] });
        patch.AnonStoreys.Add(new() { VTable = [0, -1] });
        patch.AnonStoreys.Add(new() { VTable = [-1, 2] });
        patch.AnonStoreys.Add(new());

        Assert.Equal(expected, PatchDispatch.Targets(patch, slot));
    }

    [Theory]
    [InlineData(-1, "<type#-1>")]
    [InlineData(2, "<type#2>")]
    [InlineData(0, "Example.Calculator, Synthetic")]
    public void TypeDisplayRetainsAnInvalidIndexInsteadOfMisidentifyingAType(int index, string expected)
        => Assert.Equal(expected, PatchTestData.Patch().TypeName(index));

    private static (PatchFile Patch, OpCodeMap Map) WithInstruction(string name, int operand)
    {
        var names = new[] { "StackSpace", "Ret", name }.Distinct().ToArray();
        var map = OpCodeMap.FromPairs(names.Select((value, index) => (index, value)));
        var patch = PatchTestData.Patch();
        patch.Methods[0].Code = [new() { Code = 0, Operand = 1 },
            new() { Code = Array.IndexOf(names, name), Operand = operand }, new() { Code = 1 }];
        return (patch, map);
    }
}
