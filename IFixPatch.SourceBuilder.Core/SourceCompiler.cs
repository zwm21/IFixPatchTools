using System.Diagnostics;
using System.Reflection.PortableExecutable;

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
            FileName = "dotnet",
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
        var references = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in prepared.ReferenceDirectories)
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.dll").Where(IsManagedAssembly))
                references[Path.GetFileNameWithoutExtension(path)] = Path.GetFullPath(path);
        }
        foreach (var path in Directory.EnumerateFiles(FindFrameworkReferenceDirectory(prepared.TargetFramework), "*.dll", SearchOption.AllDirectories).Where(IsManagedAssembly))
            references[Path.GetFileNameWithoutExtension(path)] = Path.GetFullPath(path);
        references["Assembly-CSharp"] = prepared.CompileReferenceAssemblyPath;
        references["IFix.Core"] = prepared.IFixCoreAssemblyPath;
        if (!references.ContainsKey("mscorlib"))
            throw new InvalidDataException("The reference set does not contain mscorlib.dll.");
        return references.Values.Order(StringComparer.Ordinal).ToList();
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
        var packageRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget", "packages", packageName);
        if (!Directory.Exists(packageRoot))
            throw new DirectoryNotFoundException(
                $"Reference assembly package {packageName} is not restored under {packageRoot}.");
        var candidate = Directory.EnumerateDirectories(packageRoot)
            .OrderByDescending(path => path, StringComparer.Ordinal)
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
            "/usr/share/dotnet",
        }.Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p)).Distinct();

        var candidates = roots.SelectMany(root => Directory.EnumerateDirectories(Path.Combine(root!, "sdk")))
            .Select(path => Path.Combine(path, "Roslyn", "bincore", "csc.dll"))
            .Where(File.Exists)
            .OrderByDescending(path => path, StringComparer.Ordinal)
            .ToList();
        return candidates.FirstOrDefault()
            ?? throw new FileNotFoundException("Unable to locate Roslyn csc.dll under DOTNET_ROOT/sdk.");
    }
}
