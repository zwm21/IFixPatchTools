using BlobBuilder = System.Reflection.Metadata.BlobBuilder;
using MetadataRootBuilder = System.Reflection.Metadata.Ecma335.MetadataRootBuilder;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IFixPatch.SourceBuilder.Core.Tests;

public sealed class AssemblyBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparationRejectsAdditionalModulesAndModuleScopedTypeReferences(bool typeReferenceOnly)
    {
        using var fixture = new SyntheticTargetFixture();
        AddModule(fixture.TargetPath, typeReferenceOnly);
        var error = Assert.Throws<NotSupportedException>(() => fixture.Prepare());
        Assert.Contains("Multi-module", error.Message);
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, "prepared", "clean-shell", "Example.Game.dll")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GraftingRejectsAdditionalModulesAndModuleScopedTypeReferences(bool typeReferenceOnly)
    {
        using var fixture = new SyntheticTargetFixture();
        var prepared = fixture.Prepare();
        var manifest = CompilerPipelineTests.WriteSource(fixture, "namespace Patch {public static class Replacement {public static int Compute()=>42;}}");
        var implementation = Path.Combine(fixture.DirectoryPath, "implementation.dll");
        new SourceCompiler().Compile(prepared, manifest, implementation);
        AddModule(implementation, typeReferenceOnly);
        var output = Path.Combine(fixture.DirectoryPath, "grafted.dll");
        var config = Path.Combine(fixture.DirectoryPath, "process.cfg");
        Assert.Contains("Multi-module", Assert.Throws<NotSupportedException>(() => new BodyGrafter().Graft(prepared, manifest, implementation, output, config)).Message);
        Assert.False(File.Exists(output));
        Assert.False(File.Exists(config));
    }

    [Fact]
    public void GraftingDiagnosesAnUnresolvedConstructedTypeBeforeWritingAnArtifact()
    {
        using var fixture = new SyntheticTargetFixture();
        var prepared = fixture.Prepare();
        var dependencyPath = Path.Combine(fixture.DirectoryPath, "ConstructorDependency.dll");
        using (var dependency = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("ConstructorDependency", new Version(1, 0)), "ConstructorDependency", ModuleKind.Dll))
            dependency.Write(dependencyPath);
        var manifest = CompilerPipelineTests.WriteSource(fixture, "namespace Patch {class State {} public static class Replacement {public static int Compute(){new State();return 42;}}}");
        var implementation = Path.Combine(fixture.DirectoryPath, "implementation.dll");
        new SourceCompiler().Compile(prepared, manifest, implementation);
        using (var assembly = AssemblyDefinition.ReadAssembly(implementation, new ReaderParameters { InMemory = true }))
        {
            var module = assembly.MainModule;
            var scope = new AssemblyNameReference("ConstructorDependency", new Version(1, 0));
            module.AssemblyReferences.Add(scope);
            var absent = new TypeReference("External", "Missing", module, scope);
            var constructor = new MethodReference(".ctor", module.TypeSystem.Void, absent) { HasThis = true };
            var root = module.GetType("Patch.Replacement").Methods.Single(method => method.Name == "Compute");
            var il = root.Body.GetILProcessor();
            il.InsertBefore(root.Body.Instructions[0], Instruction.Create(OpCodes.Newobj, constructor));
            il.InsertBefore(root.Body.Instructions[1], Instruction.Create(OpCodes.Pop));
            assembly.Write(implementation);
        }
        var output = Path.Combine(fixture.DirectoryPath, "grafted.dll");
        var config = Path.Combine(fixture.DirectoryPath, "process.cfg");
        var error = Assert.Throws<InvalidDataException>(() => new BodyGrafter().Graft(prepared, manifest, implementation, output, config));
        Assert.Contains("Unable to resolve constructed type: External.Missing", error.Message);
        Assert.False(File.Exists(output));
        Assert.False(File.Exists(config));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GraftingRejectsAmbiguousMemberMetadataInsteadOfChoosingAnArbitraryDefinition(bool field)
    {
        using var fixture = new SyntheticTargetFixture();
        fixture.Change(module =>
        {
            var native = SyntheticTargetFixture.Class(module, "Example", "Native");
            if (field) native.Fields.Add(new FieldDefinition("Value", FieldAttributes.Public | FieldAttributes.Static, module.TypeSystem.Int32));
            else SyntheticTargetFixture.Method(native, "Read", true, module.TypeSystem.Int32);
        });
        var expression = field ? "Example.Native.Value" : "Example.Native.Read()";
        var manifest = CompilerPipelineTests.WriteSource(fixture,
            "namespace Patch {public static class Replacement {public static int Compute()=>" + expression + ";}}");
        var prepared = fixture.Prepare();
        var implementation = Path.Combine(fixture.DirectoryPath, "implementation.dll");
        new SourceCompiler().Compile(prepared, manifest, implementation);
        using (var clean = AssemblyDefinition.ReadAssembly(prepared.CleanAssemblyPath, new ReaderParameters { InMemory = true }))
        {
            var native = clean.MainModule.GetType("Example.Native");
            if (field) native.Fields.Add(new FieldDefinition("Value", FieldAttributes.Public | FieldAttributes.Static, clean.MainModule.TypeSystem.Int32));
            else SyntheticTargetFixture.Method(native, "Read", true, clean.MainModule.TypeSystem.Int32);
            clean.Write(prepared.CleanAssemblyPath);
        }
        var output = Path.Combine(fixture.DirectoryPath, "grafted.dll");
        var config = Path.Combine(fixture.DirectoryPath, "process.cfg");
        var error = Assert.Throws<InvalidDataException>(() => new BodyGrafter().Graft(prepared, manifest, implementation, output, config));
        Assert.Contains(field ? "Ambiguous target field rebind" : "Ambiguous target method rebind", error.Message);
        Assert.Contains("Example.Native", error.Message);
        Assert.False(File.Exists(output));
        Assert.False(File.Exists(config));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../escaped")]
    [InlineData("..\\escaped")]
    [InlineData("/absolute")]
    [InlineData("C:\\absolute")]
    [InlineData("bad\0name")]
    [InlineData("Bad<Name")]
    [InlineData("Bad|Name")]
    [InlineData("Bad?Name")]
    [InlineData("Bad:Name")]
    public void BuildRejectsAnImplementationNameThatIsNotAPortableFileNameBeforeCreatingOutput(string? name)
    {
        using var fixture = new SyntheticTargetFixture();
        var manifest = CompilerPipelineTests.WriteSource(fixture, "namespace Patch {public static class Replacement {public static int Compute()=>42;}}");
        manifest.AssemblyName = name!;
        var manifestPath = Path.Combine(fixture.DirectoryPath, "manifest.json");
        var preparedPath = Path.Combine(fixture.DirectoryPath, "prepared.json");
        // Keep an explicit JSON null: the normal writer omits it and selects the model default.
        File.WriteAllText(manifestPath, System.Text.Json.JsonSerializer.Serialize(manifest, new System.Text.Json.JsonSerializerOptions(SourceBuilderJson.Options)
        { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never }));
        SourceBuilderJson.Save(preparedPath, fixture.Prepare());
        var output = Path.Combine(fixture.DirectoryPath, "build");
        var sentinel = Path.Combine(fixture.DirectoryPath, "escaped.dll");
        File.WriteAllText(sentinel, "existing artifact");
        Assert.Contains("Implementation assembly name cannot be used as a file name", Assert.Throws<InvalidDataException>(() => new BuildPipeline().Build(preparedPath, manifestPath, output)).Message);
        Assert.False(Directory.Exists(output));
        Assert.Equal("existing artifact", File.ReadAllText(sentinel));
    }

    [Theory]
    [InlineData("Patch.Implementation-v2")]
    [InlineData("Patch_Implementation")]
    public void BuildAcceptsPortableImplementationNamesAndPreservesTheirAssemblyIdentity(string name)
    {
        using var fixture = new SyntheticTargetFixture();
        var manifest = CompilerPipelineTests.WriteSource(fixture, "namespace Patch {public static class Replacement {public static int Compute()=>42;}}");
        manifest.AssemblyName = name;
        var manifestPath = Path.Combine(fixture.DirectoryPath, "manifest.json");
        var preparedPath = Path.Combine(fixture.DirectoryPath, "prepared.json");
        SourceBuilderJson.Save(manifestPath, manifest);
        SourceBuilderJson.Save(preparedPath, fixture.Prepare());
        var output = Path.Combine(fixture.DirectoryPath, "build");
        var report = new BuildPipeline().Build(preparedPath, manifestPath, output);
        Assert.Equal(Path.Combine(output, name + ".dll"), report.ImplementationAssembly);
        Assert.Equal(name, System.Reflection.AssemblyName.GetAssemblyName(report.ImplementationAssembly).Name);
        var assembly = System.Reflection.Assembly.Load(File.ReadAllBytes(report.GraftedAssembly));
        Assert.Equal(42, assembly.GetType("Example.Calculator")!.GetMethod("Compute")!.Invoke(null, null));
    }

    [Fact]
    public void ReusedGrafterReportsOnlyTheCurrentGraftTypeAndMemberAdditions()
    {
        using var fixture = new SyntheticTargetFixture();
        var prepared = fixture.Prepare();
        var graph = CompilerPipelineTests.WriteSource(fixture, """
            namespace Patch {
                public class Extension {public int Extra; public int Value {get;set;} }
                public static class Replacement {public static int Compute()=>new Extension().Extra;}
            }
            """);
        graph.TypeBindings.Add("Patch.Extension", "Example.Calculator");
        var implementation = Path.Combine(fixture.DirectoryPath, "implementation.dll");
        var output = Path.Combine(fixture.DirectoryPath, "grafted.dll");
        var config = Path.Combine(fixture.DirectoryPath, "process.cfg");
        var grafter = new BodyGrafter();
        new SourceCompiler().Compile(prepared, graph, implementation);
        grafter.Graft(prepared, graph, implementation, output, config);
        Assert.True(grafter.HasTypeGraph);
        Assert.NotEmpty(grafter.NewTypes);
        Assert.NotEmpty(grafter.AddedFields);
        Assert.NotEmpty(grafter.AddedProperties);

        var flat = CompilerPipelineTests.WriteSource(fixture, "namespace Patch {public static class Replacement {public static int Compute()=>42;}}");
        new SourceCompiler().Compile(prepared, flat, implementation);
        grafter.Graft(prepared, flat, implementation, output, config);
        Assert.False(grafter.HasTypeGraph);
        Assert.Empty(grafter.NewTypes);
        Assert.Empty(grafter.AddedFields);
        Assert.Empty(grafter.AddedProperties);
        var assembly = System.Reflection.Assembly.Load(File.ReadAllBytes(output));
        Assert.Equal(42, assembly.GetType("Example.Calculator")!.GetMethod("Compute")!.Invoke(null, null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GraftingRejectsAbiTypesThatShareAFullNameButBelongToDifferentAssemblies(bool returnType)
    {
        using var fixture = new SyntheticTargetFixture();
        foreach (var name in new[] { "AbiDependencyA", "AbiDependencyB" })
        {
            using var dependency = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name, new Version(1, 0)), name, ModuleKind.Dll);
            SyntheticTargetFixture.Class(dependency.MainModule, "Shared", "Payload");
            dependency.Write(Path.Combine(fixture.DirectoryPath, name + ".dll"));
        }
        fixture.Change(module =>
        {
            var scope = new AssemblyNameReference("AbiDependencyA", new Version(1, 0));
            module.AssemblyReferences.Add(scope);
            var payload = new TypeReference("Shared", "Payload", module, scope);
            var compute = module.GetType("Example.Calculator").Methods.Single();
            if (returnType) compute.ReturnType = payload;
            else compute.Parameters.Add(new ParameterDefinition(payload));
        });
        var prepared = fixture.Prepare();
        var implementation = Path.Combine(fixture.DirectoryPath, "implementation.dll");
        using (var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("SyntheticImplementation", new Version(1, 0)), "SyntheticImplementation", ModuleKind.Dll))
        {
            var module = assembly.MainModule;
            var scope = new AssemblyNameReference("AbiDependencyB", new Version(1, 0));
            module.AssemblyReferences.Add(scope);
            var payload = new TypeReference("Shared", "Payload", module, scope);
            var compute = SyntheticTargetFixture.Method(SyntheticTargetFixture.Class(module, "Patch", "Replacement"), "Compute", true,
                returnType ? payload : module.TypeSystem.Int32);
            if (!returnType) compute.Parameters.Add(new ParameterDefinition(payload));
            compute.Body.Instructions.Clear();
            compute.Body.Instructions.Add(returnType ? Instruction.Create(OpCodes.Ldnull) : Instruction.Create(OpCodes.Ldc_I4, 42));
            compute.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            assembly.Write(implementation);
        }
        var signature = returnType ? "Shared.Payload {0}::Compute()" : "System.Int32 {0}::Compute(Shared.Payload)";
        var manifest = new PatchManifest
        {
            Patches = [new PatchBinding
        {
            Target = string.Format(signature, "Example.Calculator"), Implementation = string.Format(signature, "Patch.Replacement")
        }]
        };
        var output = Path.Combine(fixture.DirectoryPath, "grafted.dll");
        var config = Path.Combine(fixture.DirectoryPath, "process.cfg");
        var error = Assert.Throws<InvalidDataException>(() => new BodyGrafter().Graft(prepared, manifest, implementation, output, config));
        Assert.Contains(returnType ? "Return type mismatch" : "Parameter 0 mismatch", error.Message);
        Assert.False(File.Exists(output));
        Assert.False(File.Exists(config));
    }

    private static void AddModule(string path, bool typeReferenceOnly)
    {
        const string moduleName = "Extra.netmodule";
        if (typeReferenceOnly)
        {
            using var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { InMemory = true });
            var main = assembly.MainModule;
            var scope = new ModuleReference(moduleName);
            main.ModuleReferences.Add(scope);
            var owner = SyntheticTargetFixture.Class(main, "Example", "ModuleUser");
            owner.Fields.Add(new FieldDefinition("Value", FieldAttributes.Public, new TypeReference("Extra", "Payload", main, scope)));
            assembly.Write(path);
            return;
        }

        var modulePath = Path.Combine(Path.GetDirectoryName(path)!, moduleName);
        using (var extra = ModuleDefinition.CreateModule(moduleName, ModuleKind.NetModule))
        {
            SyntheticTargetFixture.Class(extra, "Extra", "Payload");
            extra.Write(modulePath);
        }
        // A real manifest File row is required: Cecil reads multi-module assemblies but
        // cannot write them. Use the CLI metadata writer rather than fabricating private state.
        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString(Path.GetFileName(path)), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString(Path.GetFileNameWithoutExtension(path)), new Version(1, 0), default, default,
            0, System.Reflection.AssemblyHashAlgorithm.Sha1);
        metadata.AddAssemblyFile(metadata.GetOrAddString(moduleName), metadata.GetOrAddBlob(SHA1.HashData(File.ReadAllBytes(modulePath))), containsMetadata: true);
        metadata.AddTypeDefinition(0, default, metadata.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var pe = new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata), new BlobBuilder());
        var image = new BlobBuilder();
        pe.Serialize(image);
        File.WriteAllBytes(path, image.ToArray());
    }
}
