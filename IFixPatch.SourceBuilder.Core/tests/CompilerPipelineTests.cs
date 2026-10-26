using System.Reflection;
using System.Security.Cryptography;
using Mono.Cecil;

namespace IFixPatch.SourceBuilder.Core.Tests;

public sealed class CompilerPipelineTests
{
    [Fact]
    public void CompilesRealCSharpAgainstThePreparedTargetAndProducesPortableSymbols()
    {
        using var fixture = new SyntheticTargetFixture();
        var prepared = fixture.Prepare();
        var manifest = WriteSource(fixture, "namespace Patch { public static class Replacement { public static int Compute() { return 6 * 7; } } }");
        var output = Path.Combine(fixture.DirectoryPath, "compiler output", "Impl.dll");
        Assert.Equal(output, new SourceCompiler().Compile(prepared, manifest, output));
        Assert.Equal(42, Assembly.Load(File.ReadAllBytes(output)).GetType("Patch.Replacement")!.GetMethod("Compute")!.Invoke(null, null));
        using var compiled = AssemblyDefinition.ReadAssembly(output, new ReaderParameters { ReadSymbols = true });
        Assert.True(compiled.MainModule.HasSymbols);
        Assert.Contains(compiled.MainModule.GetType("Patch.Replacement").Methods, method => method.DebugInformation.HasSequencePoints);
    }

