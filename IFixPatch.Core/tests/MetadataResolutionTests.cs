using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IFixPatch.Core.Tests;

public sealed class MetadataResolutionTests
{
    [Fact]
    public void UnknownUnqualifiedNamesRemainUnresolvedAfterOtherTypesAreLoaded()
    {
        using var directory = new TemporaryDirectory();
        WriteMetadata(directory);
        using var source = new DummyDllSource(directory.Path);
        Assert.NotNull(source.FindType("Example.Value, Synthetic"));

        Assert.Null(source.FindType("Example.Absent"));
        Assert.Null(source.FindType("Example.Absent"));
    }

    [Fact]
    public void UnqualifiedMetadataSearchSkipsPreviouslyMissingAssemblies()
    {
        using var directory = new TemporaryDirectory();
        WriteMetadata(directory);
        using var source = new DummyDllSource(directory.Path);
        Assert.Null(source.FindType("Example.Absent, Missing"));
        Assert.NotNull(source.FindType("Example.Value, Synthetic"));

        var type = source.FindTypeByCecilName("Example.Value", null);

        Assert.NotNull(type);
        Assert.Equal("Synthetic", type.Module.Assembly.Name.Name);
    }

    [Theory]
    [InlineData("Example.Value, Synthetic", "Example.Value")]
    [InlineData("Example.Outer+Inner, Synthetic", "Example.Outer/Inner")]
    [InlineData("Example.Value[], Synthetic", "Example.Value[]")]
    [InlineData("Example.Value[,], Synthetic", "Example.Value[,]")]
    [InlineData("Example.Value&, Synthetic", "Example.Value&")]
    [InlineData("Example.Value*, Synthetic", "Example.Value*")]
    [InlineData("Example.Box`1[[Example.Value, Synthetic]], Synthetic", "Example.Box`1<Example.Value>")]
    public void StructuredAqnResolutionPreservesTypeShape(string aqn, string expected)
    {
        using var directory = new TemporaryDirectory();
        WriteMetadata(directory);
        using var source = new DummyDllSource(directory.Path);
        using var destination = ModuleDefinition.CreateModule("Output", ModuleKind.Dll);
        var resolver = new AqnResolver(source, destination);

        Assert.Equal(expected, resolver.Resolve(aqn)?.FullName);
        Assert.Equal(expected, resolver.Resolve(aqn)?.FullName);
    }

    [Theory]
    [InlineData("Example.Missing, Synthetic")]
    [InlineData("Example.Missing[], Synthetic")]
    [InlineData("Example.Missing&, Synthetic")]
    [InlineData("Example.Missing*, Synthetic")]
    [InlineData("Example.Missing`1[[Example.Value, Synthetic]], Synthetic")]
    [InlineData("Example.Box`1[[Example.Missing, Synthetic]], Synthetic")]
    [InlineData("Example.Value[[")]
    public void UnknownOrMalformedTypesDoNotCreateUnresolvedReferences(string aqn)
    {
        using var directory = new TemporaryDirectory();
        WriteMetadata(directory);
        using var source = new DummyDllSource(directory.Path);
        using var destination = ModuleDefinition.CreateModule("Output", ModuleKind.Dll);
        var resolver = new AqnResolver(source, destination);

        Assert.Null(resolver.Resolve(aqn));
        Assert.Null(resolver.Resolve(aqn));
    }

