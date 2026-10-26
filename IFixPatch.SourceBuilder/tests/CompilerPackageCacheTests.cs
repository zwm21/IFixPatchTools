using System.Reflection;
using IFixPatch.SourceBuilder.Core;

namespace IFixPatch.SourceBuilder.Tests;

public sealed class CompilerPackageCacheTests
{
    [Theory]
    [InlineData("missing", "not restored")]
    [InlineData("incomplete", "Unable to locate")]
    [InlineData("no-core", "does not contain mscorlib")]
    public async Task ReportsUnavailableReferencesInTheConfiguredPackageCache(string state, string diagnostic)
    {
        using var fixture = new CompilerCommandFixture();
        var cache = Path.Combine(fixture.DirectoryPath, "packages");
        if (state == "incomplete") Directory.CreateDirectory(Path.Combine(cache, "microsoft.netframework.referenceassemblies.net471", "1.0.3"));
        if (state == "no-core") fixture.CreateReferenceCache("1.0.3", false);
        var result = await fixture.BuildAsync(new() { ["NUGET_PACKAGES"] = cache });
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(diagnostic, result.Error);
        Assert.False(File.Exists(Path.Combine(fixture.OutputPath, "build-report.json")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public async Task UsesTheDefaultFrameworkCacheWhenTheSettingIsBlank(string? setting)
    {
        using var fixture = new CompilerCommandFixture();
        var package = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget", "packages", "microsoft.netframework.referenceassemblies.net471");
        var available = Directory.Exists(package) && Directory.EnumerateFiles(package, "mscorlib.dll", SearchOption.AllDirectories)
            .Any(path => path.EndsWith(Path.Combine("build", ".NETFramework", "v4.7.1", "mscorlib.dll"), StringComparison.Ordinal));
        var result = await fixture.BuildAsync(new() { ["NUGET_PACKAGES"] = setting });
        if (!available)
        {
            // A custom-cache installation may have no default cache. It must report that
            // missing dependency instead of falling back to the parent's configured cache.
            Assert.NotEqual(0, result.ExitCode);
            Assert.False(File.Exists(Path.Combine(fixture.OutputPath, "build-report.json")));
            return;
        }
        Assert.True(result.ExitCode == 0, result.Output + result.Error);
        var report = SourceBuilderJson.Load<BuildReport>(Path.Combine(fixture.OutputPath, "build-report.json"));
        var assembly = Assembly.Load(File.ReadAllBytes(report.GraftedAssembly));
        Assert.Equal(42, assembly.GetType("Example.Calculator")!.GetMethod("Compute")!.Invoke(null, null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UsesConfiguredReferencesAndOrdersVersionsNumerically(bool multipleVersions)
    {
        using var fixture = new CompilerCommandFixture();
        if (multipleVersions) fixture.CreateReferenceCache("1.0.9", false);
        var cache = fixture.CreateReferenceCache("1.0.10", true);
        var original = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        var result = await fixture.BuildAsync(new() { ["NUGET_PACKAGES"] = cache });
        Assert.True(result.ExitCode == 0, result.Output + result.Error);
        Assert.Equal(original, Environment.GetEnvironmentVariable("NUGET_PACKAGES"));
        var report = SourceBuilderJson.Load<BuildReport>(Path.Combine(fixture.OutputPath, "build-report.json"));
        var assembly = Assembly.Load(File.ReadAllBytes(report.GraftedAssembly));
        Assert.Equal(42, assembly.GetType("Example.Calculator")!.GetMethod("Compute")!.Invoke(null, null));
    }
}
