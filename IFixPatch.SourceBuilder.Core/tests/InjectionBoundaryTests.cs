using Mono.Cecil;

namespace IFixPatch.SourceBuilder.Core.Tests;

public sealed class InjectionBoundaryTests
{
    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("Unsafe/Target")]
    public void RejectsAssemblyNamesThatCannotIdentifyAnOutputFileWithinThePreparedDirectory(string name)
    {
        using var fixture = new SyntheticTargetFixture(name);
        Assert.Contains("cannot be used as a file name", Assert.Throws<InvalidDataException>(() => fixture.Prepare()).Message);
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, "prepared", "prepared-profile.json")));
    }

    [Fact]
    public void RequiresAtLeastIDMAP0EvenWhenAllWrapperTypesArePresent()
    {
        using var fixture = new SyntheticTargetFixture(segmentCount: 0);
        Assert.Contains("IDMAP segments must start at IDMAP0", Assert.Throws<InvalidDataException>(() => fixture.Prepare()).Message);
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, "prepared", "prepared-profile.json")));
    }

    [Fact]
    public void RejectsIDMAPSegmentNumbersThatOverflowBeforeCheckingSegmentContinuity()
    {
        using var fixture = new SyntheticTargetFixture();
        fixture.Change(module => module.GetType("IFix.IDMAP1").Name = "IDMAP2147483648");
        Assert.Contains("IDMAP index must be within", Assert.Throws<InvalidDataException>(() => fixture.Prepare()).Message);
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, "prepared", "prepared-profile.json")));
    }

    [Fact]
    public void RequiresTheDynamicWrapperToBeAClassRatherThanAnUnrelatedSameNamedInterface()
    {
        using var fixture = new SyntheticTargetFixture();
        fixture.Change(module =>
        {
            var wrapper = module.GetType("IFix.ILFixDynamicMethodWrapper");
            wrapper.Attributes = TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract;
            wrapper.BaseType = null;
            wrapper.Fields.Clear();
            wrapper.Methods.Clear();
        });
        Assert.Contains("IFix.ILFixDynamicMethodWrapper", Assert.Throws<InvalidDataException>(() => fixture.Prepare()).Message);
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, "prepared", "prepared-profile.json")));
    }
}
