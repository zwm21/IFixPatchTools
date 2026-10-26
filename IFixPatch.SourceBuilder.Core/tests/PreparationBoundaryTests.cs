using System.Security.Cryptography;
using Mono.Cecil;

namespace IFixPatch.SourceBuilder.Core.Tests;

public sealed class PreparationBoundaryTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" \t")]
    public void RequiresANonblankProfileName(string name)
    {
        using var fixture = new SyntheticTargetFixture(); var profile = LoadProfile(fixture); profile.Name = name; SaveProfile(fixture, profile);
        Assert.Contains("name is required", Assert.Throws<InvalidDataException>(() => fixture.Prepare()).Message);
    }

    [Theory]
    [InlineData("target")]
    [InlineData("runtime")]
    [InlineData("known-patch")]
    public void ReportsTheExactMissingProfileInput(string input)
    {
        using var fixture = new SyntheticTargetFixture(); var profile = LoadProfile(fixture); var missing = Path.Combine(fixture.DirectoryPath, "missing-" + input);
        if (input == "target") profile.AssemblyPath = missing;
        else if (input == "runtime") profile.IFixCoreAssemblyPath = missing;
        else profile.KnownPatchPath = missing;
        SaveProfile(fixture, profile);
        Assert.Equal(missing, Assert.Throws<FileNotFoundException>(() => fixture.Prepare()).FileName);
    }

    [Fact]
    public void DefaultsReferenceSearchToTheTargetAssemblyDirectory()
    {
        using var fixture = new SyntheticTargetFixture(); var profile = LoadProfile(fixture); profile.ReferenceDirectories = []; SaveProfile(fixture, profile);
        var prepared = fixture.Prepare();
        Assert.Equal(fixture.DirectoryPath, Assert.Single(prepared.ReferenceDirectories));
        BuildPipeline.ValidatePrepared(prepared);
    }

    [Fact]
    public void RejectsAnExplicitReferenceDirectoryThatDoesNotExist()
    {
        using var fixture = new SyntheticTargetFixture(); var profile = LoadProfile(fixture); var missing = Path.Combine(fixture.DirectoryPath, "missing-references");
        profile.ReferenceDirectories = [missing]; SaveProfile(fixture, profile);
        Assert.Contains(missing, Assert.Throws<DirectoryNotFoundException>(() => fixture.Prepare()).Message);
    }

    [Fact]
    public void ValidatesKnownPatchMagicAndAllInjectionAssemblyIdentitiesBeforeSavingThePreparedProfile()
    {
        using var fixture = new SyntheticTargetFixture(); var patch = WriteKnownPatch(fixture, "matching"); var profile = LoadProfile(fixture);
        profile.KnownPatchPath = patch; SaveProfile(fixture, profile); var prepared = fixture.Prepare();
        Assert.Equal(0x123456789UL, prepared.KnownPatchMagic);
        Assert.Equal(patch, prepared.KnownPatchPath);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(patch))), prepared.KnownPatchSha256);
        Assert.Equal("Example.Game, Version=2.3.4.0, Culture=neutral, PublicKeyToken=null", prepared.TargetAssemblyIdentity);
        BuildPipeline.ValidatePrepared(prepared);
        var saved = SourceBuilderJson.Load<PreparedProfile>(Path.Combine(fixture.DirectoryPath, "prepared", "prepared-profile.json"));
        Assert.Equal(prepared.KnownPatchSha256, saved.KnownPatchSha256);
    }

    [Theory]
    [InlineData("magic", "magic")]
    [InlineData("bridge-name", "injection type")]
    [InlineData("bridge-assembly", "assembly identity")]
    [InlineData("manager-name", "injection type")]
    [InlineData("manager-assembly", "assembly identity")]
    [InlineData("idmap-assembly", "assembly identity")]
    [InlineData("fix-assembly", "assembly identity")]
    [InlineData("trailing", "trailing bytes")]
    public void RejectsKnownPatchesThatConflictWithTheSelectedRuntimeOrTarget(string conflict, string requirement)
    {
        using var fixture = new SyntheticTargetFixture(); var profile = LoadProfile(fixture); profile.KnownPatchPath = WriteKnownPatch(fixture, conflict); SaveProfile(fixture, profile);
        Assert.Contains(requirement, Assert.Throws<InvalidDataException>(() => fixture.Prepare()).Message);
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, "prepared", "prepared-profile.json")));
    }

    [Theory]
    [InlineData(FieldAttributes.Private, "private")]
    [InlineData(FieldAttributes.FamANDAssem, "private protected")]
    [InlineData(FieldAttributes.Assembly, "internal")]
    [InlineData(FieldAttributes.Family, "protected")]
    [InlineData(FieldAttributes.FamORAssem, "protected internal")]
    [InlineData(FieldAttributes.Public, "public")]
    [InlineData(FieldAttributes.CompilerControlled, "compiler-controlled")]
    public void CatalogReportsOriginalClrFieldAccessWhileTheCompileReferenceAllowsAccess(FieldAttributes attributes, string visibility)
    {
        using var fixture = new SyntheticTargetFixture();
        fixture.Change(module => module.GetType("Example.Calculator").Fields.Add(new FieldDefinition("AccessLevel", attributes, module.TypeSystem.Int32)));
        var prepared = fixture.Prepare(); var catalog = SourceBuilderJson.Load<FieldCatalog>(prepared.FieldCatalogPath);
        Assert.Equal(visibility, catalog.Types.Single(type => type.Type == "Example.Calculator").Fields.Single(field => field.Name == "AccessLevel").Visibility);
        using var clean = AssemblyDefinition.ReadAssembly(prepared.CleanAssemblyPath);
        using var compile = AssemblyDefinition.ReadAssembly(prepared.CompileReferenceAssemblyPath);
        Assert.Equal(attributes, clean.MainModule.GetType("Example.Calculator").Fields.Single(field => field.Name == "AccessLevel").Attributes & FieldAttributes.FieldAccessMask);
        Assert.True(compile.MainModule.GetType("Example.Calculator").Fields.Single(field => field.Name == "AccessLevel").IsPublic);
    }

    [Fact]
    public void CatalogPreservesReadonlyStaticAndLiteralFlags()
    {
        using var fixture = new SyntheticTargetFixture();
        fixture.Change(module =>
        {
            var owner = module.GetType("Example.Calculator");
            owner.Fields.Add(new FieldDefinition("Readonly", FieldAttributes.Private | FieldAttributes.InitOnly, module.TypeSystem.Int32));
            owner.Fields.Add(new FieldDefinition("Static", FieldAttributes.Private | FieldAttributes.Static, module.TypeSystem.Int32));
            owner.Fields.Add(new FieldDefinition("Constant", FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault, module.TypeSystem.Int32) { Constant = 42 });
        });
        var prepared = fixture.Prepare(); var fields = SourceBuilderJson.Load<FieldCatalog>(prepared.FieldCatalogPath).Types.Single(type => type.Type == "Example.Calculator").Fields;
        Assert.True(fields.Single(field => field.Name == "Readonly").IsInitOnly);
        Assert.False(fields.Single(field => field.Name == "Readonly").IsStatic);
        Assert.True(fields.Single(field => field.Name == "Static").IsStatic);
        Assert.False(fields.Single(field => field.Name == "Static").IsLiteral);
        Assert.True(fields.Single(field => field.Name == "Constant").IsStatic);
        Assert.True(fields.Single(field => field.Name == "Constant").IsLiteral);
    }

    [Theory]
    [InlineData("<Value>k__BackingField", "Value")]
    [InlineData("_Value_k__BackingField", null)]
    [InlineData("<>k__BackingField", null)]
    [InlineData("<Value>other", null)]
    public void CatalogRecognizesMetadataBackingFieldNamesRatherThanDumpRendering(string name, string? property)
    {
        using var fixture = new SyntheticTargetFixture(); fixture.Change(module => module.GetType("Example.Calculator").Fields.Add(new FieldDefinition(name, FieldAttributes.Private, module.TypeSystem.Int32)));
        var prepared = fixture.Prepare(); var field = SourceBuilderJson.Load<FieldCatalog>(prepared.FieldCatalogPath).Types.Single(type => type.Type == "Example.Calculator").Fields.Single(field => field.Name == name);
        Assert.Equal(property, field.BackedProperty); Assert.Equal(property is not null, field.IsBackingField);
    }

    [Fact]
    public void CompileReferenceKeepsAnEventBackingFieldPrivateAndExposesItsAccessors()
    {
        using var fixture = new SyntheticTargetFixture();
        fixture.Change(module =>
        {
            var owner = module.GetType("Example.Calculator"); var action = new TypeReference("System", "Action", module, module.TypeSystem.CoreLibrary);
            owner.Fields.Add(new FieldDefinition("Changed", FieldAttributes.Private, action));
            var add = SyntheticTargetFixture.Method(owner, "add_Changed", false, module.TypeSystem.Void, action); add.IsPublic = false; add.IsPrivate = true; add.IsSpecialName = true;
            var remove = SyntheticTargetFixture.Method(owner, "remove_Changed", false, module.TypeSystem.Void, action); remove.IsPublic = false; remove.IsPrivate = true; remove.IsSpecialName = true;
            owner.Events.Add(new EventDefinition("Changed", EventAttributes.None, action) { AddMethod = add, RemoveMethod = remove });
        });
        var prepared = fixture.Prepare(); using var compile = AssemblyDefinition.ReadAssembly(prepared.CompileReferenceAssemblyPath);
        var owner = compile.MainModule.GetType("Example.Calculator"); Assert.True(owner.Fields.Single(field => field.Name == "Changed").IsPrivate);
        var changed = Assert.Single(owner.Events); Assert.True(changed.AddMethod.IsPublic); Assert.True(changed.RemoveMethod.IsPublic);
    }

    private static SourceBuilderProfile LoadProfile(SyntheticTargetFixture fixture) => SourceBuilderJson.Load<SourceBuilderProfile>(fixture.ProfilePath);
    private static void SaveProfile(SyntheticTargetFixture fixture, SourceBuilderProfile profile) => SourceBuilderJson.Save(fixture.ProfilePath, profile);

    private static string WriteKnownPatch(SyntheticTargetFixture fixture, string conflict)
    {
        const string identity = "Example.Game, Version=2.3.4.0, Culture=neutral, PublicKeyToken=null";
        const string otherIdentity = "Other.Game, Version=2.3.4.0, Culture=neutral, PublicKeyToken=null";
        var path = Path.Combine(fixture.DirectoryPath, "known.bytes");
        using var writer = new BinaryWriter(File.Create(path));
        // Independently encode the serialized IFix sections. Opcodes below match this fixture's
        // declared sequential enum: StackSpace=180, Ldc_I4=9, Ret=18; no VM execution is claimed.
        writer.Write(conflict == "magic" ? 0xBADUL : 0x123456789UL);
        writer.Write((conflict == "bridge-name" ? "IFix.OtherBridge" : "IFix.ILFixInterfaceBridge") + ", " + (conflict == "bridge-assembly" ? otherIdentity : identity));
        writer.Write(1); writer.Write("Example.Calculator, " + (conflict == "fix-assembly" ? otherIdentity : identity)); // extern types
        writer.Write(1); writer.Write(3); // one method, three instructions
        writer.Write(180); writer.Write(1 << 16); writer.Write(9); writer.Write(42); writer.Write(18); writer.Write(0); writer.Write(0); // no handlers
        writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0); // extern methods, strings, fields, static fields, closures
        writer.Write((conflict == "manager-name" ? "IFix.OtherManager" : "IFix.WrappersManagerImpl") + ", " + (conflict == "manager-assembly" ? otherIdentity : identity));
        writer.Write(", " + (conflict == "idmap-assembly" ? otherIdentity : identity));
        writer.Write(1); writer.Write(false); writer.Write(0); writer.Write("Compute"); writer.Write(0); writer.Write(0); // fix count, signature, method id
        writer.Write(0); // new classes
        if (conflict == "trailing") writer.Write((byte)0xCC);
        return path;
    }
}
