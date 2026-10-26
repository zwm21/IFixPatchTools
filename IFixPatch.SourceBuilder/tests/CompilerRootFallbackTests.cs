using System.Diagnostics;
using System.Reflection;
using IFixPatch.SourceBuilder.Core;

namespace IFixPatch.SourceBuilder.Tests;

public sealed class CompilerRootFallbackTests
{
    [Theory]
    [InlineData("blank")]
    [InlineData("missing")]
    [InlineData("runtime-only")]
    [InlineData("empty-sdk")]
    [InlineData("no-compiler")]
    public async Task BuildFallsBackToTheDefaultSdkWhenTheChildProcessHasAnInvalidDotnetRoot(string state)
    {
        using var fixture = new InjectedTargetFixture();
        var prepared = fixture.Prepare();
        var preparedPath = Path.Combine(fixture.DirectoryPath, "prepared-profile.json");
        SourceBuilderJson.Save(preparedPath, prepared);
        var source = Path.Combine(fixture.DirectoryPath, "PatchImpl.cs");
        File.WriteAllText(source, "namespace Patch { public static class Replacement { public static int Compute() => 6 * 7; } }");
        var manifestPath = Path.Combine(fixture.DirectoryPath, "manifest.json");
        SourceBuilderJson.Save(manifestPath, new PatchManifest
        {
            AssemblyName = "FallbackImplementation",
            Sources = [source],
            Patches = [new PatchBinding { Target = "System.Int32 Example.Calculator::Compute()", Implementation = "System.Int32 Patch.Replacement::Compute()" }],
        });
        var output = Path.Combine(fixture.DirectoryPath, "fallback-build");
        var originalDotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        var missingRoot = Path.Combine(fixture.DirectoryPath, "missing-dotnet-root");
        if (state == "runtime-only") Directory.CreateDirectory(missingRoot);
        if (state == "empty-sdk") Directory.CreateDirectory(Path.Combine(missingRoot, "sdk"));
        if (state == "no-compiler") Directory.CreateDirectory(Path.Combine(missingRoot, "sdk", "10.0.100"));
        var existed = Directory.Exists(missingRoot);
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { Path.Combine(AppContext.BaseDirectory, "IFixPatch.SourceBuilder.dll"), "build", preparedPath, manifestPath, "-o", output })
            start.ArgumentList.Add(argument);
        // Only the child gets an incorrect SDK root. Its normal default installation
        // remains available, and the host environment and package cache are untouched.
        start.Environment["DOTNET_ROOT"] = state == "blank" ? "" : missingRoot;
        using var child = Process.Start(start)!;
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        await child.WaitForExitAsync();
        var capturedOutput = await stdout;
        var capturedError = await stderr;
        Assert.True(child.ExitCode == 0, $"CLI exited {child.ExitCode}.\n{capturedOutput}\n{capturedError}");
        Assert.Empty(capturedError);
        Assert.Equal(originalDotnetRoot, Environment.GetEnvironmentVariable("DOTNET_ROOT"));
        Assert.Equal(existed, Directory.Exists(missingRoot));
        var report = SourceBuilderJson.Load<BuildReport>(Path.Combine(output, "build-report.json"));
        var assembly = Assembly.Load(File.ReadAllBytes(report.GraftedAssembly));
        Assert.Equal(42, assembly.GetType("Example.Calculator")!.GetMethod("Compute")!.Invoke(null, null));
        Assert.True(File.Exists(report.ImplementationAssembly));
        Assert.True(File.Exists(report.ProcessConfig));
    }
}
