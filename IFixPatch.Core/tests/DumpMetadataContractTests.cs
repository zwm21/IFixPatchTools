namespace IFixPatch.Core.Tests;

public sealed class DumpMetadataContractTests
{
    [Theory]
    [InlineData("public List<int Broken() { }")]
    [InlineData("public();")]
    public void InvalidDumpSignaturesCannotInventCallableMembers(string declaration)
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("dump.cs");
        File.WriteAllText(path, "// Namespace: Example\npublic class Owner\n{\n // Methods\n " + declaration + "\n}\n");
        var index = DumpCsIndex.FromFile(path);
        Assert.True(index.TryGetDeclaredInstanceMethodCount("Example.Owner, Synthetic", out var count));
        Assert.Equal(0, count);
        Assert.False(index.TryGetReturnType("Example.Owner, Synthetic", "Broken", 0, out _, out _));
    }

    [Fact]
    public void DumpMetadataKeepsTupleParameterCommasInsideOneParameterAndFindsGlobalNestedTypes()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("dump.cs");
        File.WriteAllText(path, """
            // Namespace:
            public class Outer.Inner<T>
            {
                // Methods
                public int Read((int x, int y) coordinates, T value) { }
            }
            """);
        var index = DumpCsIndex.FromFile(path);
        Assert.True(index.TryGetDeclaredInstanceMethodCount("Outer+Inner<System.Int32>, Synthetic", out var count));
        Assert.Equal(1, count);
        Assert.False(index.TryGetDeclaredInstanceMethodCount("Missing+Inner, Synthetic", out _));
        Assert.True(index.TryGetReturnType("Outer+Inner<System.Int32>, Synthetic", "Read", 2, out var result, out var ambiguous));
        Assert.Equal("int", result);
        Assert.False(ambiguous);
    }
}
