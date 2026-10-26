namespace IFixPatch.Core.Tests;

public sealed class DumpCsTests
{
    // Synthetic Il2CppDumper layout: inherited members are absent from each // Methods block.
    private const string Dump = """
        // Namespace: Example
        public interface IBase // TypeDefIndex: 1
        {
            // Methods
            public abstract void Inherited();
        }
        // Namespace: Example
        public interface IValue : IBase // TypeDefIndex: 2
        {
            // Fields
            public const int Constant = 2;
            // Properties
            public int Value { get; }
            // Methods
            public abstract int Read();
            public static int Create() { }
            // GenericInstMethod :
            | public abstract void NotADeclaration();
            /* public abstract void AlsoNotADeclaration(); */
        }
        // Namespace: Example
        public interface IValue<T> // TypeDefIndex: 3
        {
            // Methods
            public abstract T Read();
            public abstract void Write(T value);
        }
        // Namespace: Example
        public interface IMap<TKey, TValue> // TypeDefIndex: 4
        {
            // Methods
            public abstract TValue Get(TKey key);
        }
        // Namespace:
        public interface Outer.Inner<T> // TypeDefIndex: 5
        {
            // Methods
            public abstract void Invoke(T value);
        }
        // Namespace: Example
        public class Calculator // TypeDefIndex: 6
        {
            // Methods
            public Calculator() { }
            public static int Same(int value) { }
            public int Same(string value) { }
            public string Different(int value) { }
            public int Different(string value) { }
            public Dictionary<string, List<int[]>> Convert<T>(Dictionary<string, List<int[]>> input, ref int count, bool enabled = False) { }
            public int this[int index] { get; }
            // Events
            public void NotAMethod() { }
        }
        """;

    [Theory]
    [InlineData("Example.IBase, Synthetic", 1)]
    [InlineData("Example.IValue, Synthetic", 1)]
    [InlineData("Example.IValue`1[[System.Int32, mscorlib]], Synthetic", 2)]
    [InlineData("Example.IMap`2[[System.String, mscorlib],[System.Int32, mscorlib]], Synthetic", 1)]
    [InlineData("Example.Outer+Inner`1[[System.Int32, mscorlib]], Synthetic", 1)]
    public void InterfaceCountsUseDeclaredMethodsAndGenericArity(string type, int expected)
    {
        using var directory = new TemporaryDirectory();
        var index = Read(directory, Dump);

        Assert.True(index.TryGetDeclaredInstanceMethodCount(type, out var count));
        Assert.Equal(expected, count);
    }

    [Fact]
    public void UnknownTypeAndUnknownSignatureDoNotInventMetadata()
    {
        using var directory = new TemporaryDirectory();
        var index = Read(directory, Dump);

        Assert.False(index.TryGetDeclaredInstanceMethodCount("Missing, Synthetic", out var count));
        Assert.Equal(0, count);
        Assert.False(index.TryGetReturnType("Missing", "Read", 0, out var missing, out var ambiguous));
        Assert.Empty(missing);
        Assert.False(ambiguous);
        Assert.False(index.TryGetReturnType("Example.Calculator", "Same", 2, out _, out ambiguous));
        Assert.False(ambiguous);
    }

    [Fact]
    public void SameArityOverloadsAreResolvedOnlyWhenReturnTypesAgree()
    {
        using var directory = new TemporaryDirectory();
        var index = Read(directory, Dump);

        Assert.True(index.TryGetReturnType("Example.Calculator, Synthetic", "Same", 1, out var result, out var ambiguous));
        Assert.Equal("int", result);
        Assert.False(ambiguous);
        Assert.False(index.TryGetReturnType("Example.Calculator", "Different", 1, out _, out ambiguous));
        Assert.True(ambiguous);
        Assert.False(index.TryGetReturnType("Example.Calculator", "Calculator", 0, out _, out _));
        Assert.False(index.TryGetReturnType("Example.Calculator", "NotAMethod", 0, out _, out _));
    }

