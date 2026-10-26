namespace IFixPatch.Core.Tests;

public sealed class ExecutableBoundaryTests
{
    [Theory]
    [InlineData("Ldarg")]
    [InlineData("Ldarga")]
    [InlineData("Starg")]
    public void ArgumentIndexesCannotBeNegative(string operation)
    {
        var (patch, map) = Create(operation, -1);
        Assert.Contains(PatchValidation.Validate(patch, map), error => error.Contains("argument index") && error.Contains("-1"));
    }

    [Theory]
    [InlineData("Ldarg")]
    [InlineData("Ldarga")]
    [InlineData("Starg")]
    public void TheFirstArgumentIndexIsStructurallyAccepted(string operation)
    {
        var (patch, map) = Create(operation, 0);
        patch.FixList[0].Method.Params.Add(new() { TypeId = 1 });
        Assert.Empty(PatchValidation.Validate(patch, map));
    }

    [Theory]
    [InlineData("Br")]
    [InlineData("Leave")]
    [InlineData("Switch")]
    public void BranchesCannotExecuteTheStackAllocationPrologue(string operation)
    {
        var (patch, map) = Create(operation, operation == "Br" ? -1 : operation == "Switch" ? 1 : 0);
        if (operation == "Switch") patch.Methods[0].Code.Insert(2, new() { Code = -1 });
        Assert.Contains(PatchValidation.Validate(patch, map), error => error.Contains("prologue"));
    }

    private static (PatchFile Patch, OpCodeMap Map) Create(string operation, int operand)
    {
        var patch = PatchTestData.Patch();
        var map = OpCodeMap.FromPairs([(0, "StackSpace"), (1, "Ret"), (2, operation)]);
        patch.Methods[0].Code = [new() { Code = 0, Operand = 1 }, new() { Code = 2, Operand = operand }, new() { Code = 1 }];
        return (patch, map);
    }
}
