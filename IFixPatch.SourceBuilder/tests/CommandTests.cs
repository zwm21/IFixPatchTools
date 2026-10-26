using System.Reflection;
using System.Text.Json;
using IFixPatch.SourceBuilder.Core;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace IFixPatch.SourceBuilder.Tests;

public sealed class CommandTests : IClassFixture<BackendProcessFixture>
{
    private readonly BackendProcessFixture _backend;
    public CommandTests(BackendProcessFixture backend) => _backend = backend;

    [Theory]
    [InlineData("")]
    [InlineData("--help")]
    [InlineData("-h")]
    public async Task HelpListsAvailableCommands(string argument)
    {
        var result = await Invoke(argument.Length == 0 ? [] : [argument]);
        Assert.Equal(0, result.ExitCode);
        foreach (var command in new[] { "prepare", "build", "adapter", "generate", "verify" }) Assert.Contains(command, result.Output);
        Assert.Empty(result.Error);
    }

    [Fact]
    public async Task UnknownCommandReturnsDiagnostic()
    {
        var result = await Invoke("unknown-command");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Unknown command: unknown-command", result.Error);
    }

    [Theory]
    [InlineData("prepare")]
    [InlineData("build")]
    [InlineData("adapter")]
    [InlineData("generate")]
    [InlineData("verify")]
    public async Task MissingArgumentsReturnUsage(string command)
    {
        var result = await Invoke(command);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Usage:", result.Error);
    }

    [Fact]
    public async Task VerifyAcceptsRenamedExplicitTargetAndRuntimeMetadata()
    {
        using var fixture = new PreparedProfileFixture();
        var result = await Invoke("verify", fixture.ProfilePath, fixture.PatchPath, "--build-report", fixture.ReportPath);
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        using var report = VerificationReport(fixture);
        Assert.True(report.RootElement.GetProperty("byteIdenticalRoundTrip").GetBoolean());
        Assert.Empty(report.RootElement.GetProperty("errors").EnumerateArray());
    }

    [Fact]
    public async Task VerifyReportsWrongMagic()
    {
        using var fixture = new PreparedProfileFixture();
        fixture.Patch.Magic = 43; fixture.Save();
        Assert.Equal(2, (await Invoke("verify", fixture.ProfilePath, fixture.PatchPath)).ExitCode);
        AssertError(fixture, "magic");
    }

    [Fact]
    public async Task VerifyReportsInvalidDeclaringTypeWithoutThrowing()
    {
        using var fixture = new PreparedProfileFixture();
        fixture.Patch.FixList[0].Method.DeclaringTypeId = 100; fixture.Save();
        Assert.Equal(2, (await Invoke("verify", fixture.ProfilePath, fixture.PatchPath)).ExitCode);
        AssertError(fixture, "declaring type");
    }

    [Fact]
    public async Task VerifyRejectsFixForAnotherAssembly()
    {
        using var fixture = new PreparedProfileFixture();
        fixture.Patch.ExternTypes[0] = "Example.Counter, Other.Target, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
        fixture.Save();
        Assert.Equal(2, (await Invoke("verify", fixture.ProfilePath, fixture.PatchPath)).ExitCode);
        AssertError(fixture, "assembly identity");
    }

    [Fact]
    public async Task VerifyReportsTrailingBytes()
    {
        using var fixture = new PreparedProfileFixture();
        using (var stream = new FileStream(fixture.PatchPath, FileMode.Append)) stream.WriteByte(0xff);
        Assert.Equal(2, (await Invoke("verify", fixture.ProfilePath, fixture.PatchPath)).ExitCode);
        AssertError(fixture, "trailing bytes");
        using var report = VerificationReport(fixture);
        Assert.False(report.RootElement.GetProperty("byteIdenticalRoundTrip").GetBoolean());
    }

    [Fact]
    public async Task VerifyRejectsEmptyMethodBody()
    {
        using var fixture = new PreparedProfileFixture();
        fixture.Patch.Methods[0].Code.Clear(); fixture.Save();
        Assert.Equal(2, (await Invoke("verify", fixture.ProfilePath, fixture.PatchPath)).ExitCode);
        AssertError(fixture, "empty method bodies");
    }

