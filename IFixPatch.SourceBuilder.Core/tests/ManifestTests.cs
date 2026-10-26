namespace IFixPatch.SourceBuilder.Core.Tests;

public sealed class ManifestTests
{
    [Fact]
    public void ResolvesSourcesRelativeToTheirManifestWithStableDeduplication()
    {
        using var directory = new ManifestDirectoryFixture();
        File.WriteAllText(Path.Combine(directory.DirectoryPath, "B.cs"), "// B");
        File.WriteAllText(Path.Combine(directory.DirectoryPath, "A.cs"), "// A");
        Directory.CreateDirectory(Path.Combine(directory.DirectoryPath, "nested"));
        File.WriteAllText(Path.Combine(directory.DirectoryPath, "nested", "C.cs"), "// C");
        var result = PathAndHash.ResolveSources(Path.Combine(directory.DirectoryPath, "patch.json"), ["B.cs", "*.cs", "**/*.cs"]);
        Assert.Equal(new[] { "B.cs", "A.cs", Path.Combine("nested", "C.cs") }, result.Select(path => Path.GetRelativePath(directory.DirectoryPath, path)));
    }

    [Fact]
    public void RejectsUnmatchedSourcePatternsInsteadOfBuildingAnEmptyPatch()
    {
        using var directory = new ManifestDirectoryFixture();
        Assert.Contains("matched no files", Assert.Throws<InvalidDataException>(() =>
            PathAndHash.ResolveSources(Path.Combine(directory.DirectoryPath, "patch.json"), ["*.cs"])).Message);
    }

    [Theory]
    [InlineData("*/Patch.cs")]
    [InlineData("**/nested/Patch.cs")]
    public void RejectsDirectoryPatternsWhoseExpansionIsUnsupported(string pattern)
    {
        using var directory = new ManifestDirectoryFixture();
        Assert.Throws<NotSupportedException>(() => PathAndHash.ResolveSources(Path.Combine(directory.DirectoryPath, "patch.json"), [pattern]));
    }

    private sealed class ManifestDirectoryFixture : IDisposable
    {
        public string DirectoryPath { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ifix-manifest-unit-" + Guid.NewGuid().ToString("N"));
        public ManifestDirectoryFixture() => Directory.CreateDirectory(DirectoryPath);
        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }
}