    [Fact]
    public void CompilationDiagnosticsIdentifyTheBrokenSource()
    {
        using var fixture = new SyntheticTargetFixture();
        var manifest = WriteSource(fixture, "namespace Patch { public class Broken { public int X() { return missingName; } } }");
        var error = Assert.Throws<InvalidOperationException>(() => new SourceCompiler().Compile(fixture.Prepare(), manifest, Path.Combine(fixture.DirectoryPath, "broken.dll")));
        Assert.Contains("CS0103", error.Message);
        Assert.Contains("PatchImpl.cs", error.Message);
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, "broken.dll")));
    }

    [Fact]
    public void RejectsEmptyAndMissingSourceInputs()
    {
        using var fixture = new SyntheticTargetFixture();
        var prepared = fixture.Prepare();
        var output = Path.Combine(fixture.DirectoryPath, "out.dll");
        Assert.Throws<InvalidDataException>(() => new SourceCompiler().Compile(prepared, new PatchManifest(), output));
        var missing = Path.Combine(fixture.DirectoryPath, "missing.cs");
        var error = Assert.Throws<FileNotFoundException>(() => new SourceCompiler().Compile(prepared, new PatchManifest { Sources = [missing] }, output));
        Assert.Equal(missing, error.FileName);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void RejectsAnUnsupportedFrameworkBeforeCompilation()
    {
        using var fixture = new SyntheticTargetFixture();
        var prepared = fixture.Prepare(); prepared.TargetFramework = "net999";
        Assert.Throws<NotSupportedException>(() => new SourceCompiler().Compile(prepared, WriteSource(fixture, "public class Example {}"), Path.Combine(fixture.DirectoryPath, "out.dll")));
    }

    [Theory]
    [InlineData("different-version")]
    [InlineData("different-content")]
    public void RejectsAmbiguousReferenceAssemblyInputs(string difference)
    {
        using var fixture = new SyntheticTargetFixture();
        var prepared = fixture.Prepare();
        var first = Path.Combine(fixture.DirectoryPath, "refs-a"); var second = Path.Combine(fixture.DirectoryPath, "refs-b");
        Directory.CreateDirectory(first); Directory.CreateDirectory(second);
        WriteReference(Path.Combine(first, "Dependency.dll"), 1, "First");
        WriteReference(Path.Combine(second, "Dependency.dll"), difference == "different-version" ? 2 : 1, "Second");
        prepared.ReferenceDirectories = [first, second];
        Assert.Contains("Conflicting", Assert.Throws<InvalidDataException>(() => new SourceCompiler().Compile(prepared,
            WriteSource(fixture, "public class Example {}"), Path.Combine(fixture.DirectoryPath, "out.dll"))).Message);
    }

    [Fact]
    public void IgnoresNativeLibrariesAndAcceptsByteIdenticalReferenceDuplicates()
    {
        using var fixture = new SyntheticTargetFixture();
        var prepared = fixture.Prepare();
        var first = Path.Combine(fixture.DirectoryPath, "refs-a"); var second = Path.Combine(fixture.DirectoryPath, "refs-b");
        Directory.CreateDirectory(first); Directory.CreateDirectory(second);
        WriteReference(Path.Combine(first, "Dependency.dll"), 1, "Shared");
        File.Copy(Path.Combine(first, "Dependency.dll"), Path.Combine(second, "Dependency.dll"));
        File.WriteAllText(Path.Combine(first, "native.dll"), "not a PE image");
        File.WriteAllText(Path.Combine(first, "System.EnterpriseServices.Wrapper.dll"), "native wrapper");
        File.WriteAllText(Path.Combine(first, "System.EnterpriseServices.Thunk.dll"), "native thunk");
        prepared.ReferenceDirectories = [first, second];
        var output = Path.Combine(fixture.DirectoryPath, "out.dll");
        new SourceCompiler().Compile(prepared, WriteSource(fixture, "public class Example { public Shared Reference; }"), output);
        using var assembly = AssemblyDefinition.ReadAssembly(output);
        Assert.Equal("Shared", assembly.MainModule.GetType("Example").Fields.Single().FieldType.FullName);
    }

    [Fact]
    public void BuildRecordsSourceAndArtifactHashesAndExecutesTheGraftedHelperChain()
    {
        using var fixture = new SyntheticTargetFixture();
        var prepared = fixture.Prepare();
        var manifest = WriteSource(fixture, "namespace Patch { public static class Replacement { public static int Compute() => Double(20) + 2; static int Double(int x) => x * 2; static int Unused() => 999; } }");
        var profilePath = Path.Combine(fixture.DirectoryPath, "prepared.json"); SourceBuilderJson.Save(profilePath, prepared);
        var manifestPath = Path.Combine(fixture.DirectoryPath, "manifest.json"); SourceBuilderJson.Save(manifestPath, manifest);
        var output = Path.Combine(fixture.DirectoryPath, "build");
        var report = new BuildPipeline().Build(profilePath, manifestPath, output);
        Assert.Equal(42, Assembly.Load(File.ReadAllBytes(report.GraftedAssembly)).GetType("Example.Calculator")!.GetMethod("Compute")!.Invoke(null, null));
        Assert.Contains("::Double(", Assert.Single(report.Helpers).Implementation);
        var source = Assert.Single(report.Sources);
        Assert.Equal(manifest.Sources[0], source.Path);
        Assert.Equal(Hash(source.Path), source.Sha256);
        Assert.Equal(Hash(profilePath), report.PreparedProfileSha256);
        Assert.Equal(Hash(manifestPath), report.ManifestSha256);
        Assert.Equal(Hash(report.ImplementationAssembly), report.ImplementationAssemblySha256);
        Assert.Equal(Hash(report.GraftedAssembly), report.GraftedAssemblySha256);
        Assert.Equal(Hash(report.ProcessConfig), report.ProcessConfigSha256);
        var persisted = SourceBuilderJson.Load<BuildReport>(Path.Combine(output, "build-report.json"));
        Assert.Equal(report.GraftedAssemblySha256, persisted.GraftedAssemblySha256);
        Assert.Equal(3, Assert.Single(persisted.Bindings).NativeSlot);
    }

    [Fact]
    public void BuildRejectsAnImplementationAssemblyNamedAfterTheTarget()
    {
        using var fixture = new SyntheticTargetFixture(); var prepared = fixture.Prepare();
        var manifest = WriteSource(fixture, "public class Example {}"); manifest.AssemblyName = prepared.TargetAssemblyName.ToUpperInvariant();
        var profilePath = Path.Combine(fixture.DirectoryPath, "prepared.json"); SourceBuilderJson.Save(profilePath, prepared);
        var manifestPath = Path.Combine(fixture.DirectoryPath, "manifest.json"); SourceBuilderJson.Save(manifestPath, manifest);
        Assert.Contains("must differ", Assert.Throws<InvalidDataException>(() => new BuildPipeline().Build(profilePath, manifestPath, Path.Combine(fixture.DirectoryPath, "build"))).Message);
    }

    internal static PatchManifest WriteSource(SyntheticTargetFixture fixture, string source)
    {
        var path = Path.Combine(fixture.DirectoryPath, "PatchImpl.cs"); File.WriteAllText(path, source);
        return new PatchManifest { AssemblyName = "SyntheticImplementation", Sources = [path], Patches = [new PatchBinding { Target = "System.Int32 Example.Calculator::Compute()", Implementation = "System.Int32 Patch.Replacement::Compute()" }] };
    }

    internal static (BodyGrafter Grafter, string Output) CompileAndGraft(SyntheticTargetFixture fixture, PreparedProfile prepared, PatchManifest manifest)
    {
        var implementation = Path.Combine(fixture.DirectoryPath, "SyntheticImplementation.dll");
        new SourceCompiler().Compile(prepared, manifest, implementation);
        var output = Path.Combine(fixture.DirectoryPath, "grafted.dll"); var grafter = new BodyGrafter();
        grafter.Graft(prepared, manifest, implementation, output, Path.Combine(fixture.DirectoryPath, "process.cfg"));
        return (grafter, output);
    }

    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static void WriteReference(string path, int version, string className)
    {
        using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Dependency", new Version(version, 0)), "Dependency", ModuleKind.Dll);
        SyntheticTargetFixture.Class(assembly.MainModule, "", className); assembly.Write(path);
    }
}
