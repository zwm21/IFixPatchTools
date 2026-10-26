using Mono.Cecil;

namespace IFixPatch.Core.Tests;

public sealed class MetadataSourceTests
{
    [Fact]
    public void MetadataSearchContinuesPastAnEmptyFirstDirectory()
    {
        using var directory = new TemporaryDirectory();
        var empty = Directory.CreateDirectory(directory.File("empty")).FullName;
        var metadata = Directory.CreateDirectory(directory.File("metadata")).FullName;
        WriteAssembly(Path.Combine(metadata, "Synthetic.dll"));
        using var source = new DummyDllSource([empty, metadata]);

        Assert.NotNull(source.FindType("Example.Calculator, Synthetic"));
        Assert.Equal(2, source.DllDirectories.Count);
        Assert.Empty(source.Warnings);
    }

    [Fact]
    public void ExplicitMetadataUsesInternalAssemblyIdentityWhenFileIsRenamed()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("renamed-input.dll");
        WriteAssembly(path);
        using var source = new DummyDllSource([directory.Path], [path]);

        var type = source.FindType("Example.Calculator, Synthetic");

        Assert.NotNull(type);
        Assert.Equal("Synthetic", type.Module.Assembly.Name.Name);
        Assert.NotNull(source.Resolver.Resolve(new AssemblyNameReference("Synthetic", new Version(1, 0))).MainModule.GetType("Example.Calculator"));
    }

    [Fact]
    public void ConflictingExplicitAssemblyIdentitiesAreRejected()
    {
        using var directory = new TemporaryDirectory();
        var first = directory.File("first.dll");
        var second = directory.File("second.dll");
        WriteAssembly(first);
        WriteAssembly(second, new Version(2, 0));

        Assert.Throws<InvalidDataException>(() => new DummyDllSource([directory.Path], [first, second]));
    }

    [Theory]
    [InlineData("Example.Outer+Inner, Synthetic, Version=1.0.0.0", "Example.Outer+Inner", "Synthetic")]
    [InlineData("System.Collections.Generic.List`1[[System.Int32, mscorlib]], mscorlib", "System.Collections.Generic.List`1", "mscorlib")]
    [InlineData("Example.Calculator", "Example.Calculator", null)]
    public void AssemblyQualifiedNamesSplitAtTheOuterComma(string aqn, string expectedType, string? expectedAssembly)
    {
        var (type, assembly) = DummyDllSource.SplitAqn(aqn);

        Assert.Equal(expectedType, type);
        Assert.Equal(expectedAssembly, assembly);
    }

    private static void WriteAssembly(string path, Version? version = null)
    {
        using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Synthetic", version ?? new Version(1, 0)), "Synthetic", ModuleKind.Dll);
        assembly.MainModule.Types.Add(new TypeDefinition("Example", "Calculator", TypeAttributes.Public, assembly.MainModule.TypeSystem.Object));
        assembly.Write(path);
    }
}
