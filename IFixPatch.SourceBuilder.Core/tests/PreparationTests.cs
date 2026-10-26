using Mono.Cecil;

namespace IFixPatch.SourceBuilder.Core.Tests;

public sealed class PreparationTests
{
    [Theory]
    [InlineData("Assembly-CSharp")]
    [InlineData("Example.Game")]
    public void UsesMetadataIdentityWhenTheInputFileIsRenamed(string name)
    {
        using var target = new SyntheticTargetFixture(name);
        var prepared = target.Prepare();
        Assert.Equal(name, prepared.TargetAssemblyName);
        Assert.Equal(name + ".dll", Path.GetFileName(prepared.CleanAssemblyPath));
        Assert.Equal(name + ".dll", Path.GetFileName(prepared.CompileReferenceAssemblyPath));
        BuildPipeline.ValidatePrepared(prepared);
        using var clean = AssemblyDefinition.ReadAssembly(prepared.CleanAssemblyPath);
        Assert.Equal(prepared.TargetAssemblyIdentity, clean.Name.FullName);
    }

    [Fact]
    public void RemovesEveryMapSegmentAndRedirectWithoutDeletingSimilarUserTypes()
    {
        using var target = new SyntheticTargetFixture(segmentCount: 5);
        var prepared = target.Prepare();
        var layout = InjectionLayout.Load(prepared);
        Assert.Equal(Enumerable.Range(0, 5), layout.IdMapSegments.Select(segment => segment.Index));
        Assert.Contains("IFix.RedirectTo.Example.Calculator/Nested", layout.RedirectTypes);
        using var clean = AssemblyDefinition.ReadAssembly(prepared.CleanAssemblyPath);
        Assert.DoesNotContain(clean.MainModule.Types, type => layout.RemovedTypes.Contains(type.FullName));
        Assert.NotNull(clean.MainModule.GetType("Example.ILFixDynamicMethodWrapper"));
        Assert.NotNull(clean.MainModule.GetType("IFix.IDMAPHelper"));
        Assert.NotNull(clean.MainModule.GetType("IFix.RedirectTools.UserType"));
        var catalog = SourceBuilderJson.Load<AssemblyCatalog>(prepared.CatalogPath);
        Assert.Equal(new[] { 3, 20, 37, 54, 71 }, catalog.IdMap.Select(entry => entry.Slot));
        Assert.Equal(Enumerable.Range(0, 5).Select(index => "IFix.IDMAP" + index), catalog.IdMap.Select(entry => entry.DeclaringType));
    }

    [Fact]
    public void KeepsOriginalVisibilityInCatalogAndWidensOnlyTheCompileReference()
    {
        using var target = new SyntheticTargetFixture();
        var prepared = target.Prepare();
        var catalog = SourceBuilderJson.Load<FieldCatalog>(prepared.FieldCatalogPath);
        Assert.Equal("private", catalog.Types.Single(type => type.Type == "Example.Calculator").Fields.Single(field => field.Name == "Offset").Visibility);
        using var compile = AssemblyDefinition.ReadAssembly(prepared.CompileReferenceAssemblyPath);
        Assert.True(compile.MainModule.GetType("Example.Calculator").Fields.Single(field => field.Name == "Offset").IsPublic);
        using var clean = AssemblyDefinition.ReadAssembly(prepared.CleanAssemblyPath);
        Assert.True(clean.MainModule.GetType("Example.Calculator").Fields.Single(field => field.Name == "Offset").IsPrivate);
    }

    [Theory]
    [InlineData("gap", "contiguous")]
    [InlineData("duplicate-slot", "duplicate")]
    [InlineData("duplicate-field", "duplicate")]
    [InlineData("negative-slot", "negative")]
    [InlineData("not-enum", "Int32 enum")]
    [InlineData("out-of-range", "0..99")]
    [InlineData("leading-zero", "noncanonical")]
    [InlineData("missing-manager", "WrappersManagerImpl")]
    [InlineData("invalid-bridge", "bridge base")]
    public void RejectsUnsupportedOrConflictingInjectionMetadata(string mutation, string diagnostic)
    {
        using var target = new SyntheticTargetFixture();
        target.Change(module =>
        {
            var map = module.GetType("IFix.IDMAP1");
            switch (mutation)
            {
                case "gap": module.Types.Remove(map); break;
                case "duplicate-slot": map.Fields.Single(field => field.IsLiteral).Constant = 3; break;
                case "duplicate-field": map.Fields.Single(field => field.IsLiteral).Name = "Example-Calculator-Compute0"; break;
                case "negative-slot": map.Fields.Single(field => field.IsLiteral).Constant = -1; break;
                case "not-enum": map.BaseType = module.TypeSystem.Object; break;
                case "out-of-range": map.Name = "IDMAP100"; break;
                case "leading-zero": map.Name = "IDMAP01"; break;
                case "missing-manager": module.Types.Remove(module.GetType("IFix.WrappersManagerImpl")); break;
                case "invalid-bridge": module.GetType("IFix.ILFixInterfaceBridge").BaseType = module.TypeSystem.Object; break;
            }
        });
        Assert.Contains(diagnostic, Assert.Throws<InvalidDataException>(() => target.Prepare()).Message);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("runtime")]
    [InlineData("layout")]
    [InlineData("catalog")]
    [InlineData("field-catalog")]
    [InlineData("adapter")]
    [InlineData("clean")]
    [InlineData("compile-reference")]
    public void InvalidatesPreparedArtifactsWhenAnInputChanges(string artifact)
    {
        using var target = new SyntheticTargetFixture();
        var prepared = target.Prepare();
        var path = artifact switch
        {
            "source" => prepared.SourceAssemblyPath,
            "runtime" => prepared.IFixCoreAssemblyPath,
            "layout" => prepared.InjectionLayoutPath,
            "catalog" => prepared.CatalogPath,
            "field-catalog" => prepared.FieldCatalogPath,
            "adapter" => prepared.AdapterCoreSourcePath,
            "clean" => prepared.CleanAssemblyPath,
            _ => prepared.CompileReferenceAssemblyPath,
        };
        using (var stream = File.Open(path, FileMode.Append)) stream.WriteByte(32);
        Assert.Contains("drifted", Assert.Throws<InvalidDataException>(() => BuildPipeline.ValidatePrepared(prepared)).Message);
    }

    [Fact]
    public void RequiresPreparingLegacyProfilesAgain()
    {
        Assert.Contains("run prepare again", Assert.Throws<InvalidDataException>(() => BuildPipeline.ValidatePrepared(new PreparedProfile())).Message);
    }
}