    [Fact]
    public void MetadataQueriesResolveOverloadsByParametersAndReportReturnAmbiguity()
    {
        using var directory = new TemporaryDirectory();
        WriteMetadata(directory);
        using var source = new DummyDllSource(directory.Path);

        Assert.True(source.SupportsTypedReferences);
        Assert.Equal(directory.Path, source.DllDirectory);
        Assert.Contains("DummyDll", source.Describe());
        Assert.True(source.TryGetReturnType("Example.Owner, Synthetic", "Read", 1, out var result, out var ambiguous));
        Assert.Equal("System.Int32", result);
        Assert.False(ambiguous);
        Assert.False(source.TryGetReturnType("Example.Owner, Synthetic", "Different", 1, out _, out ambiguous));
        Assert.True(ambiguous);
        Assert.False(source.TryGetReturnType("Example.Owner, Synthetic", "Missing", 1, out _, out ambiguous));
        Assert.False(ambiguous);
        Assert.Equal("System.String", source.FindMethodByParamTypes("Example.Owner, Synthetic", "Read", ["System.String"], type => type.FullName)?.Parameters[0].ParameterType.FullName);
        Assert.Equal("System.Int32", source.FindMethodByParamTypes("Example.Owner, Synthetic", "Read", ["System.Int32"], type => type.FullName)?.Parameters[0].ParameterType.FullName);
        Assert.Equal("System.Int32", source.FindMethod("Example.Owner, Synthetic", "Only", 0)?.ReturnType.FullName);
        Assert.Equal("Only", source.FindMethodByParamTypes("Example.Owner, Synthetic", "Only", [], type => type.FullName)?.Name);
        Assert.Equal("Read", source.FindMethodByParamTypes("Example.Owner, Synthetic", "Read", [null], type => type.FullName)?.Name);
        Assert.Equal("System.Int32", source.FindField("Example.Owner, Synthetic", "Count")?.FieldType.FullName);
        Assert.Null(source.FindField("Example.Owner, Synthetic", "Missing"));
    }

    [Fact]
    public void MissingMetadataReturnsUnknownInsteadOfFabricatingMembers()
    {
        using var directory = new TemporaryDirectory();
        using var source = new DummyDllSource(directory.Path);

        Assert.False(source.TryGetDeclaredInstanceMethodCount("Missing.Type, Missing", out var count));
        Assert.Equal(0, count);
        Assert.Null(source.FindDeclaredInstanceMethods("Missing.Type, Missing"));
        Assert.False(source.TryGetReturnType("Missing.Type, Missing", "Run", 0, out var result, out var ambiguous));
        Assert.Empty(result);
        Assert.False(ambiguous);
        Assert.Null(source.FindMethodByParamTypes("Missing.Type, Missing", "Run", [], type => type.FullName));
        Assert.Null(source.FindMethod("Missing.Type, Missing", "Run", 0));
        Assert.Null(source.FindField("Missing.Type, Missing", "Count"));
        Assert.Null(source.FindTypeByCecilName("Missing.Type", "Missing"));
        Assert.Null(source.TryGetOpCodeMap());
        Assert.NotEmpty(source.Warnings);
    }

    [Fact]
    public void LoadedMetadataCanResolveNamesWithoutAnAssemblySuffix()
    {
        using var directory = new TemporaryDirectory();
        WriteMetadata(directory);
        using var source = new DummyDllSource(directory.Path);
        Assert.NotNull(source.FindType("Example.Value, Synthetic"));

        Assert.Equal("Example.Value", source.FindType("Example.Value")?.FullName);
        Assert.Equal("Example.Value", source.FindTypeByCecilName("Example.Value", null)?.FullName);
        Assert.Null(source.FindTypeByCecilName("Example.Missing", null));
    }

    [Theory]
    [InlineData("Missing")]
    [InlineData("Wrong")]
    [InlineData("Other")]
    public void QualifiedTypeLookupDoesNotSearchOtherLoadedAssemblies(string requestedAssembly)
    {
        using var directory = new TemporaryDirectory();
        using var source = PreloadTypeWithUnrelatedAssemblies(directory);

        Assert.Null(source.FindType($"Example.Value, {requestedAssembly}"));
        Assert.Null(source.FindType($"Example.Value, {requestedAssembly}"));
        Assert.Equal("Synthetic", source.FindType("Example.Value")?.Module.Assembly.Name.Name);
    }

    [Theory]
    [InlineData("Missing")]
    [InlineData("Wrong")]
    [InlineData("Other")]
    public void QualifiedCecilLookupDoesNotSearchOtherLoadedAssemblies(string requestedAssembly)
    {
        using var directory = new TemporaryDirectory();
        using var source = PreloadTypeWithUnrelatedAssemblies(directory);

        Assert.Null(source.FindTypeByCecilName("Example.Value", requestedAssembly));
        Assert.Equal("Synthetic", source.FindTypeByCecilName("Example.Value", null)?.Module.Assembly.Name.Name);
    }

