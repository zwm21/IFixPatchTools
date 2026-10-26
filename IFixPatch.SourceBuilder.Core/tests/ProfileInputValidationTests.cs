using IFixPatch.Core;

namespace IFixPatch.SourceBuilder.Core.Tests;

public sealed class ProfileInputValidationTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" \t")]
    public void RejectsBlankProfilePathsInsteadOfInterpretingThemAsTheContainingDirectory(string path)
    {
        using var fixture = new SyntheticTargetFixture();
        Assert.Contains("empty", Assert.Throws<InvalidDataException>(() => PathAndHash.ResolveFromFile(fixture.ProfilePath, path)).Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t")]
    [InlineData("**/")]
    [InlineData("missing/*.cs")]
    public void RejectsSourceEntriesThatCannotDenoteACompilerInput(string pattern)
    {
        using var fixture = new SyntheticTargetFixture();
        if (pattern == "missing/*.cs")
            Assert.Throws<DirectoryNotFoundException>(() => PathAndHash.ResolveSources(fixture.ProfilePath, [pattern]));
        else
            Assert.Throws<InvalidDataException>(() => PathAndHash.ResolveSources(fixture.ProfilePath, [pattern]));
    }

    [Fact]
    public void RejectsAnEmptySourceSetBeforeInvokingACompiler()
    {
        using var fixture = new SyntheticTargetFixture();
        Assert.Contains("no patch sources", Assert.Throws<InvalidDataException>(() => PathAndHash.ResolveSources(fixture.ProfilePath, [])).Message);
    }

    [Fact]
    public void RejectsJsonNullAsAnAbsentProfileRatherThanReturningANullModel()
    {
        using var fixture = new SyntheticTargetFixture();
        var path = Path.Combine(fixture.DirectoryPath, "null-profile.json");
        File.WriteAllText(path, "null");
        Assert.Throws<InvalidDataException>(() => SourceBuilderJson.Load<SourceBuilderProfile>(path));
    }

    [Theory]
    [InlineData("format")]
    [InlineData("identity")]
    public void RejectsALayoutFromAnotherFormatOrTargetEvenIfTheFileExists(string mismatch)
    {
        using var fixture = new SyntheticTargetFixture();
        var prepared = fixture.Prepare();
        var layout = SourceBuilderJson.Load<InjectionLayout>(prepared.InjectionLayoutPath);
        if (mismatch == "format") layout.Format = "injectfix-v999";
        else layout.TargetAssemblyIdentity = "Other.Game, Version=2.3.4.0, Culture=neutral, PublicKeyToken=null";
        SourceBuilderJson.Save(prepared.InjectionLayoutPath, layout);
        Assert.Contains("run prepare again", Assert.Throws<InvalidDataException>(() => InjectionLayout.Load(prepared)).Message);
    }

    [Theory]
    [InlineData("suffix")]
    [InlineData("fix-identity")]
    public void RejectsPatchNamesThatOmitTheRequiredOuterAssemblyIdentity(string omission)
    {
        const string identity = "Example.Game, Version=2.3.4.0, Culture=neutral, PublicKeyToken=null";
        var layout = new InjectionLayout { TargetAssemblyIdentity = identity, InterfaceBridgeType = "IFix.ILFixInterfaceBridge", WrapperManagerType = "IFix.WrappersManagerImpl" };
        var patch = new PatchFile
        {
            FullyParsed = true,
            InterfaceBridgeType = "IFix.ILFixInterfaceBridge, " + identity,
            WrapperManagerType = "IFix.WrappersManagerImpl, " + identity,
            IdMapSuffix = omission == "suffix" ? identity : ", " + identity,
            ExternTypes = [omission == "fix-identity" ? "Example.Calculator" : "Example.Calculator, " + identity],
            FixList = [new FixEntry { Method = new ExternMethodSig { DeclaringTypeId = 0, Name = "Compute" } }],
        };
        Assert.Throws<InvalidDataException>(() => layout.ValidatePatchIdentity(patch));
    }
}
