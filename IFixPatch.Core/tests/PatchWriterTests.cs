namespace IFixPatch.Core.Tests;

public sealed class PatchWriterTests
{
    [Fact]
    public void FailedAtomicReplacementPreservesTheDestinationAndRemovesTheTemporaryFile()
    {
        using var directory = new TemporaryDirectory();
        var output = directory.File("occupied");
        Directory.CreateDirectory(output);
        var marker = Path.Combine(output, "existing.txt");
        File.WriteAllText(marker, "preserve");

        Assert.ThrowsAny<IOException>(() => PatchWriter.WriteValidatedFile(output, PatchTestData.Patch(), PatchTestData.Map()));

        Assert.Equal("preserve", File.ReadAllText(marker));
        Assert.Empty(Directory.GetFiles(directory.Path));
        Assert.Equal(new[] { marker }, Directory.GetFiles(output));
    }
}