    [Theory]
    [InlineData("Missing")]
    [InlineData("Wrong")]
    [InlineData("Other")]
    public void AqnResolutionKeepsAnUnresolvedAssemblyQualifierUnresolved(string requestedAssembly)
    {
        using var directory = new TemporaryDirectory();
        using var source = PreloadTypeWithUnrelatedAssemblies(directory);
        using var destination = ModuleDefinition.CreateModule("Output", ModuleKind.Dll);
        var resolver = new AqnResolver(source, destination);

        Assert.Null(resolver.Resolve($"Example.Value, {requestedAssembly}"));
        Assert.Null(resolver.Resolve($"Example.Value, {requestedAssembly}"));
        Assert.Equal("Example.Value", resolver.Resolve("Example.Value")?.FullName);
    }

    private static DummyDllSource PreloadTypeWithUnrelatedAssemblies(TemporaryDirectory directory)
    {
        WriteMetadata(directory);
        File.Copy(directory.File("Synthetic.dll"), directory.File("Wrong.dll"));
        using (var other = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Other", new Version(1, 0)), "Other", ModuleKind.Dll))
        {
            other.MainModule.Types.Add(new TypeDefinition("Example", "Unrelated", TypeAttributes.Public, other.MainModule.TypeSystem.Object));
            other.Write(directory.File("Other.dll"));
        }
        var source = new DummyDllSource(directory.Path);
        Assert.Equal("Synthetic", source.FindType("Example.Value, Synthetic")?.Module.Assembly.Name.Name);
        return source;
    }

    [Fact]
    public void DeclaredInstanceMethodsExcludeStaticPrivateInheritedAndConstructors()
    {
        using var directory = new TemporaryDirectory();
        WriteMetadata(directory);
        using var source = new DummyDllSource(directory.Path);

        Assert.True(source.TryGetDeclaredInstanceMethodCount("Example.Owner, Synthetic", out var count));
        Assert.Equal(5, count); // Read x2, Different x2, Only; neither constructor nor base type methods.
        var methods = source.FindDeclaredInstanceMethods("Example.Owner, Synthetic")!;
        Assert.DoesNotContain(methods, method => method.IsConstructor || method.IsStatic || !method.IsPublic);
        Assert.DoesNotContain(methods, method => method.Name == "Inherited");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidAssemblyOrMismatchedIdentityIsReportedWithoutResolvingTypes(bool mismatchedIdentity)
    {
        using var directory = new TemporaryDirectory();
        if (mismatchedIdentity)
        {
            WriteMetadata(directory);
            File.Copy(directory.File("Synthetic.dll"), directory.File("Wrong.dll"));
        }
        else File.WriteAllText(directory.File("Wrong.dll"), "not a PE assembly");
        using var source = new DummyDllSource(directory.Path);

        Assert.Null(source.FindType("Example.Value, Wrong"));
        Assert.NotEmpty(source.Warnings);
    }

    [Fact]
    public void NoMetadataDirectoriesIsAnArgumentError()
        => Assert.Throws<ArgumentException>(() => new DummyDllSource(Array.Empty<string>()));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpcodeLiteralsAreReadFromTheActualMetadataEnum(bool withLiterals)
    {
        using var directory = new TemporaryDirectory();
        using (var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("IFix.Core", new Version(1, 0)), "IFix.Core", ModuleKind.Dll))
        {
            var module = assembly.MainModule;
            var code = new TypeDefinition("IFix.Core", "Code", TypeAttributes.Public | TypeAttributes.Sealed, module.ImportReference(typeof(Enum)));
            code.Fields.Add(new FieldDefinition("value__", FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RTSpecialName, module.TypeSystem.Int32));
            if (withLiterals)
            {
                code.Fields.Add(new FieldDefinition("Nop", FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault, code) { Constant = 47 });
                code.Fields.Add(new FieldDefinition("Ret", FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault, code) { Constant = 9 });
            }
            module.Types.Add(code);
            assembly.Write(directory.File("IFix.Core.dll"));
        }
        using var source = new DummyDllSource(directory.Path);

        var map = source.TryGetOpCodeMap();

        if (withLiterals)
        {
            Assert.NotNull(map);
            Assert.Equal(2, map.Count);
            Assert.Equal("Nop", map.Name(47));
            Assert.Equal("Ret", map.Name(9));
        }
        else Assert.Null(map);
    }

    [Fact]
    public void StandardCilMnemonicsMapAndVmSpecificNamesAreNotReportedAsMissing()
    {
        Assert.True(OpCodeTable.TryGet("Add", out var add));
        Assert.Equal(OpCodes.Add, add);
        Assert.True(OpCodeTable.TryGet("Ldarg", out var ldarg));
        Assert.Equal(OpCodes.Ldarg, ldarg);
        Assert.False(OpCodeTable.TryGet("Typo", out _));
        Assert.Equal(new[] { "Typo" }, OpCodeTable.Unmapped(["Add", "StackSpace", "CallExtern", "Newanon", "Ldtype", "Typo"]));
    }

    private static void WriteMetadata(TemporaryDirectory directory)
    {
        using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Synthetic", new Version(1, 0)), "Synthetic", ModuleKind.Dll);
        var module = assembly.MainModule;
        var value = new TypeDefinition("Example", "Value", TypeAttributes.Public, module.TypeSystem.Object);
        module.Types.Add(value);
        var box = new TypeDefinition("Example", "Box`1", TypeAttributes.Public, module.TypeSystem.Object);
        box.GenericParameters.Add(new GenericParameter("T", box));
        module.Types.Add(box);
        var outer = new TypeDefinition("Example", "Outer", TypeAttributes.Public, module.TypeSystem.Object);
        outer.NestedTypes.Add(new TypeDefinition("", "Inner", TypeAttributes.NestedPublic, module.TypeSystem.Object));
        module.Types.Add(outer);
        var baseType = new TypeDefinition("Example", "Base", TypeAttributes.Public, module.TypeSystem.Object);
        baseType.Methods.Add(Method("Inherited", module.TypeSystem.Void));
        module.Types.Add(baseType);
        var owner = new TypeDefinition("Example", "Owner", TypeAttributes.Public, baseType);
        owner.Fields.Add(new FieldDefinition("Count", FieldAttributes.Public, module.TypeSystem.Int32));
        owner.Methods.Add(Method(".ctor", module.TypeSystem.Void, attributes: MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName));
        owner.Methods.Add(Method("Read", module.TypeSystem.Int32, module.TypeSystem.Int32));
        owner.Methods.Add(Method("Read", module.TypeSystem.Int32, module.TypeSystem.String));
        owner.Methods.Add(Method("Different", module.TypeSystem.Int32, module.TypeSystem.Int32));
        owner.Methods.Add(Method("Different", module.TypeSystem.String, module.TypeSystem.String));
        owner.Methods.Add(Method("Only", module.TypeSystem.Int32));
        owner.Methods.Add(Method("Static", module.TypeSystem.Void, attributes: MethodAttributes.Public | MethodAttributes.Static));
        owner.Methods.Add(Method("Private", module.TypeSystem.Void, attributes: MethodAttributes.Private));
        module.Types.Add(owner);
        assembly.Write(directory.File("Synthetic.dll"));
    }

    private static MethodDefinition Method(string name, TypeReference result, TypeReference? parameter = null, MethodAttributes attributes = MethodAttributes.Public)
    {
        var method = new MethodDefinition(name, attributes, result);
        if (parameter != null) method.Parameters.Add(new ParameterDefinition(parameter));
        if (result.FullName == "System.Int32") method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
        if (result.FullName == "System.String") method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        return method;
    }

    [Fact]
    public void UnqualifiedAqnCanResolveAnAlreadyLoadedMetadataType()
    {
        using var directory = new TemporaryDirectory();
        using (var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Synthetic", new Version(1, 0)), "Synthetic", ModuleKind.Dll))
        {
            assembly.MainModule.Types.Add(new TypeDefinition("Example", "Owner", TypeAttributes.Public, assembly.MainModule.TypeSystem.Object));
            assembly.Write(directory.File("Synthetic.dll"));
        }
        using var source = new DummyDllSource(directory.Path);
        Assert.NotNull(source.FindType("Example.Owner, Synthetic"));
        using var output = ModuleDefinition.CreateModule("Output", ModuleKind.Dll);
        Assert.Equal("Example.Owner", new AqnResolver(source, output).Resolve("Example.Owner")?.FullName);
    }
}
