namespace IFixPatch.SourceBuilder.Tests;

public sealed class CompilerProtocolTests
{
    [Fact]
    public async Task HonorsAnExplicitHostPathInsteadOfSilentlyUsingAnotherHost()
    {
        using var fixture = new CompilerCommandFixture();
        var missing = Path.Combine(fixture.DirectoryPath, "missing-dotnet-host");
        var result = await fixture.BuildAsync(new() { ["DOTNET_HOST_PATH"] = missing });
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("missing-dotnet-host", result.Error);
        Assert.False(File.Exists(Path.Combine(fixture.OutputPath, "build-report.json")));
    }

    [Theory]
    [InlineData(0, "did not produce")]
    [InlineData(7, "exit code 7")]
    public async Task ReportsCompilerProtocolFailuresAndSelectsTheNewestStableSdk(int exitCode, string diagnostic)
    {
        using var fixture = new CompilerCommandFixture();
        var root = fixture.CreateCompilerStub();
        var trace = Path.Combine(fixture.DirectoryPath, "compiler.txt");
        // The stub checks process orchestration only; real compilation is covered separately.
        var result = await fixture.BuildAsync(new()
        {
            ["DOTNET_ROOT"] = root,
            ["IFIX_TEST_COMPILER_TRACE"] = trace,
            ["IFIX_TEST_COMPILER_EXIT"] = exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(diagnostic, result.Error);
        var selected = File.ReadAllText(trace);
        Assert.Contains(Path.Combine("sdk", "10.0.100", "Roslyn", "bincore"), selected);
        Assert.False(File.Exists(Path.Combine(fixture.OutputPath, "build-report.json")));
        if (exitCode != 0) Assert.Contains("synthetic compiler failure", result.Error);
    }
}
