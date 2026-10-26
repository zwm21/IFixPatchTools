using System.Diagnostics;
using IFixPatch.SourceBuilder.Core;

namespace IFixPatch.SourceBuilder.Tests;

internal sealed class CompilerCommandFixture : IDisposable
{
    private readonly InjectedTargetFixture _target = new();
    public string DirectoryPath => _target.DirectoryPath;
    public string OutputPath => Path.Combine(DirectoryPath, "build");
    private string PreparedPath => Path.Combine(DirectoryPath, "prepared-profile.json");
    private string ManifestPath => Path.Combine(DirectoryPath, "manifest.json");

    public CompilerCommandFixture()
    {
        SourceBuilderJson.Save(PreparedPath, _target.Prepare());
        var source = Path.Combine(DirectoryPath, "Replacement.cs");
        File.WriteAllText(source, "namespace Patch {public static class Replacement {public static int Compute()=>6*7;}}");
        SourceBuilderJson.Save(ManifestPath, new PatchManifest
        {
            AssemblyName = "CompilerContracts",
            Sources = [source],
            Patches = [new() { Target = "System.Int32 Example.Calculator::Compute()", Implementation = "System.Int32 Patch.Replacement::Compute()" }]
        });
    }

    public async Task<(int ExitCode, string Output, string Error)> BuildAsync(Dictionary<string, string?> settings, string host = "dotnet")
    {
        var start = new ProcessStartInfo(host) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { Path.Combine(AppContext.BaseDirectory, "IFixPatch.SourceBuilder.dll"), "build", PreparedPath, ManifestPath, "-o", OutputPath })
            start.ArgumentList.Add(argument);
        foreach (var (name, value) in settings)
        {
            if (value is null) start.Environment.Remove(name);
            else start.Environment[name] = value;
        }
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout, await stderr);
    }

    public string CreateReferenceCache(string version, bool includeCoreLibrary)
    {
        var cache = Path.Combine(DirectoryPath, "packages");
        var framework = Path.Combine(cache, "microsoft.netframework.referenceassemblies.net471", version, "build", ".NETFramework", "v4.7.1");
        System.IO.Directory.CreateDirectory(framework);
        if (includeCoreLibrary) File.Copy(Path.Combine(AppContext.BaseDirectory, "ReferenceAssemblies", "mscorlib.dll"), Path.Combine(framework, "mscorlib.dll"));
        return cache;
    }

    public string CreateCompilerStub()
    {
        var project = Path.Combine(DirectoryPath, "Compiler.csproj");
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>csc</AssemblyName><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(DirectoryPath, "Program.cs"), """
            File.WriteAllText(Environment.GetEnvironmentVariable("IFIX_TEST_COMPILER_TRACE")!, typeof(Program).Assembly.Location);
            var code=int.Parse(Environment.GetEnvironmentVariable("IFIX_TEST_COMPILER_EXIT")!);
            if(code!=0) Console.Error.WriteLine("synthetic compiler failure");
            return code;
            """);
        var binary = Path.Combine(DirectoryPath, "compiler-bin");
        BackendProcessFixture.Build(project, binary);
        var sdkRoot = Path.Combine(DirectoryPath, "dotnet");
        foreach (var version in new[] { "9.0.999", "10.0.100-preview.1", "10.0.100", "development" })
        {
            var destination = Path.Combine(sdkRoot, "sdk", version, "Roslyn", "bincore");
            System.IO.Directory.CreateDirectory(destination);
            foreach (var file in System.IO.Directory.EnumerateFiles(binary)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
        return sdkRoot;
    }

    public void Dispose() => _target.Dispose();
}
