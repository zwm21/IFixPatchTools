using System.Security.Cryptography;
using Mono.Cecil;

namespace IFixPatch.SourceBuilder.Core.Tests;

public sealed class PreparedValidationTests
{
    [Fact]
    public void RejectsProfilesMissingInjectionLayoutMetadata()
    {
        using var fixture = new SyntheticTargetFixture(); var prepared = fixture.Prepare(); prepared.InjectionLayoutSha256 = "";
        Assert.Contains("run prepare again", Assert.Throws<InvalidDataException>(() => BuildPipeline.ValidatePrepared(prepared)).Message);
    }

    [Fact]
    public void ReportsTheExactPreparedInputThatWasDeleted()
    {
        using var fixture = new SyntheticTargetFixture(); var prepared = fixture.Prepare(); File.Delete(prepared.CatalogPath);
        Assert.Equal(prepared.CatalogPath, Assert.Throws<FileNotFoundException>(() => BuildPipeline.ValidatePrepared(prepared)).FileName);
    }

    [Fact]
    public void RejectsACleanAssemblyWithADifferentIdentityEvenIfItsHashWasRefreshed()
    {
        using var fixture = new SyntheticTargetFixture(); var prepared = fixture.Prepare();
        using (var assembly = AssemblyDefinition.ReadAssembly(prepared.CleanAssemblyPath, new ReaderParameters { InMemory = true }))
        {
            assembly.Name.Name = "Different.Game"; assembly.Write(prepared.CleanAssemblyPath);
        }
        prepared.CleanAssemblySha256 = Hash(prepared.CleanAssemblyPath);
        Assert.Contains("identity mismatch", Assert.Throws<InvalidDataException>(() => BuildPipeline.ValidatePrepared(prepared)).Message);
    }

    [Fact]
    public void RejectsARuntimeWithADifferentIdentityEvenIfItsHashWasRefreshed()
    {
        using var fixture = new SyntheticTargetFixture(); var prepared = fixture.Prepare();
        using (var assembly = AssemblyDefinition.ReadAssembly(prepared.IFixCoreAssemblyPath, new ReaderParameters { InMemory = true }))
        {
            assembly.Name.Version = new Version(9, 0); assembly.Write(prepared.IFixCoreAssemblyPath);
        }
        prepared.IFixCoreAssemblySha256 = Hash(prepared.IFixCoreAssemblyPath);
        Assert.Contains("runtime identity", Assert.Throws<InvalidDataException>(() => BuildPipeline.ValidatePrepared(prepared)).Message);
    }

    [Fact]
    public void RejectsKnownPatchContentDrift()
    {
        using var fixture = new SyntheticTargetFixture(); var prepared = fixture.Prepare();
        prepared.KnownPatchPath = Path.Combine(fixture.DirectoryPath, "known.bytes"); File.WriteAllBytes(prepared.KnownPatchPath, [1, 2, 3, 4, 5, 6, 7, 8]);
        prepared.KnownPatchSha256 = Hash(prepared.KnownPatchPath); BuildPipeline.ValidatePrepared(prepared);
        File.WriteAllBytes(prepared.KnownPatchPath, [1, 2, 3, 4, 5, 6, 7, 9]);
        Assert.Contains("Known patch hash drifted", Assert.Throws<InvalidDataException>(() => BuildPipeline.ValidatePrepared(prepared)).Message);
    }

    [Fact]
    public void RejectsDifferentPreferredCompileAndRuntimeReferencesWithTheSameAssemblyIdentity()
    {
        using var fixture = new SyntheticTargetFixture(); var prepared = fixture.Prepare();
        var duplicate = Path.Combine(fixture.DirectoryPath, "duplicate-runtime.dll");
        using (var assembly = AssemblyDefinition.ReadAssembly(prepared.IFixCoreAssemblyPath))
        {
            SyntheticTargetFixture.Class(assembly.MainModule, "Test", "DifferentContent"); assembly.Write(duplicate);
        }
        prepared.CompileReferenceAssemblyPath = duplicate;
        var manifest = CompilerPipelineTests.WriteSource(fixture, "public class Example {}");
        Assert.Contains("Conflicting preferred references", Assert.Throws<InvalidDataException>(() => new SourceCompiler().Compile(prepared, manifest, Path.Combine(fixture.DirectoryPath, "out.dll"))).Message);
    }
    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}