    [Theory]
    [InlineData(-1, 2)]
    [InlineData(0, 2)]
    [InlineData(1, 0)]
    public async Task VerifyReconcilesInternalMethodsAgainstHelperReport(int helpers, int expectedExit)
    {
        using var fixture = new PreparedProfileFixture();
        fixture.AddInternalMethod();
        if (helpers == 1) fixture.Report.Helpers.Add(new GraftHelperReport());
        fixture.Save();
        var arguments = new List<string> { "verify", fixture.ProfilePath, fixture.PatchPath };
        if (helpers >= 0) arguments.AddRange(["--build-report", fixture.ReportPath]);
        Assert.Equal(expectedExit, (await Invoke(arguments.ToArray())).ExitCode);
        if (expectedExit != 0) AssertError(fixture, "internal methods");
    }

    [Fact]
    public async Task TypeGraphRequiresGenerationReport()
    {
        using var fixture = new PreparedProfileFixture();
        fixture.Report.HasTypeGraph = true; fixture.Save();
        Assert.Equal(2, (await VerifyGraph(fixture)).ExitCode);
        AssertError(fixture, "generation report");
    }

    [Fact]
    public async Task TypeGraphAcceptsMatchingGenerationProvenance()
    {
        using var fixture = new PreparedProfileFixture();
        fixture.Report.HasTypeGraph = true; fixture.AddInternalMethod(); fixture.Save(); fixture.SaveGenerationReport();
        Assert.Equal(0, (await VerifyGraph(fixture)).ExitCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TypeGraphRejectsDriftFromGeneratedPatchOrReport(bool patch)
    {
        using var fixture = new PreparedProfileFixture();
        fixture.Report.HasTypeGraph = true; fixture.Save(); fixture.SaveGenerationReport();
        if (patch) fixture.Patch.Methods[0].Code[1] = new() { Code = 20, Operand = 43 };
        else fixture.Report.Helpers.Add(new GraftHelperReport());
        fixture.Save();
        Assert.Equal(2, (await VerifyGraph(fixture)).ExitCode);
        AssertError(fixture, "does not match");
    }

    [Fact]
    public async Task StaleLayoutFailsBeforeWritingVerificationReport()
    {
        using var fixture = new PreparedProfileFixture();
        File.AppendAllText(fixture.Prepared.InjectionLayoutPath, "\n");
        var result = await Invoke("verify", fixture.ProfilePath, fixture.PatchPath);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Injection layout hash drifted", result.Error);
        Assert.False(File.Exists(fixture.PatchPath + ".verify.json"));
    }

    [Fact]
    public async Task OldPreparedProfileRequestsPrepareAgain()
    {
        using var fixture = new PreparedProfileFixture();
        fixture.Prepared.TargetAssemblyIdentity = "";
        SourceBuilderJson.Save(fixture.ProfilePath, fixture.Prepared);
        var result = await Invoke("verify", fixture.ProfilePath, fixture.PatchPath);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("run prepare again", result.Error);
    }

    [Theory]
    [InlineData(true, "Patch source changed")]
    [InlineData(false, "failed size check")]
    public async Task GenerateRejectsStaleOrFailedBuildWithoutReplacingOutput(bool staleSource, string expected)
    {
        using var fixture = new PreparedProfileFixture();
        if (staleSource) File.AppendAllText(fixture.SourcePath, "// changed\n");
        else fixture.Report.Diagnostics.Add(new BuildDiagnostic { Severity = "fail", Message = "Synthetic size bound exceeded." });
        fixture.Save();
        var output = Path.Combine(fixture.DirectoryPath, "protected.bytes");
        File.WriteAllText(output, "original");
        var result = await Invoke("generate", fixture.ProfilePath, fixture.ReportPath, "--injectfix", Path.Combine(fixture.DirectoryPath, "not-used.dll"), "-o", output);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains(expected, result.Error);
        Assert.Equal("original", File.ReadAllText(output));
        Assert.Empty(Directory.EnumerateFiles(fixture.DirectoryPath, "*.tmp.*"));
    }

    [Fact]
    public async Task GenerateUsesTheBackendProtocolAndRecordsExactArtifactProvenance()
    {
        using var fixture = new PreparedProfileFixture();
        var trace = BackendProcessFixture.Configure(fixture, "success");
        var output = Path.Combine(fixture.DirectoryPath, "generated.bytes");
        File.WriteAllText(output, "original");
        var expectedBytes = File.ReadAllBytes(fixture.PatchPath);
        var result = await Invoke("generate", fixture.ProfilePath, fixture.ReportPath, "--injectfix", _backend.AssemblyPath, "--output", output);
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        Assert.Contains("patch: " + output, result.Output);
        Assert.Equal(expectedBytes, File.ReadAllBytes(output));
        var arguments = JsonSerializer.Deserialize<string[]>(File.ReadAllText(trace))!;
        Assert.Equal(new[] { "-patch", fixture.Prepared.IFixCoreAssemblyPath, fixture.Report.GraftedAssembly, "null", fixture.Report.ProcessConfig }, arguments[..5]);
        Assert.StartsWith(output + ".tmp.", arguments[5]);
        Assert.Equal(fixture.Prepared.ReferenceDirectories, arguments[6..]);
        var generation = SourceBuilderJson.Load<GenerationReport>(output + ".generation.json");
        Assert.Equal(Hash(expectedBytes), generation.PatchSha256);
        Assert.Equal(Hash(File.ReadAllBytes(fixture.ReportPath)), generation.BuildReportSha256);
        Assert.Equal(1, generation.Methods);
        Assert.Equal(0, generation.InternalMethods);
        Assert.Contains("synthetic backend completed", File.ReadAllText(output + ".injectfix.log"));
        Assert.Empty(Directory.EnumerateFiles(fixture.DirectoryPath, "*.tmp.*"));
    }

    [Theory]
    [InlineData("exit-error", "exit code 7")]
    [InlineData("reported-error", "reported an error")]
    [InlineData("unhandled", "reported an error")]
    [InlineData("no-output", "without producing a patch")]
    public async Task BackendFailurePreservesTheExistingPatchAndCleansTemporaryOutput(string mode, string diagnostic)
    {
        using var fixture = new PreparedProfileFixture();
        var trace = BackendProcessFixture.Configure(fixture, mode);
        var output = Path.Combine(fixture.DirectoryPath, "protected.bytes");
        File.WriteAllText(output, "original");
        var result = await Invoke("generate", fixture.ProfilePath, fixture.ReportPath, "--injectfix", _backend.AssemblyPath, "-o", output);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains(diagnostic, result.Error);
        if (mode == "exit-error") Assert.Contains("synthetic backend stderr", result.Error);
        Assert.True(File.Exists(trace));
        Assert.Equal("original", File.ReadAllText(output));
        Assert.False(File.Exists(output + ".generation.json"));
        Assert.Empty(Directory.EnumerateFiles(fixture.DirectoryPath, "*.tmp.*"));
    }

    [Theory]
    [InlineData("binding", "differs from the build bindings")]
    [InlineData("method", "Unknown or ambiguous generated fix binding")]
    [InlineData("type", "Missing fix declaring type")]
    [InlineData("declaring-assembly", "assembly identity")]
    [InlineData("identity", "assembly identity")]
    [InlineData("class", "class inventory differs")]
    public async Task GeneratedPatchMustMatchTheBuildContractBeforeReplacingOutput(string mutation, string diagnostic)
    {
        using var fixture = new PreparedProfileFixture();
        if (mutation == "binding") fixture.Report.Bindings[0].Target = "System.Int32 Example.Counter::Another()";
        if (mutation == "method") fixture.Patch.FixList[0].Method.Name = "Unknown";
        if (mutation == "type") fixture.Patch.ExternTypes[0] = "Example.Missing, " + fixture.Prepared.TargetAssemblyIdentity;
        if (mutation == "declaring-assembly") fixture.Patch.ExternTypes[0] = "IFix.Core.Code, IFix.Core, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
        if (mutation == "identity") fixture.Patch.InterfaceBridgeType = "IFix.ILFixInterfaceBridge, Other.Target";
        if (mutation == "class") { fixture.Report.HasTypeGraph = true; fixture.Report.NewTypes.Add("Example.NewClass"); }
        BackendProcessFixture.Configure(fixture, "success");
        var output = Path.Combine(fixture.DirectoryPath, "protected.bytes");
        File.WriteAllText(output, "original");
        var result = await Invoke("generate", fixture.ProfilePath, fixture.ReportPath, "--injectfix", _backend.AssemblyPath, "-o", output);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains(diagnostic, result.Error);
        Assert.Equal("original", File.ReadAllText(output));
        Assert.Empty(Directory.EnumerateFiles(fixture.DirectoryPath, "*.tmp.*"));
    }

    [Theory]
    [InlineData("other-profile", "different prepared profile")]
    [InlineData("missing-artifact", "no longer exists")]
    [InlineData("artifact-hash", "hash drifted after build")]
    [InlineData("no-sources", "records no patch sources")]
    [InlineData("missing-source", "Patch source no longer exists")]
    public async Task GenerateRejectsBrokenInputProvenanceBeforeInvokingBackend(string mutation, string diagnostic)
    {
        using var fixture = new PreparedProfileFixture();
        var trace = BackendProcessFixture.Configure(fixture, "success");
        switch (mutation)
        {
            case "other-profile": fixture.Report.PreparedProfile = Path.Combine(fixture.DirectoryPath, "other.json"); break;
            case "missing-artifact": fixture.Report.Manifest = Path.Combine(fixture.DirectoryPath, "missing.json"); break;
            case "artifact-hash": fixture.Report.ManifestSha256 = new string('0', 64); break;
            case "no-sources": fixture.Report.Sources.Clear(); break;
            case "missing-source": File.Delete(fixture.SourcePath); break;
        }
        fixture.Save();
        var output = Path.Combine(fixture.DirectoryPath, "new.bytes");
        var result = await Invoke("generate", fixture.ProfilePath, fixture.ReportPath, "--injectfix", _backend.AssemblyPath, "-o", output);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains(diagnostic, result.Error);
        Assert.False(File.Exists(trace));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task GenerateRejectsConflictingBackendOptions()
    {
        using var fixture = new PreparedProfileFixture();
        var result = await Invoke("generate", fixture.ProfilePath, fixture.ReportPath, "--injectfix", _backend.AssemblyPath,
            "--injectfix-root", _backend.DirectoryPath, "-o", Path.Combine(fixture.DirectoryPath, "new.bytes"));
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("mutually exclusive", result.Error);
    }

    [Fact]
    public async Task GenerateReportsMissingBackendWithoutWritingPatch()
    {
        using var fixture = new PreparedProfileFixture();
        var output = Path.Combine(fixture.DirectoryPath, "new.bytes");
        var result = await Invoke("generate", fixture.ProfilePath, fixture.ReportPath, "--injectfix", Path.Combine(fixture.DirectoryPath, "absent.dll"), "-o", output);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("InjectFix CLI does not exist", result.Error);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task AdapterBuildPassesPreparedRuntimeSourceAndExplicitUpstreamRoot()
    {
        using var fixture = new PreparedProfileFixture();
        var output = Path.Combine(fixture.DirectoryPath, "adapter output");
        var root = Path.Combine(fixture.DirectoryPath, "upstream root");
        var result = await Invoke("adapter", fixture.ProfilePath, "-o", output, "--project", _backend.ProjectPath, "--injectfix-root", root);
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        Assert.True(File.Exists(Path.Combine(output, "IFix.dll")));
        Assert.Equal(new[] { fixture.Prepared.AdapterCoreSourcePath, root }, File.ReadAllLines(Path.Combine(output, "adapter-inputs.txt")));
        Assert.Contains("Build succeeded", File.ReadAllText(Path.Combine(output, "adapter-build.log")));
        Assert.Contains("synthetic adapter stderr", File.ReadAllText(Path.Combine(output, "adapter-build.log")));
        Assert.Contains("adapter: " + Path.Combine(output, "IFix.dll"), result.Output);
    }

    [Theory]
    [InlineData("missing", "adapter project was not found")]
    [InlineData("invalid", "adapter build failed")]
    [InlineData("wrong-output", "without producing IFix.dll")]
    public async Task AdapterReportsProjectAndBuildFailures(string mode, string diagnostic)
    {
        using var fixture = new PreparedProfileFixture();
        var project = Path.Combine(fixture.DirectoryPath, "Adapter.csproj");
        if (mode == "invalid") File.WriteAllText(project, "<not-a-project>");
        if (mode == "wrong-output") File.WriteAllText(project, "<Project><Target Name=\"Build\" /></Project>");
        var result = await Invoke("adapter", fixture.ProfilePath, "-o", Path.Combine(fixture.DirectoryPath, "adapter"), "--project", project);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains(diagnostic, result.Error);
    }

    [Theory]
    [InlineData("prepare")]
    [InlineData("build")]
    [InlineData("adapter")]
    [InlineData("generate")]
    public async Task MissingOutputOptionIsAnArgumentError(string command)
    {
        using var fixture = new PreparedProfileFixture();
        string[] args = command is "build" or "generate"
            ? [command, fixture.ProfilePath, fixture.ReportPath, "--unknown", "value"]
            : [command, fixture.ProfilePath, "--unknown", "value"];
        var result = await Invoke(args);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Missing option -o/--output", result.Error);
    }

    [Fact]
    public async Task VerifyWritesAnExplicitReportAndChecksGeneratedMethodInventory()
    {
        using var fixture = new PreparedProfileFixture();
        fixture.Report.HasTypeGraph = true; fixture.Save(); fixture.SaveGenerationReport();
        var generation = SourceBuilderJson.Load<GenerationReport>(fixture.PatchPath + ".generation.json");
        generation.Methods = 2;
        SourceBuilderJson.Save(fixture.PatchPath + ".generation.json", generation);
        var output = Path.Combine(fixture.DirectoryPath, "explicit-report.json");
        var result = await Invoke("verify", fixture.ProfilePath, fixture.PatchPath, "--build-report", fixture.ReportPath, "-o", output);
        Assert.Equal(2, result.ExitCode);
        using var report = JsonDocument.Parse(File.ReadAllText(output));
        Assert.Contains(report.RootElement.GetProperty("errors").EnumerateArray(), e => e.GetString()!.Contains("method inventory"));
        Assert.False(File.Exists(fixture.PatchPath + ".verify.json"));
    }

    [Theory]
    [InlineData("normal", 0)]
    [InlineData("warn", 0)]
    [InlineData("size-failure", 2)]
    [InlineData("recursive", 0)]
    public async Task PrepareAndBuildPersistUsableArtifactsAndExposeSizeDiagnostics(string mode, int expectedExit)
    {
        using var fixture = new InjectedTargetFixture();
        var preparedDirectory = Path.Combine(fixture.DirectoryPath, "prepared via CLI");
        var prepare = await Invoke("prepare", fixture.ProfilePath, "--output", preparedDirectory);
        Assert.Equal(0, prepare.ExitCode);
        Assert.Empty(prepare.Error);
        var profile = Path.Combine(preparedDirectory, "prepared-profile.json");
        var prepared = SourceBuilderJson.Load<PreparedProfile>(profile);
        Assert.Equal("Example.Game", prepared.TargetAssemblyName);
        foreach (var label in new[] { "prepared:", "clean shell:", "compile reference:", "field catalog:" }) Assert.Contains(label, prepare.Output);
        Assert.True(File.Exists(prepared.CleanAssemblyPath));
        Assert.True(File.Exists(prepared.CompileReferenceAssemblyPath));
        Assert.True(File.Exists(prepared.FieldCatalogPath));
        var sourcePath = Path.Combine(fixture.DirectoryPath, "Replacement.cs");
        File.WriteAllText(sourcePath, mode == "recursive"
            ? "namespace Patch { public static class Replacement { public static int Compute() => Again(1); static int Again(int x) => x == 0 ? 42 : Again(x - 1); } }"
            : "namespace Patch { public static class Replacement { public static int Compute() => Twice(20) + 2; static int Twice(int value) => value * 2; } }");
        var manifest = new PatchManifest
        {
            AssemblyName = "CliReplacement",
            Sources = [sourcePath],
            Patches = [new() { Target = "System.Int32 Example.Calculator::Compute()", Implementation = "System.Int32 Patch.Replacement::Compute()" }],
        };
        if (mode == "warn") { manifest.Limits.WarnHelperCount = 0; manifest.Limits.WarnInstructionCount = 0; }
        if (mode == "size-failure") manifest.Limits.FailEvaluationStackPercent = 0;
        var manifestPath = Path.Combine(fixture.DirectoryPath, "manifest.json");
        SourceBuilderJson.Save(manifestPath, manifest);
        var output = Path.Combine(fixture.DirectoryPath, "build via CLI");
        var result = await Invoke("build", profile, manifestPath, "--output", output);
        Assert.Equal(expectedExit, result.ExitCode);
        foreach (var label in new[] { "implementation:", "grafted assembly:", "process config:", "evaluation stack:" }) Assert.Contains(label, result.Output);
        var report = SourceBuilderJson.Load<BuildReport>(Path.Combine(output, "build-report.json"));
        Assert.Equal("System.Int32 Example.Calculator::Compute()", Assert.Single(report.Bindings).Target);
        Assert.Equal(3, report.Bindings[0].NativeSlot);
        Assert.Equal(Hash(File.ReadAllBytes(sourcePath)), Assert.Single(report.Sources).Sha256);
        Assert.True(File.Exists(report.ProcessConfig));
        Assert.Equal(42, Assembly.Load(File.ReadAllBytes(report.GraftedAssembly)).GetType("Example.Calculator")!.GetMethod("Compute")!.Invoke(null, null));
        if (mode == "normal") Assert.Empty(result.Error);
        if (mode == "warn") { Assert.Contains("warn: [helper-count]", result.Error); Assert.Contains("warn: [instruction-count]", result.Error); }
        if (mode == "size-failure") { Assert.Contains("fail: [evaluation-stack]", result.Error); Assert.Contains("generate will refuse", result.Error); }
        if (mode == "recursive") { Assert.Contains("no static bound", result.Output); Assert.Contains("recursive", result.Error); }
    }

    [Theory]
    [InlineData("matching", 0)]
    [InlineData("wrong-parameter-type", 1)]
    [InlineData("generic-parameter", 1)]
    public async Task GenerateResolvesBindingsUsingParameterTypes(string mode, int expectedExit)
    {
        using var fixture = new PreparedProfileFixture();
        fixture.AddParameterBinding();
        if (mode == "wrong-parameter-type") fixture.Patch.ExternTypes[1] = "System.String, mscorlib";
        if (mode == "generic-parameter")
        {
            fixture.Patch.FixList[0].Method.IsGenericInstance = true;
            fixture.Patch.FixList[0].Method.GenericArgTypeIds.Add(1);
            fixture.Patch.FixList[0].Method.Params[0].IsGeneric = true;
            fixture.Patch.FixList[0].Method.Params[0].GenericName = "T";
        }
        BackendProcessFixture.Configure(fixture, "success");
        var output = Path.Combine(fixture.DirectoryPath, "parameter.bytes");
        var result = await Invoke("generate", fixture.ProfilePath, fixture.ReportPath, "--injectfix", _backend.AssemblyPath, "-o", output);
        Assert.Equal(expectedExit, result.ExitCode);
        if (expectedExit == 0)
        {
            var patch = IFixPatch.Core.PatchReader.ReadFile(output);
            Assert.Equal("Echo", Assert.Single(patch.FixList).Method.Name);
            Assert.Equal(1, Assert.Single(patch.FixList[0].Method.Params).TypeId);
        }
        else
        {
            Assert.Contains("Unknown or ambiguous generated fix binding", result.Error);
            Assert.False(File.Exists(output));
        }
    }

    [Fact]
    public async Task GenerateAcceptsAnExactTypeGraphInventory()
    {
        using var fixture = new PreparedProfileFixture();
        fixture.Report.HasTypeGraph = true;
        fixture.Report.NewTypes.Add("Example.Added");
        fixture.Patch.NewClasses.Add("Example.Added, " + fixture.Prepared.TargetAssemblyIdentity);
        BackendProcessFixture.Configure(fixture, "success");
        var output = Path.Combine(fixture.DirectoryPath, "graph.bytes");
        Assert.Equal(0, (await Invoke("generate", fixture.ProfilePath, fixture.ReportPath, "--injectfix", _backend.AssemblyPath, "-o", output)).ExitCode);
        Assert.Equal(new[] { "Example.Added, " + fixture.Prepared.TargetAssemblyIdentity }, IFixPatch.Core.PatchReader.ReadFile(output).NewClasses);
        Assert.True(File.Exists(output + ".generation.json"));
    }

    [Fact]
    public async Task VerifyRejectsAnEmptyFixListWithThePublishedVerificationExitCode()
    {
        using var fixture = new PreparedProfileFixture();
        fixture.Patch.FixList.Clear(); fixture.Save();
        Assert.Equal(2, (await Invoke("verify", fixture.ProfilePath, fixture.PatchPath)).ExitCode);
        AssertError(fixture, "fix list is empty");
    }

    [Fact]
    public async Task VerifyCanUseAProfileWithoutAPatchMagicConstraint()
    {
        using var fixture = new PreparedProfileFixture();
        fixture.Prepared.KnownPatchMagic = null;
        SourceBuilderJson.Save(fixture.ProfilePath, fixture.Prepared);
        fixture.Patch.Magic = 123;
        fixture.Save();
        Assert.Equal(0, (await Invoke("verify", fixture.ProfilePath, fixture.PatchPath)).ExitCode);
        using var report = VerificationReport(fixture);
        Assert.Equal(123UL, report.RootElement.GetProperty("magic").GetUInt64());
        Assert.Empty(report.RootElement.GetProperty("errors").EnumerateArray());
    }

    [Fact]
    public async Task VerifyReportsBuildBindingMismatchesAsVerificationFailures()
    {
        using var fixture = new PreparedProfileFixture();
        fixture.Report.Bindings[0].Target = "System.Int32 Example.Counter::Different()";
        fixture.Save();
        Assert.Equal(2, (await VerifyGraph(fixture)).ExitCode);
        AssertError(fixture, "differs from the build bindings");
    }

    [Fact]
    public async Task TraceModeIncludesTheExecutionFailureDetails()
    {
        var previous = Environment.GetEnvironmentVariable("IFIX_SOURCEBUILDER_TRACE");
        try
        {
            Environment.SetEnvironmentVariable("IFIX_SOURCEBUILDER_TRACE", "1");
            var result = await Invoke("invalid-traced-command");
            Assert.Equal(1, result.ExitCode);
            Assert.Contains("System.ArgumentException", result.Error);
            Assert.Contains("Unknown command: invalid-traced-command", result.Error);
        }
        finally { Environment.SetEnvironmentVariable("IFIX_SOURCEBUILDER_TRACE", previous); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RelocatedGenerateBuildsItsDefaultAdapterOrExplainsTheMissingProject(bool includeProject)
    {
        using var fixture = new PreparedProfileFixture();
        var trace = BackendProcessFixture.Configure(fixture, "success");
        var layout = Path.Combine(fixture.DirectoryPath, "relocated");
        var app = Directory.CreateDirectory(Path.Combine(layout, "bin", "Release", "net10.0")).FullName;
        foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory))
            if (path.EndsWith(".dll", StringComparison.Ordinal) || path.EndsWith(".json", StringComparison.Ordinal))
                File.Copy(path, Path.Combine(app, Path.GetFileName(path)));
        if (includeProject)
        {
            var project = Directory.CreateDirectory(Path.Combine(layout, "tools", "InjectFix.Cli")).FullName;
            File.Copy(_backend.ProjectPath, Path.Combine(project, "InjectFix.Cli.csproj"));
            File.Copy(Path.Combine(_backend.DirectoryPath, "Program.cs"), Path.Combine(project, "Program.cs"));
        }
        var output = Path.Combine(fixture.DirectoryPath, "automatic.bytes");
        var start = new System.Diagnostics.ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { Path.Combine(app, "IFixPatch.SourceBuilder.dll"), "generate", fixture.ProfilePath, fixture.ReportPath, "--injectfix-root", _backend.DirectoryPath, "-o", output }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw new TimeoutException("Relocated CLI exceeded two minutes."); }
        var error = await stderr;
        Assert.Equal(includeProject ? 0 : 1, process.ExitCode);
        if (includeProject)
        {
            Assert.Empty(error);
            Assert.Contains("patch: " + output, await stdout);
            Assert.Equal(File.ReadAllBytes(fixture.PatchPath), File.ReadAllBytes(output));
            Assert.True(File.Exists(trace));
            Assert.True(File.Exists(output + ".generation.json"));
            Assert.Contains("Build succeeded", File.ReadAllText(output + ".adapter-build.log"));
            Assert.Contains("synthetic backend completed", File.ReadAllText(output + ".injectfix.log"));
        }
        else
        {
            Assert.Contains("adapter project was not found", error);
            Assert.Contains("--project", error);
            Assert.False(File.Exists(output));
            Assert.False(File.Exists(trace));
        }
    }

    [Fact]
    public async Task VerifyReportsUnresolvedInterfaceMetadataAsIncompleteParsing()
    {
        using var fixture = new PreparedProfileFixture();
        fixture.Patch.ExternTypes.Add("Example.MissingInterface, " + fixture.Prepared.TargetAssemblyIdentity);
        fixture.Patch.AnonStoreys.Add(new IFixPatch.Core.AnonStorey { CtorId = 0, HasInterfaces = true, Interfaces = [new() { TypeId = 1, MethodIds = [0] }] });
        fixture.Save();
        var result = await Invoke("verify", fixture.ProfilePath, fixture.PatchPath);
        Assert.Equal(2, result.ExitCode);
        AssertError(fixture, "did not fully parse");
        using var report = VerificationReport(fixture);
        Assert.False(report.RootElement.GetProperty("fullyParsed").GetBoolean());
        Assert.False(report.RootElement.GetProperty("byteIdenticalRoundTrip").GetBoolean());
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

    private static Task<(int ExitCode, string Output, string Error)> VerifyGraph(PreparedProfileFixture fixture) =>
        Invoke("verify", fixture.ProfilePath, fixture.PatchPath, "--build-report", fixture.ReportPath);

    private static JsonDocument VerificationReport(PreparedProfileFixture fixture) => JsonDocument.Parse(File.ReadAllText(fixture.PatchPath + ".verify.json"));

    private static void AssertError(PreparedProfileFixture fixture, string expected)
    {
        using var report = VerificationReport(fixture);
        Assert.Contains(report.RootElement.GetProperty("errors").EnumerateArray(), error => error.GetString()!.Contains(expected, StringComparison.Ordinal));
    }

    private static async Task<(int ExitCode, string Output, string Error)> Invoke(params string[] args)
    {
        var previousOutput = Console.Out; var previousError = Console.Error;
        using var output = new StringWriter(); using var error = new StringWriter();
        try
        {
            Console.SetOut(output); Console.SetError(error);
            var result = Assembly.Load("IFixPatch.SourceBuilder").EntryPoint!.Invoke(null, [args]);
            var exitCode = result is Task<int> pending ? await pending : (int)result!;
            return (exitCode, output.ToString(), error.ToString());
        }
        finally { Console.SetOut(previousOutput); Console.SetError(previousError); }
    }
}
