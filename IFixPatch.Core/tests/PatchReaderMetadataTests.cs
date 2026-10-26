using System.Text;

namespace IFixPatch.Core.Tests;

public sealed class PatchReaderMetadataTests
{
    [Fact]
    public void DumpInterfaceMetadataKeepsTheFollowingVtableAndSectionsAligned()
    {
        using var directory = new TemporaryDirectory();
        var source = Metadata(directory, "Example");
        using var input = BinaryPatch(0);

        var patch = new PatchReader(input, source).Read();

        Assert.True(patch.FullyParsed);
        Assert.Equal(0, patch.TrailingBytes);
        Assert.Null(patch.ParseNote);
        var storey = Assert.Single(patch.AnonStoreys);
        Assert.True(storey.HasInterfaces);
        Assert.Equal(0, Assert.Single(storey.Interfaces).TypeId);
        Assert.Equal(new[] { 0 }, storey.Interfaces[0].MethodIds);
        Assert.Equal(new[] { -1, -1, 0 }, storey.VTable);
        Assert.Equal("Example.Manager, Synthetic", patch.WrapperManagerType);
        Assert.Equal(new[] { "Example.Added, Synthetic" }, patch.NewClasses);
    }

    [Theory]
    [InlineData(0, "Other")]
    [InlineData(7, "Example")]
    [InlineData(-1, "Example")]
    public void UnknownInterfaceStopsBeforeReadingAnUnknownNumberOfSlots(int typeId, string metadataNamespace)
    {
        using var directory = new TemporaryDirectory();
        var source = Metadata(directory, metadataNamespace);
        using var input = BinaryPatch(typeId);

        var patch = new PatchReader(input, source).Read();

        Assert.False(patch.FullyParsed);
        Assert.Contains($"type#{typeId}", patch.ParseNote);
        Assert.Empty(Assert.Single(patch.AnonStoreys).Interfaces);
        Assert.Empty(patch.WrapperManagerType);
        Assert.Empty(patch.NewClasses);
    }

    private static DumpCsSource Metadata(TemporaryDirectory directory, string ns)
    {
        var path = directory.File("dump.cs");
        File.WriteAllText(path, $"// Namespace: {ns}\npublic interface IOne\n{{\n\t// Methods\n\tpublic abstract void Run();\n}}\n");
        return new DumpCsSource(path);
    }

    // Direct encoding follows PatchManager.Load/readSlotInfo's sequential binary format.
    // This intentionally does not invoke PatchWriter, so writer/reader agreement is not the oracle.
    private static MemoryStream BinaryPatch(int interfaceId)
    {
        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0x123456789ABCDEF0UL);
            writer.Write("Example.Bridge, Synthetic");
            writer.Write(1); // extern type count
            writer.Write("Example.IOne, Synthetic");
            writer.Write(1); // method count
            writer.Write(2); // code slots
            writer.Write(100); writer.Write(0); // StackSpace (shuffled ID irrelevant to reader)
            writer.Write(128); writer.Write(0); // Ret
            writer.Write(0); // EH count
            writer.Write(0); // extern method count
            writer.Write(0); // string count
            writer.Write(0); // field count
            writer.Write(0); // VM static field count
            writer.Write(1); // anonymous storey count
            writer.Write(0); // storey field count
            writer.Write(0); // constructor method id
            writer.Write(0); // constructor arg count
            writer.Write(1); // interface count
            writer.Write(interfaceId);
            writer.Write(0); // exactly one declared public instance interface slot
            writer.Write(3); // vtable length (not interface method count)
            writer.Write(-1); writer.Write(-1); writer.Write(0);
            writer.Write("Example.Manager, Synthetic");
            writer.Write(", Synthetic");
            writer.Write(0); // fix count
            writer.Write(1); // new class count
            writer.Write("Example.Added, Synthetic");
        }
        stream.Position = 0;
        return stream;
    }
}
