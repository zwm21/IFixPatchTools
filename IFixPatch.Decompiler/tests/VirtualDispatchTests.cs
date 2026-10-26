using IFixPatch.Core;
using System.Reflection;

namespace IFixPatch.Decompiler.Tests;

public sealed class VirtualDispatchTests
{
    [Theory]
    [InlineData(1, 11)]
    [InlineData(0, 22)]
    public void VirtualCallSelectsImplementationFromReceiver(int choice, int expected)
    {
        using var fixture = new DecompilerFixture();
        var (_, owner, _) = fixture.Decompile(fixture.VirtualPatch());

        Assert.Equal(expected, owner.GetMethod("Value__m0")!.Invoke(null, [choice]));
    }

    [Theory]
    [InlineData(1, 11)]
    [InlineData(0, 22)]
    public void VirtualPointerPreservesReceiverAndBranchTarget(int choice, int expected)
    {
        using var fixture = new DecompilerFixture();
        var (_, owner, _) = fixture.Decompile(fixture.VirtualPatch());

        var function = Assert.IsType<Func<int>>(owner.GetMethod("Pointer__m5")!.Invoke(null, [choice]));
        Assert.Equal(expected, function());
    }

    [Theory]
    [InlineData(1, 11)]
    [InlineData(0, 22)]
    public void SharedVirtualBodyReadsEachReceiversOwnField(int choice, int expected)
    {
        using var fixture = new DecompilerFixture();
        var (_, owner, _) = fixture.Decompile(fixture.VirtualPatch(sharedImplementation: true));

        Assert.Equal(expected, owner.GetMethod("Value__m0")!.Invoke(null, [choice]));
    }

    [Theory]
    [InlineData(1, 11)]
    [InlineData(0, 22)]
    public void SharedVirtualPointerUsesReceiverSpecificBody(int choice, int expected)
    {
        using var fixture = new DecompilerFixture();
        var (_, owner, _) = fixture.Decompile(fixture.VirtualPatch(sharedImplementation: true));

        var function = Assert.IsType<Func<int>>(owner.GetMethod("Pointer__m5")!.Invoke(null, [choice]));
        Assert.Equal(expected, function());
    }

    [Fact]
    public void KnownReceiverExcludesUnrelatedTypesWithSameSlot()
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.VirtualPatch(sharedImplementation: true);
        AddUnrelatedVoidSlot(fixture, patch);
        patch.Methods[0].Code = [fixture.Op("StackSpace", 1), fixture.Op("Newanon"),
            fixture.Op("Callvirtvirt", (1 << 16) | DecompilerFixture.Slot), fixture.Op("Ret", 1)];
        patch.Methods[5].Code = [fixture.Op("StackSpace", 2), fixture.Op("Newanon", 1), fixture.Op("Ldc_I4"),
            fixture.Op("Ldvirtftn2", DecompilerFixture.Slot), fixture.Op("Newobj", (2 << 16) | 1), fixture.Op("Ret", 1)];

        var (_, owner, _) = fixture.Decompile(patch);

        Assert.Equal(11, owner.GetMethod("Value__m0")!.Invoke(null, [0]));
        var function = Assert.IsType<Func<int>>(owner.GetMethod("Pointer__m5")!.Invoke(null, [0]));
        Assert.Equal(22, function());
    }

    [Fact]
    public void ReachableReceiversWithIncompatibleSlotContractsAreRejected()
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.VirtualPatch();
        patch.Methods[4].Code = [fixture.Op("StackSpace"), fixture.Op("Ret")];

        var error = Assert.Throws<InvalidDataException>(() => fixture.Decompile(patch));

        Assert.Contains("Incompatible return signatures", error.Message);
    }

    [Fact]
    public void VirtualDispatchRejectsNullReceiver()
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.VirtualPatch();
        patch.Methods[0].Code = [fixture.Op("StackSpace", 1), fixture.Op("Ldnull"),
            fixture.Op("Callvirtvirt", (1 << 16) | DecompilerFixture.Slot), fixture.Op("Ret", 1)];
        var (_, owner, _) = fixture.Decompile(patch);

        var error = Assert.Throws<TargetInvocationException>(() => owner.GetMethod("Value__m0")!.Invoke(null, [0]));

        Assert.IsType<NullReferenceException>(error.InnerException);
    }

    [Fact]
    public void VirtualPointerRejectsIncompatibleCandidateReturnContracts()
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.VirtualPatch();
        patch.Methods[0].Code = [fixture.Op("StackSpace", 1), fixture.Op("Ldc_I4"), fixture.Op("Ret", 1)];
        patch.Methods[4].Code = [fixture.Op("StackSpace"), fixture.Op("Ret")];

        var error = Assert.Throws<InvalidDataException>(() => fixture.Decompile(patch));

        Assert.Contains("Incompatible return signatures", error.Message);
    }

    [Fact]
    public void VirtualPointerRejectsIncompatibleCandidateArgumentContracts()
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.VirtualPatch();
        patch.Methods[0].Code = [fixture.Op("StackSpace", 1), fixture.Op("Ldc_I4"), fixture.Op("Ret", 1)];
        patch.Methods[4].Code = [fixture.Op("StackSpace", 1), fixture.Op("Ldarg", 1), fixture.Op("Pop"),
            fixture.Op("Ldc_I4", 22), fixture.Op("Ret", 1)];

        var error = Assert.Throws<InvalidDataException>(() => fixture.Decompile(patch));

        Assert.Contains("Incompatible argument signatures", error.Message);
    }

    private static void AddUnrelatedVoidSlot(DecompilerFixture fixture, PatchFile patch)
    {
        var constructor = fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldarg"),
            fixture.Op("CallExtern", 1 << 16), fixture.Op("Ret"));
        var implementation = fixture.Method(patch, fixture.Op("StackSpace"), fixture.Op("Ret"));
        patch.AnonStoreys.Add(new AnonStorey { CtorId = constructor, VTable = [-1, -1, -1, -1, implementation] });
    }
}
