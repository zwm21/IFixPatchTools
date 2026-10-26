namespace IFixPatch.SourceBuilder.Core;

public sealed class BuildPipeline
{
    public BuildReport Build(string preparedProfilePath, string manifestPath, string outputDirectory)
    {
        preparedProfilePath = Path.GetFullPath(preparedProfilePath);
        manifestPath = Path.GetFullPath(manifestPath);
        outputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputDirectory);

        var prepared = SourceBuilderJson.Load<PreparedProfile>(preparedProfilePath);
        var manifest = PathAndHash.LoadResolvedManifest(manifestPath);
        ValidatePrepared(prepared);

        var implementationPath = Path.Combine(outputDirectory, manifest.AssemblyName + ".dll");
        var graftedPath = Path.Combine(outputDirectory, "Assembly-CSharp.grafted.dll");
        var processConfigPath = Path.Combine(outputDirectory, "process_cfg");

        new SourceCompiler().Compile(prepared, manifest, implementationPath);
        var grafter = new BodyGrafter();
        var bindings = grafter.Graft(
            prepared, manifest, implementationPath, graftedPath, processConfigPath);

        var report = new BuildReport
        {
            PreparedProfile = preparedProfilePath,
            PreparedProfileSha256 = PathAndHash.Sha256(preparedProfilePath),
            Manifest = manifestPath,
            ManifestSha256 = PathAndHash.Sha256(manifestPath),
            Sources = [.. manifest.Sources.Select(source => new SourceFileRecord
            {
                Path = source,
                Sha256 = PathAndHash.Sha256(source),
            })],
            ImplementationAssembly = implementationPath,
            ImplementationAssemblySha256 = PathAndHash.Sha256(implementationPath),
            GraftedAssembly = graftedPath,
            GraftedAssemblySha256 = PathAndHash.Sha256(graftedPath),
            ProcessConfig = processConfigPath,
            ProcessConfigSha256 = PathAndHash.Sha256(processConfigPath),
            Bindings = bindings,
            Helpers = [.. grafter.Helpers],
            GenericInstances = [.. grafter.GenericInstances],
            Size = grafter.Size,
            Diagnostics = [.. grafter.Diagnostics],
        };
        SourceBuilderJson.Save(Path.Combine(outputDirectory, "build-report.json"), report);
        return report;
    }

    public static void ValidatePrepared(PreparedProfile prepared)
    {
        var inputs = new[]
        {
            prepared.SourceAssemblyPath,
            prepared.IFixCoreAssemblyPath,
            prepared.AdapterCoreSourcePath,
            prepared.CatalogPath,
            prepared.FieldCatalogPath,
            prepared.CleanAssemblyPath,
            prepared.CompileReferenceAssemblyPath,
        };
        foreach (var input in inputs)
            if (!File.Exists(input))
                throw new FileNotFoundException("Prepared profile input no longer exists.", input);

        if (PathAndHash.Sha256(prepared.SourceAssemblyPath) != prepared.SourceAssemblySha256)
            throw new InvalidDataException("Source Assembly-CSharp hash drifted after prepare.");
        if (PathAndHash.Sha256(prepared.IFixCoreAssemblyPath) != prepared.IFixCoreAssemblySha256)
            throw new InvalidDataException("IFix.Core hash drifted after prepare.");
        if (PathAndHash.Sha256(prepared.AdapterCoreSourcePath) != prepared.AdapterCoreSourceSha256)
            throw new InvalidDataException("Adapter IFix.Core source drifted after prepare.");
        if (PathAndHash.Sha256(prepared.CatalogPath) != prepared.CatalogSha256)
            throw new InvalidDataException("Assembly catalog hash drifted after prepare.");
        if (PathAndHash.Sha256(prepared.FieldCatalogPath) != prepared.FieldCatalogSha256)
            throw new InvalidDataException("Field catalog hash drifted after prepare.");
        if (PathAndHash.Sha256(prepared.CleanAssemblyPath) != prepared.CleanAssemblySha256)
            throw new InvalidDataException("Clean shell hash drifted after prepare.");
        if (PathAndHash.Sha256(prepared.CompileReferenceAssemblyPath) != prepared.CompileReferenceAssemblySha256)
            throw new InvalidDataException("Compile reference hash drifted after prepare.");
        if (prepared.KnownPatchPath is not null &&
            PathAndHash.Sha256(prepared.KnownPatchPath) != prepared.KnownPatchSha256)
            throw new InvalidDataException("Known patch hash drifted after prepare.");
    }
}
