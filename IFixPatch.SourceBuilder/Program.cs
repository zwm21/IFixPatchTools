using System.Diagnostics;
using System.Text.Json;
using IFixPatch.Core;
using IFixPatch.SourceBuilder.Core;

return await SourceBuilderProgram.RunAsync(args);

internal static class SourceBuilderProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                PrintUsage();
                return 0;
            }

            return args[0] switch
            {
                "prepare" => RunPrepare(args[1..]),
                "build" => RunBuild(args[1..]),
                "adapter" => await RunAdapterAsync(args[1..]),
                "generate" => await RunGenerateAsync(args[1..]),
                "verify" => RunVerify(args[1..]),
                _ => throw new ArgumentException($"Unknown command: {args[0]}")
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            if (Environment.GetEnvironmentVariable("IFIX_SOURCEBUILDER_TRACE") == "1")
                Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static int RunPrepare(string[] args)
    {
        var parsed = ParseInputOutput(args, "prepare PROFILE -o DIRECTORY");
        var prepared = new AssemblyPreparer().Prepare(parsed.Input, parsed.Output);
        Console.WriteLine($"prepared: {Path.Combine(parsed.Output, "prepared-profile.json")}");
        Console.WriteLine($"clean shell: {prepared.CleanAssemblyPath}");
        Console.WriteLine($"compile reference: {prepared.CompileReferenceAssemblyPath}");
        Console.WriteLine($"field catalog: {prepared.FieldCatalogPath}");
        return 0;
    }

    private static int RunBuild(string[] args)
    {
        if (args.Length < 4)
            throw new ArgumentException("Usage: build PREPARED_PROFILE MANIFEST -o DIRECTORY");
        var prepared = args[0];
        var manifest = args[1];
        var output = ReadOption(args[2..], "-o", "--output");
        var report = new BuildPipeline().Build(prepared, manifest, output);
        foreach (var diagnostic in report.Diagnostics)
            Console.Error.WriteLine(
                $"{diagnostic.Severity}: [{diagnostic.Rule}] " +
                (diagnostic.Method is null ? "" : diagnostic.Method + ": ") + diagnostic.Message);

        Console.WriteLine($"implementation: {report.ImplementationAssembly}");
        Console.WriteLine($"grafted assembly: {report.GraftedAssembly}");
        Console.WriteLine($"process config: {report.ProcessConfig}");
        Console.WriteLine(
            $"evaluation stack: {report.Size.DeepestChainSlots}/{report.Size.EvaluationStackLimit} slots " +
            $"({report.Size.DeepestChainPercent}%) on the deepest bounded chain" +
            (report.Size.ChainBounded ? "" : "; some call paths have no static bound"));

        var failed = report.Diagnostics.Count(diagnostic => diagnostic.Severity == "fail");
        if (failed == 0)
            return 0;
        Console.Error.WriteLine(
            $"error: {failed} size check(s) failed; generate will refuse this build report.");
        return 2;
    }

    private static async Task<int> RunGenerateAsync(string[] args)
    {
        if (args.Length < 4)
            throw new ArgumentException("Usage: generate PREPARED_PROFILE BUILD_REPORT [--injectfix IFIX.dll | --injectfix-root DIRECTORY] -o PATCH.bytes");
        var preparedPath = Path.GetFullPath(args[0]);
        var buildReportPath = Path.GetFullPath(args[1]);
        var output = ReadOption(args[2..], "-o", "--output");
        var prepared = SourceBuilderJson.Load<PreparedProfile>(preparedPath);
        var report = SourceBuilderJson.Load<BuildReport>(buildReportPath);
        BuildPipeline.ValidatePrepared(prepared);
        ValidateBuildReport(report, preparedPath);
        var injectFix = TryReadOption(args[2..], "--injectfix");
        var injectFixRoot = TryReadOption(args[2..], "--injectfix-root");
        if (injectFix is not null && injectFixRoot is not null)
            throw new ArgumentException("--injectfix and --injectfix-root are mutually exclusive.");
        if (injectFix is null)
        {
            var adapterDirectory = Path.Combine(Path.GetDirectoryName(output)!, ".injectfix-adapter");
            var adapter = await BuildInjectFixAdapterAsync(prepared, adapterDirectory, null, injectFixRoot);
            injectFix = adapter.Assembly;
            File.WriteAllText(output + ".adapter-build.log", adapter.Log);
        }

        var temporaryOutput = output + ".tmp." + Guid.NewGuid().ToString("N");
        string log;
        try
        {
            log = await RunInjectFixAsync(prepared, report, injectFix, temporaryOutput);
            if (!File.Exists(temporaryOutput))
                throw new InvalidOperationException("InjectFix exited without producing a patch.");
            using var metadata = CreateMetadata(prepared);
            var patch = PatchReader.ReadFile(temporaryOutput, metadata);
            PatchValidation.RequireValid(patch, metadata.TryGetOpCodeMap());
            InjectionLayout.Load(prepared).ValidatePatchIdentity(patch);
            ValidateBindings(patch, report, metadata, prepared.TargetAssemblyIdentity);
            File.Move(temporaryOutput, output, true);
            SourceBuilderJson.Save(output + ".generation.json", new GenerationReport
            {
                PatchSha256 = PathAndHash.Sha256(output), BuildReportSha256 = PathAndHash.Sha256(buildReportPath),
                Methods = patch.Methods.Count, InternalMethods = patch.Methods.Count - patch.FixList.Count,
            });
        }
        finally
        {
            if (File.Exists(temporaryOutput))
                File.Delete(temporaryOutput);
        }
        File.WriteAllText(output + ".injectfix.log", log);
        Console.WriteLine($"patch: {Path.GetFullPath(output)}");
        return 0;
    }

    private static async Task<int> RunAdapterAsync(string[] args)
    {
        if (args.Length < 3)
            throw new ArgumentException("Usage: adapter PREPARED_PROFILE -o DIRECTORY [--project InjectFix.Cli.csproj] [--injectfix-root DIRECTORY]");
        var prepared = SourceBuilderJson.Load<PreparedProfile>(Path.GetFullPath(args[0]));
        BuildPipeline.ValidatePrepared(prepared);
        var output = ReadOption(args[1..], "-o", "--output");
        var project = TryReadOption(args[1..], "--project");
        var injectFixRoot = TryReadOption(args[1..], "--injectfix-root");
        var result = await BuildInjectFixAdapterAsync(prepared, output, project, injectFixRoot);
        File.WriteAllText(Path.Combine(output, "adapter-build.log"), result.Log);
        Console.WriteLine($"adapter: {result.Assembly}");
        return 0;
    }

    private static int RunVerify(string[] args)
    {
        if (args.Length < 2)
            throw new ArgumentException(
                "Usage: verify PREPARED_PROFILE PATCH.bytes [-o REPORT.json] [--build-report BUILD_REPORT.json]");
        var prepared = SourceBuilderJson.Load<PreparedProfile>(Path.GetFullPath(args[0]));
        BuildPipeline.ValidatePrepared(prepared);
        var patchPath = Path.GetFullPath(args[1]);
        var output = TryReadOption(args[2..], "-o", "--output") ?? patchPath + ".verify.json";
        var buildReportPath = TryReadOption(args[2..], "--build-report");
        var buildReport = buildReportPath is null ? null : SourceBuilderJson.Load<BuildReport>(Path.GetFullPath(buildReportPath));
        var expectedInternalMethods = buildReport is null || buildReport.HasTypeGraph ? (int?)null : buildReport.Helpers.Count;
        using var metadata = CreateMetadata(prepared);
        var patch = PatchReader.ReadFile(patchPath, metadata);
        var roundTrip = PatchWriter.WriteToBytes(patch);
        var source = File.ReadAllBytes(patchPath);

        var errors = PatchValidation.Validate(patch, metadata.TryGetOpCodeMap());
        try { InjectionLayout.Load(prepared).ValidatePatchIdentity(patch); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException) { errors.Add(error.Message); }
        if (!patch.FullyParsed) errors.Add("Patch did not fully parse: " + patch.ParseNote);
        if (patch.TrailingBytes != 0) errors.Add($"Patch has {patch.TrailingBytes} trailing bytes.");
        if (!source.AsSpan().SequenceEqual(roundTrip)) errors.Add("Parse/serialize round-trip is not byte-identical.");
        if (prepared.KnownPatchMagic is not null && patch.Magic != prepared.KnownPatchMagic)
            errors.Add($"Patch magic 0x{patch.Magic:X16} != profile magic 0x{prepared.KnownPatchMagic:X16}.");
        if (patch.FixList.Count == 0) errors.Add("Patch fix list is empty.");
        var nonEmptyMethods = patch.Methods.Count(method => !method.IsEmpty);
        if (nonEmptyMethods != patch.Methods.Count)
            errors.Add($"Patch contains {patch.Methods.Count - nonEmptyMethods} empty method bodies.");
        // 内部方法（不被 fix list 引用的方法体）是 IFix 的正常能力：helper 与闭包都走这里。
        // 但它同样是 Dummy getter 被误翻译进 VM 的表现，所以数量必须与 build-report 记录的 helper 数对得上。
        var internalMethods = patch.Methods.Count - patch.FixList.Count;
        if (expectedInternalMethods is { } expected)
        {
            if (internalMethods != expected)
                errors.Add(
                    $"Patch contains {internalMethods} internal methods but the build report grafted {expected} helpers; " +
                    "the difference is most likely a dummy member mistranslated into the VM.");
        }
        else if (buildReport is { HasTypeGraph: true })
        {
            var generationPath = patchPath + ".generation.json";
            if (!File.Exists(generationPath)) errors.Add("Type-graph patches require the generation report produced by generate.");
            else
            {
                var generation = SourceBuilderJson.Load<GenerationReport>(generationPath);
                if (generation.PatchSha256 != PathAndHash.Sha256(patchPath) || generation.BuildReportSha256 != PathAndHash.Sha256(buildReportPath!))
                    errors.Add("Generation report does not match the patch or build report.");
                if (generation.Methods != patch.Methods.Count || generation.InternalMethods != internalMethods)
                    errors.Add("Generated method inventory does not match the patch.");
            }
        }
        else if (internalMethods != 0)
        {
            errors.Add(
                $"Patch contains {internalMethods} internal methods; " +
                "pass --build-report BUILD_REPORT.json so they can be reconciled against the grafted helpers.");
        }

        if (buildReport is not null && buildReport.Bindings.Count != 0)
        {
            try { ValidateBindings(patch, buildReport, metadata, prepared.TargetAssemblyIdentity); }
            catch (InvalidDataException error) { errors.Add(error.Message); }
        }

        var result = new
        {
            patch = patchPath,
            sha256 = PathAndHash.Sha256(patchPath),
            patch.Magic,
            patch.FullyParsed,
            patch.TrailingBytes,
            byteIdenticalRoundTrip = source.AsSpan().SequenceEqual(roundTrip),
            methods = patch.Methods.Count,
            nonEmptyMethods,
            externTypes = patch.ExternTypes.Count,
            externMethods = patch.ExternMethods.Count,
            fixes = patch.FixList.Count,
            internalMethods,
            expectedInternalMethods,
            newClasses = patch.NewClasses.Count,
            errors,
        };
        File.WriteAllText(output, JsonSerializer.Serialize(result, SourceBuilderJson.Options));
        Console.WriteLine(errors.Count == 0
            ? $"verified: {Path.GetFullPath(output)}"
            : $"verification failed: {Path.GetFullPath(output)}");
        return errors.Count == 0 ? 0 : 2;
    }

    private static DummyDllSource CreateMetadata(PreparedProfile prepared) => new(
        prepared.ReferenceDirectories.Prepend(Path.GetDirectoryName(prepared.SourceAssemblyPath)!)
            .Append(Path.GetDirectoryName(prepared.IFixCoreAssemblyPath)!),
        new[] { prepared.SourceAssemblyPath, prepared.IFixCoreAssemblyPath }.Distinct(StringComparer.Ordinal));

    private static void ValidateBindings(PatchFile patch, BuildReport report, DummyDllSource metadata, string targetIdentity)
    {
        var actual = new List<string>();
        foreach (var fix in patch.FixList)
        {
            var declaring = patch.ExternTypes[fix.Method.DeclaringTypeId];
            var type = metadata.FindType(declaring) ?? throw new InvalidDataException("Missing fix declaring type: " + declaring);
            if (type.Module.Assembly.Name.FullName != targetIdentity)
                throw new InvalidDataException("Fix declaring type belongs to another assembly: " + declaring);
            var matches = type.Methods.Where(method => method.Name == fix.Method.Name && method.Parameters.Count == fix.Method.Params.Count &&
                method.Parameters.Zip(fix.Method.Params).All(pair => !pair.Second.IsGeneric &&
                    pair.First.ParameterType.FullName.Replace('/', '+') == DummyDllSource.SplitAqn(patch.ExternTypes[pair.Second.TypeId]).TypeName.Replace('/', '+'))).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("Unknown or ambiguous generated fix binding: " + fix.Method.Name);
            actual.Add(matches[0].FullName);
        }
        if (!actual.Order(StringComparer.Ordinal).SequenceEqual(report.Bindings.Select(binding => binding.Target).Order(StringComparer.Ordinal)))
            throw new InvalidDataException("Generated fix list differs from the build bindings.");
        if (report.HasTypeGraph && !patch.NewClasses.Select(name => name.Split(',')[0]).Order(StringComparer.Ordinal).SequenceEqual(report.NewTypes.Order(StringComparer.Ordinal)))
            throw new InvalidDataException("Generated class inventory differs from the build type graph.");
    }

    private static async Task<string> RunInjectFixAsync(
        PreparedProfile prepared,
        BuildReport report,
        string injectFixPath,
        string outputPath)
    {
        injectFixPath = Path.GetFullPath(injectFixPath);
        outputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        if (!File.Exists(injectFixPath))
            throw new FileNotFoundException("InjectFix CLI does not exist.", injectFixPath);

        var start = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(injectFixPath);
        start.ArgumentList.Add("-patch");
        start.ArgumentList.Add(prepared.IFixCoreAssemblyPath);
        start.ArgumentList.Add(report.GraftedAssembly);
        start.ArgumentList.Add("null");
        start.ArgumentList.Add(report.ProcessConfig);
        start.ArgumentList.Add(outputPath);
        foreach (var directory in prepared.ReferenceDirectories)
            start.ArgumentList.Add(directory);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start InjectFix CLI.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var log = stdout + (stderr.Length == 0 ? "" : Environment.NewLine + stderr);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"InjectFix failed with exit code {process.ExitCode}.\n{log}");
        if (stdout.Contains("Unhandled Exception", StringComparison.Ordinal) ||
            stdout.Contains("Error:", StringComparison.Ordinal))
            throw new InvalidOperationException("InjectFix reported an error.\n" + log);
        return log;
    }

    private static void ValidateBuildReport(BuildReport report, string preparedProfilePath)
    {
        preparedProfilePath = Path.GetFullPath(preparedProfilePath);
        if (Path.GetFullPath(report.PreparedProfile) != preparedProfilePath)
            throw new InvalidDataException("Build report belongs to a different prepared profile.");
        var inputs = new[]
        {
            (report.PreparedProfile, report.PreparedProfileSha256, "Prepared profile"),
            (report.Manifest, report.ManifestSha256, "Patch manifest"),
            (report.ImplementationAssembly, report.ImplementationAssemblySha256, "Implementation assembly"),
            (report.GraftedAssembly, report.GraftedAssemblySha256, "Grafted assembly"),
            (report.ProcessConfig, report.ProcessConfigSha256, "Process config"),
        };
        foreach (var (path, expectedHash, label) in inputs)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException(label + " no longer exists.", path);
            if (PathAndHash.Sha256(path) != expectedHash)
                throw new InvalidDataException(label + " hash drifted after build.");
        }

        // 规模检查失败的 build 不该继续走下去：越限只会在真机上表现为静默串位或
        // StackOverflowException，离线 verify 看不出来。
        var failed = report.Diagnostics.Where(diagnostic => diagnostic.Severity == "fail").ToList();
        if (failed.Count != 0)
            throw new InvalidDataException(
                $"Build report has {failed.Count} failed size check(s); re-run build and fix them first. " +
                "First: " + failed[0].Message);

        // 源文件不参与上面的产物链校验：改了源码但忘记重跑 build 时，manifest 与全部产物的哈希
        // 都仍然自洽，只有这里能发现 patch 是从过期实现生成的。
        if (report.Sources.Count == 0)
            throw new InvalidDataException(
                "Build report records no patch sources; regenerate it with a current SourceBuilder build.");
        foreach (var source in report.Sources)
        {
            if (!File.Exists(source.Path))
                throw new FileNotFoundException("Patch source no longer exists.", source.Path);
            if (PathAndHash.Sha256(source.Path) != source.Sha256)
                throw new InvalidDataException(
                    $"Patch source changed after build: {source.Path}. Re-run build before generate.");
        }
    }

    private static async Task<(string Assembly, string Log)> BuildInjectFixAdapterAsync(
        PreparedProfile prepared,
        string outputDirectory,
        string? projectPath,
        string? injectFixRoot)
    {
        outputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        projectPath ??= Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "tools", "InjectFix.Cli", "InjectFix.Cli.csproj"));
        if (!File.Exists(projectPath))
            throw new FileNotFoundException(
                "InjectFix adapter project was not found. Pass --project when running a relocated/published SourceBuilder.",
                projectPath);

        var start = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("build");
        start.ArgumentList.Add(projectPath);
        start.ArgumentList.Add("--configuration");
        start.ArgumentList.Add("Release");
        start.ArgumentList.Add("--output");
        start.ArgumentList.Add(outputDirectory);
        start.ArgumentList.Add($"-p:GameIFixCoreSource={prepared.AdapterCoreSourcePath}");
        if (injectFixRoot is not null)
            start.ArgumentList.Add($"-p:InjectFixRoot={injectFixRoot}");

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start adapter build.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var log = stdout + (stderr.Length == 0 ? "" : Environment.NewLine + stderr);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"InjectFix adapter build failed with exit code {process.ExitCode}.\n{log}");

        var assembly = Path.Combine(outputDirectory, "IFix.dll");
        if (!File.Exists(assembly))
            throw new InvalidOperationException("Adapter build succeeded without producing IFix.dll.\n" + log);
        return (assembly, log);
    }

    private static (string Input, string Output) ParseInputOutput(string[] args, string usage)
    {
        if (args.Length < 3)
            throw new ArgumentException("Usage: " + usage);
        return (Path.GetFullPath(args[0]), Path.GetFullPath(ReadOption(args[1..], "-o", "--output")));
    }

    private static string ReadOption(string[] args, params string[] names) =>
        TryReadOption(args, names) ?? throw new ArgumentException($"Missing option {string.Join("/", names)}.");

    private static string? TryReadOption(string[] args, params string[] names)
    {
        for (var i = 0; i < args.Length; i++)
            if (names.Contains(args[i], StringComparer.Ordinal) && i + 1 < args.Length)
                return Path.GetFullPath(args[i + 1]);
        return null;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("IFixPatch.SourceBuilder");
        Console.WriteLine("  prepare PROFILE -o DIRECTORY");
        Console.WriteLine("  build PREPARED_PROFILE MANIFEST -o DIRECTORY");
        Console.WriteLine("  adapter PREPARED_PROFILE -o DIRECTORY [--project InjectFix.Cli.csproj] [--injectfix-root DIRECTORY]");
        Console.WriteLine("  generate PREPARED_PROFILE BUILD_REPORT [--injectfix IFIX.dll | --injectfix-root DIRECTORY] -o PATCH.bytes");
        Console.WriteLine("  verify PREPARED_PROFILE PATCH.bytes [--build-report BUILD_REPORT.json] [-o REPORT.json]");
    }
}
