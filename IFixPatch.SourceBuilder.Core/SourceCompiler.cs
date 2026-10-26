using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Reflection;

namespace IFixPatch.SourceBuilder.Core;

public sealed class SourceCompiler
{
    public string Compile(PreparedProfile prepared, PatchManifest manifest, string outputPath)
    {
        if (manifest.Sources.Count == 0)
            throw new InvalidDataException("Patch manifest has no sources.");
        foreach (var source in manifest.Sources)
            if (!File.Exists(source))
                throw new FileNotFoundException("Patch source does not exist.", source);

        outputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var compiler = FindCompiler();
        var references = CollectReferences(prepared);
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(compiler);
        start.ArgumentList.Add("-nologo");
        start.ArgumentList.Add("-target:library");
        start.ArgumentList.Add("-nostdlib+");
        start.ArgumentList.Add("-optimize+");
        start.ArgumentList.Add("-deterministic+");
        // portable pdb 让 BodyGrafter 能把 graft 失败定位到 PatchImpl.cs 的具体行。
        // 它不参与 -optimize+ 的取舍，也不进 patch，只是构建期诊断信息。
        start.ArgumentList.Add("-debug:portable");
        start.ArgumentList.Add("-langversion:latest");
        start.ArgumentList.Add("-nullable:disable");
        start.ArgumentList.Add($"-out:{outputPath}");
        foreach (var reference in references)
            start.ArgumentList.Add($"-reference:{reference}");
        foreach (var source in manifest.Sources)
            start.ArgumentList.Add(source);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start Roslyn compiler.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(stdoutTask, stderrTask);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"C# compilation failed with exit code {process.ExitCode}.\n{stdoutTask.Result}\n{stderrTask.Result}");
        if (!File.Exists(outputPath))
            throw new InvalidOperationException("C# compiler reported success but did not produce the implementation assembly.");
        return outputPath;
    }

    private static List<string> CollectReferences(PreparedProfile prepared)
    {
        var references = new Dictionary<string, (string Identity, string Path)>(StringComparer.OrdinalIgnoreCase);
        void Add(string path, bool preferred)
        {
            path = Path.GetFullPath(path);
            var identity = AssemblyName.GetAssemblyName(path);
            var name = identity.Name ?? throw new InvalidDataException("Reference has no assembly name: " + path);
            if (references.TryGetValue(name, out var existing))
            {
                if (existing.Identity != identity.FullName)
                    throw new InvalidDataException($"Conflicting assembly identities for {name}: {existing.Path} ({existing.Identity}) and {path} ({identity.FullName}).");
                // Prepared target and framework references intentionally replace their source metadata copies.
                if (!preferred) return;
                if (PathAndHash.Sha256(existing.Path) != PathAndHash.Sha256(path))
                    throw new InvalidDataException($"Conflicting preferred references for {name}: {existing.Path} and {path}.");
            }
            references[name] = (identity.FullName, path);
        }
        foreach (var path in Directory.EnumerateFiles(FindFrameworkReferenceDirectory(prepared.TargetFramework), "*.dll", SearchOption.AllDirectories).Where(IsManagedAssembly).Order(StringComparer.Ordinal))
            Add(path, true);
        Add(prepared.CompileReferenceAssemblyPath, true);
        Add(prepared.IFixCoreAssemblyPath, true);
        var preferredNames = references.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in prepared.ReferenceDirectories)
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.dll").Where(IsManagedAssembly).Order(StringComparer.Ordinal))
            {
                var identity = AssemblyName.GetAssemblyName(path);
                if (references.TryGetValue(identity.Name!, out var existing) && !preferredNames.Contains(identity.Name!) &&
                    existing.Identity == identity.FullName && PathAndHash.Sha256(existing.Path) != PathAndHash.Sha256(path))
                    throw new InvalidDataException($"Conflicting reference contents for {identity.Name}: {existing.Path} and {path}.");
                Add(path, false);
            }
        }
        if (!references.ContainsKey("mscorlib"))
            throw new InvalidDataException("The reference set does not contain mscorlib.dll.");
        return references.Values.Select(reference => reference.Path).Order(StringComparer.Ordinal).ToList();
    }

    private static bool IsManagedAssembly(string path)
    {
        var fileName = Path.GetFileName(path);
        if (fileName is "System.EnterpriseServices.Wrapper.dll" or "System.EnterpriseServices.Thunk.dll")
            return false;
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new PEReader(stream);
            return reader.HasMetadata;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }

    private static string FindFrameworkReferenceDirectory(string targetFramework)
    {
        var (packageName, frameworkDirectory) = targetFramework.ToLowerInvariant() switch
        {
            "net471" => ("microsoft.netframework.referenceassemblies.net471", "v4.7.1"),
            _ => throw new NotSupportedException($"Unsupported compiler target framework: {targetFramework}"),
        };
        var packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrWhiteSpace(packages))
            packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        var packageRoot = Path.Combine(Path.GetFullPath(packages), packageName);
        if (!Directory.Exists(packageRoot))
            throw new DirectoryNotFoundException(
                $"Reference assembly package {packageName} is not restored under {packageRoot}.");
        var candidate = VersionDirectories(packageRoot)
            .Select(path => Path.Combine(path, "build", ".NETFramework", frameworkDirectory))
            .FirstOrDefault(Directory.Exists);
        return candidate ?? throw new DirectoryNotFoundException(
            $"Unable to locate {frameworkDirectory} reference assemblies in {packageRoot}.");
    }

    private static string FindCompiler()
    {
        var roots = new[]
        {
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            Path.GetDirectoryName(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")),
            new DirectoryInfo(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()).Parent?.Parent?.Parent?.FullName,
        }.Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p)).Distinct();

        // Keep an explicit installation ahead of the running host's installation.
        // Runtime-only roots have no sdk directory and must not prevent fallback.
        foreach (var root in roots)
        {
            var sdk = Path.Combine(root!, "sdk");
            if (!Directory.Exists(sdk)) continue;
            var candidate = VersionDirectories(sdk)
                .Select(path => Path.Combine(path, "Roslyn", "bincore", "csc.dll"))
                .FirstOrDefault(File.Exists);
            if (candidate is not null) return candidate;
        }
        throw new FileNotFoundException("Unable to locate Roslyn csc.dll in the configured or running .NET installation.");
    }

    private static IEnumerable<string> VersionDirectories(string root) => Directory.EnumerateDirectories(root)
        .OrderByDescending(path => Version.TryParse(Path.GetFileName(path).Split('-')[0], out var version) ? version : new Version(0, 0))
        .ThenBy(path => Path.GetFileName(path).Contains('-'))
        .ThenByDescending(path => path, StringComparer.Ordinal);
}