    [Fact]
    public void SignatureParsingKeepsNestedGenericTypesAndIgnoresParameterNamesAndDefaults()
    {
        using var directory = new TemporaryDirectory();
        var index = Read(directory, Dump);

        Assert.True(index.TryGetReturnType("Example.Calculator", "Convert", 3, out var result, out var ambiguous));
        Assert.Equal("Dictionary<string, List<int[]>>", result);
        Assert.False(ambiguous);
        Assert.True(index.TryGetReturnType("Example.Outer+Inner`1[[System.Int32, mscorlib]], Synthetic", "Invoke", 1, out result, out ambiguous));
        Assert.Equal("void", result);
        Assert.False(ambiguous);
    }

    [Theory]
    [InlineData(" Example.IMap`2[[System.String, mscorlib],[System.Int32, mscorlib]], Synthetic, Version=1.0.0.0 ", "Example.IMap`2")]
    [InlineData("Example.Outer`1+Inner`2[[A, Assembly],[B, Assembly],[C, Assembly]], Synthetic", "Example.Outer`1+Inner`2")]
    [InlineData(" Example.Calculator ", "Example.Calculator")]
    public void AssemblySuffixAndClosedGenericArgumentsAreRemovedAtOuterLevel(string input, string expected)
        => Assert.Equal(expected, DumpCsIndex.NormalizeAqn(input));

    [Fact]
    public void EndOfFileAndNextTypeFlushDeclarationsWithoutClosingBrace()
    {
        using var directory = new TemporaryDirectory();
        var index = Read(directory, """
            // Namespace: Example
            public interface IFirst
                // Methods
                public abstract void A();
            public interface ISecond
                // Methods
                public abstract void B();
                public abstract void C();
            """);

        Assert.Equal(2, index.TypeCount);
        Assert.Equal(3, index.MethodCount);
        Assert.True(index.TryGetDeclaredInstanceMethodCount("Example.IFirst", out var first));
        Assert.Equal(1, first);
        Assert.True(index.TryGetDeclaredInstanceMethodCount("Example.ISecond", out var second));
        Assert.Equal(2, second);
    }

    [Fact]
    public void DumpSourceProvidesPartialMetadataWithoutAnOpcodeEnum()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(directory.File("metadata.cs"), Dump);
        var source = new DumpCsSource(directory.File("metadata.cs"));

        Assert.False(source.SupportsTypedReferences);
        Assert.Null(source.TryGetOpCodeMap());
        Assert.Contains("metadata.cs", source.Describe());
        Assert.True(source.TryGetDeclaredInstanceMethodCount("Example.IValue", out var count));
        Assert.Equal(1, count);
        Assert.True(source.TryGetReturnType("Example.Calculator", "Same", 1, out var result, out var ambiguous));
        Assert.Equal("int", result);
        Assert.False(ambiguous);
    }

    [Fact]
    public void DumpSourceExposesOpcodeMapWhenPresent()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(directory.File("metadata.cs"), Dump + """

            // Namespace: IFix.Core
            public enum Code
            {
                public const Code Nop = 29;
                public const Code Ret = 3;
            }
            """);
        var source = new DumpCsSource(directory.File("metadata.cs"));

        Assert.Equal("Ret", source.TryGetOpCodeMap()!.Name(3));
        Assert.Equal("Nop", source.TryGetOpCodeMap()!.Name(29));
    }

    [Fact]
    public void SlotCountsMatchDeclaredPublicInstanceMethodsIncludingDefaultBodies()
    {
        using var directory = new TemporaryDirectory();
        var index = Read(directory, """
            // Namespace: Example
            public interface IModern
            {
                // Methods
                public abstract void Required();
                public void DefaultBody() { }
                protected abstract void Protected();
                internal abstract void Internal();
                private void Helper() { }
                public static void Factory() { }
            }
            // Namespace: Example
            public class Owner
            {
                // Methods
                public Owner() { }
                public void .ctor() { }
                public static void .cctor() { }
                public void Run() { }
                private void Helper() { }
            }
            """);

        Assert.True(index.TryGetDeclaredInstanceMethodCount("Example.IModern", out var interfaceCount));
        Assert.Equal(2, interfaceCount);
        Assert.True(index.TryGetDeclaredInstanceMethodCount("Example.Owner", out var classCount));
        Assert.Equal(1, classCount);
    }

    private static DumpCsIndex Read(TemporaryDirectory directory, string text)
    {
        var path = directory.File("dump.cs");
        File.WriteAllText(path, text);
        return DumpCsIndex.FromFile(path);
    }
}
