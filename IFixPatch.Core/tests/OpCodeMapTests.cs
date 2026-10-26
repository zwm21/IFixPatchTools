namespace IFixPatch.Core.Tests;

public sealed class OpCodeMapTests
{
    [Fact]
    public void PlainMappingAcceptsDocumentedSeparatorsHexAndComments()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(directory.File("opcodes.txt"), """
            # shuffled mapping
            // another comment
            Nop = 0x7F
            Ret: 0X10
            Pop -3
            Ldstr 42
            """);

        var map = OpCodeMap.FromFile(directory.File("opcodes.txt"));

        Assert.Equal(4, map.Count);
        Assert.False(map.IsEmpty);
        Assert.Equal("Nop", map.Name(127));
        Assert.Equal("Ret", map.Name(16));
        Assert.True(map.TryName(-3, out var name));
        Assert.Equal("Pop", name);
        Assert.Equal("code#12345", map.Name(12345));
        Assert.False(map.TryName(12345, out _));
        Assert.Equal(new[] { -3, 16, 42, 127 }, map.Enumerate().Select(kv => kv.Key).Order().ToArray());
    }

    [Fact]
    public void DumpMappingSelectsTheIFixNamespaceEvenWithAnotherCodeEnum()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(directory.File("dump.CS"), """
            // Namespace: Unrelated
            public enum Code
            {
                public const Code Wrong = 1;
            }
            // Namespace: IFix.Core
            [Serializable]
            public enum Code
            {
                public const Code Nop = 0x2A;
                public const Code Ret = 7;
            }
            // Namespace: Other
            public enum Code
            {
                public const Code AlsoWrong = 99;
            }
            """);

        var map = OpCodeMap.FromFile(directory.File("dump.CS"));

        Assert.Equal(2, map.Count);
        Assert.Equal("Nop", map.Name(42));
        Assert.Equal("Ret", map.Name(7));
        Assert.Empty(map.Validate().Unknown);
    }

    [Fact]
    public void NamespaceQualifiedSourcePreservesEnumIdentity()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(directory.File("dump.cs"), "namespace IFix.Core { public enum Code { public const Code Break = 8; } }");

        Assert.Equal("Break", OpCodeMap.FromFile(directory.File("dump.cs")).Name(8));
    }

    [Fact]
    public void FileScopedNamespacePreservesEnumIdentity()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(directory.File("dump.cs"), "namespace IFix.Core; [Serializable] public enum Code { public const Code Break = 8; }");

        Assert.Equal("Break", OpCodeMap.FromFile(directory.File("dump.cs")).Name(8));
    }

    [Theory]
    [InlineData("// Namespace: IFix.Core\npublic class Marker {}\n// Namespace: Unrelated\npublic enum Code { public const Code Wrong = 1; }")]
    [InlineData("namespace IFix.Core { public class Marker {} } namespace Unrelated { public enum Code { public const Code Wrong = 1; } }")]
    [InlineData("// Mentioning IFix.Core is not a namespace declaration.\npublic enum Code { public const Code Wrong = 1; }")]
    public void NearbyNamespaceTextCannotQualifyAnUnrelatedEnum(string source)
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(directory.File("dump.cs"), source);

        Assert.Throws<InvalidOperationException>(() => OpCodeMap.FromFile(directory.File("dump.cs")));
    }

    [Theory]
    [InlineData("empty.txt", "// comments only\n# no literals")]
    [InlineData("dump.cs", "public enum Code { public const Code Nop = 1; }")]
    [InlineData("dump.cs", "// Namespace: IFix.Core\npublic enum Code { }")]
    [InlineData("dump.cs", "// Namespace: IFix.Core\npublic enum Code")]
    [InlineData("dump.cs", "// Namespace: IFix.Core\npublic enum Code { public const Code Nop = 1;")]
    [InlineData("dump.cs", "// Namespace: Unrelated\npublic enum Code { public const Code Nop = 1; }")]
    public void MissingOrUnscopedMappingsFailInsteadOfGuessing(string filename, string content)
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(directory.File(filename), content);

        Assert.Throws<InvalidOperationException>(() => OpCodeMap.FromFile(directory.File(filename)));
    }

    [Fact]
    public void ValidationReportsBothCollisionKindsAndUnknownMnemonic()
    {
        var map = OpCodeMap.FromPairs([(1, "Nop"), (2, "Nop"), (2, "Ret"), (3, "Typo")]);

        var validation = map.Validate();

        Assert.False(validation.IsComplete);
        Assert.Equal(181, validation.CanonicalCount); // Fixed upstream IFix.Core.Code contract.
        Assert.Equal(3, validation.MappedCount);
        Assert.Equal(new[] { "Typo" }, validation.Unknown);
        Assert.Single(validation.NameConflicts);
        Assert.Contains("Nop", validation.NameConflicts[0]);
        Assert.Single(validation.ValueConflicts);
        Assert.Contains("Ret", validation.ValueConflicts[0]);
        Assert.Contains("StackSpace", validation.Missing);
        Assert.DoesNotContain("Nop", validation.Missing);
        Assert.DoesNotContain("Ret", validation.Missing);
    }

    [Fact]
    public void ReloadReplacesMappingsAndClearsOldConflicts()
    {
        var map = OpCodeMap.FromPairs([(1, "Nop"), (1, "Ret"), (2, "Ret")]);

        map.LoadFromNameToValue(new Dictionary<string, int> { ["Nop"] = 17, ["Ret"] = 23 });

        Assert.Equal(2, map.Count);
        Assert.False(map.TryName(1, out _));
        Assert.Equal("Nop", map.Name(17));
        Assert.Equal("Ret", map.Name(23));
        Assert.Empty(map.Validate().NameConflicts);
        Assert.Empty(map.Validate().ValueConflicts);
    }

    [Theory]
    [InlineData("-0x1", -1)]
    [InlineData("-0x80000000", int.MinValue)]
    [InlineData("0xFFFFFFFF", -1)]
    [InlineData("0x80000000", int.MinValue)]
    [InlineData("2147483647", int.MaxValue)]
    public void IntegerLiteralsPreserveSigned32BitValues(string literal, int expected)
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(directory.File("opcodes.txt"), $"Nop = {literal}");

        Assert.Equal("Nop", OpCodeMap.FromFile(directory.File("opcodes.txt")).Name(expected));
    }

    [Theory]
    [InlineData("0x100000000")]
    [InlineData("-0x80000001")]
    [InlineData("2147483648")]
    [InlineData("-2147483649")]
    public void OutOfRangeIntegerLiteralsCannotWrapIntoAnotherOpcode(string literal)
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(directory.File("opcodes.txt"), $"Nop = {literal}");

        Assert.Throws<OverflowException>(() => OpCodeMap.FromFile(directory.File("opcodes.txt")));
    }

    [Fact]
    public void RepeatedIdenticalMappingIsNotAConflict()
    {
        var map = OpCodeMap.FromPairs([(4, "Nop"), (4, "Nop")]);

        Assert.Single(map.Enumerate());
        Assert.Empty(map.Validate().NameConflicts);
        Assert.Empty(map.Validate().ValueConflicts);
    }
}
