using IFixPatch.Core;
using IFixPatch.SourceBuilder.Core;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IFixPatch.SourceBuilder.Tests;

/// <summary>Metadata sufficient to exercise verify; it does not provide an executable IFix VM.</summary>
internal sealed class PreparedProfileFixture : IDisposable
{
    public string DirectoryPath { get; } = Directory.CreateTempSubdirectory("ifix-sourcebuilder-tests-").FullName;
    public PreparedProfile Prepared { get; }
    public string ProfilePath => Path.Combine(DirectoryPath, "prepared-profile.json");
    public string PatchPath => Path.Combine(DirectoryPath, "patch.bytes");
    public string ReportPath => Path.Combine(DirectoryPath, "build-report.json");
    public string SourcePath => Path.Combine(DirectoryPath, "Patch.cs");
    public string ArtifactPath => Path.Combine(DirectoryPath, "artifact.txt");
    public PatchFile Patch { get; }
    public BuildReport Report { get; }

    public PreparedProfileFixture()
    {
        var metadata = Directory.CreateDirectory(Path.Combine(DirectoryPath, "metadata")).FullName;
        var runtime = Directory.CreateDirectory(Path.Combine(DirectoryPath, "runtime")).FullName;
        var empty = Directory.CreateDirectory(Path.Combine(DirectoryPath, "empty")).FullName;
        var targetPath = Path.Combine(metadata, "renamed-target.dll");
        var runtimePath = Path.Combine(runtime, "renamed-runtime.dll");
        using var target = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Synthetic.Target", new Version(1, 0)), "Synthetic.Target", ModuleKind.Dll);
        var module = target.MainModule;
        var counter = new TypeDefinition("Example", "Counter", TypeAttributes.Public, module.TypeSystem.Object);
        module.Types.Add(counter);
        var value = new MethodDefinition("Value", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32);
        value.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, 42));
        value.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        counter.Methods.Add(value);
        foreach (var name in new[] { "ILFixDynamicMethodWrapper", "ILFixInterfaceBridge", "WrappersManagerImpl" })
            module.Types.Add(new TypeDefinition("IFix", name, TypeAttributes.Public, module.TypeSystem.Object));
        target.Write(targetPath);

        using var core = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("IFix.Core", new Version(1, 0)), "IFix.Core", ModuleKind.Dll);
        var code = new TypeDefinition("IFix.Core", "Code", TypeAttributes.Public | TypeAttributes.Sealed,
            new TypeReference("System", "Enum", core.MainModule, core.MainModule.TypeSystem.CoreLibrary));
        core.MainModule.Types.Add(code);
        code.Fields.Add(new FieldDefinition("value__", FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RTSpecialName, core.MainModule.TypeSystem.Int32));
        foreach (var (name, number) in new[] { ("StackSpace", 10), ("Ldc_I4", 20), ("Ret", 30) })
            code.Fields.Add(new FieldDefinition(name, FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault, code) { Constant = number });
        core.Write(runtimePath);

        File.WriteAllText(SourcePath, "// Synthetic source input for provenance validation.\n");
        File.WriteAllText(ArtifactPath, "synthetic artifact");
        var layoutPath = Path.Combine(DirectoryPath, "injection-layout.json");
        var layout = new InjectionLayout
        {
            TargetAssemblyIdentity = target.Name.FullName,
            RuntimeAssemblyIdentity = core.Name.FullName,
            DynamicWrapperType = "IFix.ILFixDynamicMethodWrapper",
            InterfaceBridgeType = "IFix.ILFixInterfaceBridge",
            WrapperManagerType = "IFix.WrappersManagerImpl",
        };
        SourceBuilderJson.Save(layoutPath, layout);
        Prepared = new PreparedProfile
        {
            Name = "synthetic",
            TargetAssemblyName = target.Name.Name,
            TargetAssemblyIdentity = target.Name.FullName,
            SourceAssemblyPath = targetPath,
            SourceAssemblySha256 = PathAndHash.Sha256(targetPath),
            CleanAssemblyPath = targetPath,
            CleanAssemblySha256 = PathAndHash.Sha256(targetPath),
            CompileReferenceAssemblyPath = targetPath,
            CompileReferenceAssemblySha256 = PathAndHash.Sha256(targetPath),
            IFixCoreAssemblyPath = runtimePath,
            IFixCoreAssemblySha256 = PathAndHash.Sha256(runtimePath),
            InjectionLayoutPath = layoutPath,
            InjectionLayoutSha256 = PathAndHash.Sha256(layoutPath),
            AdapterCoreSourcePath = ArtifactPath,
            AdapterCoreSourceSha256 = PathAndHash.Sha256(ArtifactPath),
            CatalogPath = ArtifactPath,
            CatalogSha256 = PathAndHash.Sha256(ArtifactPath),
            FieldCatalogPath = ArtifactPath,
            FieldCatalogSha256 = PathAndHash.Sha256(ArtifactPath),
            ReferenceDirectories = [empty, metadata, runtime],
            KnownPatchMagic = 42,
        };
        SourceBuilderJson.Save(ProfilePath, Prepared);
        Patch = new PatchFile
        {
            FullyParsed = true,
            Magic = 42,
            InterfaceBridgeType = layout.InterfaceBridgeType + ", " + target.Name.FullName,
            WrapperManagerType = layout.WrapperManagerType + ", " + target.Name.FullName,
            IdMapSuffix = ", " + target.Name.FullName,
            ExternTypes = ["Example.Counter, " + target.Name.FullName],
            Methods = [new() { Id = 0, Code = [new() { Code = 10, Operand = 1 }, new() { Code = 20, Operand = 42 }, new() { Code = 30, Operand = 1 }] }],
            FixList = [new() { MethodId = 0, Method = new() { DeclaringTypeId = 0, Name = "Value" } }],
        };
        Report = new BuildReport
        {
            PreparedProfile = ProfilePath,
            PreparedProfileSha256 = PathAndHash.Sha256(ProfilePath),
            Manifest = ArtifactPath,
            ManifestSha256 = PathAndHash.Sha256(ArtifactPath),
            ImplementationAssembly = targetPath,
            ImplementationAssemblySha256 = PathAndHash.Sha256(targetPath),
            GraftedAssembly = targetPath,
            GraftedAssemblySha256 = PathAndHash.Sha256(targetPath),
            ProcessConfig = ArtifactPath,
            ProcessConfigSha256 = PathAndHash.Sha256(ArtifactPath),
            Sources = [new() { Path = SourcePath, Sha256 = PathAndHash.Sha256(SourcePath) }],
            Bindings = [new() { Target = value.FullName }],
        };
        Save();
    }

    public void AddParameterBinding()
    {
        using var target = AssemblyDefinition.ReadAssembly(Prepared.SourceAssemblyPath, new ReaderParameters { InMemory = true });
        var method = new MethodDefinition("Echo", MethodAttributes.Public | MethodAttributes.Static, target.MainModule.TypeSystem.Int32);
        method.Parameters.Add(new ParameterDefinition(target.MainModule.TypeSystem.Int32));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        target.MainModule.GetType("Example.Counter").Methods.Add(method);
        target.Write(Prepared.SourceAssemblyPath);
        var hash = PathAndHash.Sha256(Prepared.SourceAssemblyPath);
        Prepared.SourceAssemblySha256 = hash; Prepared.CleanAssemblySha256 = hash; Prepared.CompileReferenceAssemblySha256 = hash;
        SourceBuilderJson.Save(ProfilePath, Prepared);
        Report.PreparedProfileSha256 = PathAndHash.Sha256(ProfilePath);
        Report.ImplementationAssemblySha256 = hash; Report.GraftedAssemblySha256 = hash;
        Report.Bindings[0].Target = "System.Int32 Example.Counter::Echo(System.Int32)";
        Patch.ExternTypes.Add("System.Int32, mscorlib");
        Patch.FixList[0].Method.Name = "Echo";
        Patch.FixList[0].Method.Params.Add(new() { TypeId = 1 });
        Save();
    }

    public void AddInternalMethod() => Patch.Methods.Add(new IFixMethod { Id = 1, Code = [new() { Code = 10 }, new() { Code = 30 }] });

    public void Save()
    {
        PatchWriter.WriteFile(PatchPath, Patch);
        SourceBuilderJson.Save(ReportPath, Report);
    }

    public void SaveGenerationReport() => SourceBuilderJson.Save(PatchPath + ".generation.json", new GenerationReport
    {
        PatchSha256 = PathAndHash.Sha256(PatchPath),
        BuildReportSha256 = PathAndHash.Sha256(ReportPath),
        Methods = Patch.Methods.Count,
        InternalMethods = Patch.Methods.Count - Patch.FixList.Count,
    });

    public void Dispose() => Directory.Delete(DirectoryPath, true);
}
