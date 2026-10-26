using IFixPatch.Core;

namespace IFixPatch.SourceBuilder.Core.Tests;

public sealed class InjectionIdentityTests
{
    private const string Identity = "Example.Game, Version=2.3.4.0, Culture=neutral, PublicKeyToken=null";

    [Fact]
    public void ReadsTheOuterAssemblyOfGenericFixTypes()
    {
        var patch = Patch();
        patch.ExternTypes[0] = "Example.Container`1[[System.Int32, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]], " + Identity;
        Layout().ValidatePatchIdentity(patch);
    }

    [Theory]
    [InlineData("bridge")]
    [InlineData("manager")]
    [InlineData("idmap")]
    [InlineData("fix")]
    [InlineData("partial")]
    [InlineData("trailing")]
    public void RejectsPatchesWhoseIdentityDoesNotMatchThePreparedTarget(string part)
    {
        var patch = Patch();
        switch (part)
        {
            case "bridge": patch.InterfaceBridgeType = "Other.Bridge, " + Identity; break;
            case "manager": patch.WrapperManagerType = "IFix.WrappersManagerImpl, Other.Game"; break;
            case "idmap": patch.IdMapSuffix = ", Other.Game"; break;
            case "fix": patch.ExternTypes[0] = "Example.Calculator, Other.Game"; break;
            case "partial": patch.FullyParsed = false; break;
            case "trailing": patch.TrailingBytes = 1; break;
        }
        Assert.Throws<InvalidDataException>(() => Layout().ValidatePatchIdentity(patch));
    }

    private static InjectionLayout Layout() => new()
    {
        TargetAssemblyIdentity = Identity,
        InterfaceBridgeType = "IFix.ILFixInterfaceBridge",
        WrapperManagerType = "IFix.WrappersManagerImpl",
    };

    private static PatchFile Patch() => new()
    {
        FullyParsed = true,
        InterfaceBridgeType = "IFix.ILFixInterfaceBridge, " + Identity,
        WrapperManagerType = "IFix.WrappersManagerImpl, " + Identity,
        IdMapSuffix = ", " + Identity,
        ExternTypes = ["Example.Calculator, " + Identity],
        FixList = [new FixEntry { Method = new ExternMethodSig { DeclaringTypeId = 0, Name = "Compute" } }],
    };
}
