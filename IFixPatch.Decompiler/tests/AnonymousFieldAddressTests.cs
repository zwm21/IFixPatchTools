using IFixPatch.Core;

namespace IFixPatch.Decompiler.Tests;

public sealed class AnonymousFieldAddressTests
{
    [Theory]
    [InlineData(0, 12)]
    [InlineData(1, 23)]
    public void AddressOfAKnownAnonymousFieldPreservesTheReceiversStorage(int receiver, int expected)
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.VirtualPatch(sharedImplementation: true);
        patch.FixList.Clear(); fixture.Fix(patch, 0, "ReadA");
        // The constructors independently capture 11 and 22. Read and increment that same
        // field through its address, then read it normally to verify the write was aliased.
        patch.Methods[0].Code = [fixture.Op("StackSpace", 4), fixture.Op("Newanon", receiver), fixture.Op("Dup"),
            fixture.Op("Ldflda", -1), fixture.Op("Dup"), fixture.Op("Ldind_I4"), fixture.Op("Ldc_I4", 1),
            fixture.Op("Add"), fixture.Op("Stind_I4"), fixture.Op("Ldfld", -1), fixture.Op("Ret", 1)];
        var (report, owner, _) = fixture.Decompile(patch);
        Assert.Empty(report.Problems);
        Assert.Equal(expected, owner.GetMethod("ReadA__m0")!.Invoke(null, null));
    }
}
