using System.Runtime.InteropServices;

namespace IFixPatch.SourceBuilder.Tests;

public sealed class CompilerInstallationTests
{
    [Fact]
    public async Task RuntimeOnlyInstallationReportsTheMissingCompilerWithoutUsingAnotherInstallation()
    {
        using var fixture = new CompilerCommandFixture();
        var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        var installation = runtime.Parent!.Parent!.Parent!;
        var isolated = Path.Combine(fixture.DirectoryPath, "runtime-only");
        var executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        Directory.CreateDirectory(isolated);
        var host = Path.Combine(isolated, executable);
        File.Copy(Path.Combine(installation.FullName, executable), host);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(host, File.GetUnixFileMode(Path.Combine(installation.FullName, executable)));
        // Copy a usable runtime without its SDK. The real installation and the parent process
        // stay untouched, so failure must come from compiler discovery rather than host startup.
        CopyDirectory(Path.Combine(installation.FullName, "host"), Path.Combine(isolated, "host"));
        CopyDirectory(runtime.FullName, Path.Combine(isolated, "shared", "Microsoft.NETCore.App", runtime.Name));
        var result = await fixture.BuildAsync(new()
        {
            ["DOTNET_ROOT"] = isolated,
            ["DOTNET_HOST_PATH"] = host,
            ["DOTNET_MULTILEVEL_LOOKUP"] = "0",
        }, host);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Unable to locate Roslyn csc.dll", result.Error);
        Assert.DoesNotContain("You must install", result.Error);
        Assert.False(File.Exists(Path.Combine(fixture.OutputPath, "build-report.json")));
        Assert.False(Directory.Exists(Path.Combine(isolated, "sdk")));
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(source)) CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
}
