using System.Diagnostics;
using System.Text.Json;
using IFixPatch.SourceBuilder.Core;

namespace IFixPatch.SourceBuilder.Tests;

/// <summary>A protocol stub for CLI orchestration, not an InjectFix implementation or VM.</summary>
public sealed class BackendProcessFixture : IDisposable
{
    public string DirectoryPath { get; } = Directory.CreateTempSubdirectory("ifix-backend-stub-").FullName;
    public string ProjectPath => Path.Combine(DirectoryPath, "Backend.csproj");
    public string AssemblyPath => Path.Combine(DirectoryPath, "bin", "IFix.dll");

    public BackendProcessFixture()
    {
        File.WriteAllText(ProjectPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>IFix</AssemblyName><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <UsingTask TaskName="EmitStderr" TaskFactory="RoslynCodeTaskFactory" AssemblyFile="$(MSBuildToolsPath)/Microsoft.Build.Tasks.Core.dll">
                <Task><Code Type="Fragment" Language="cs"><![CDATA[System.Console.Error.WriteLine("synthetic adapter stderr");]]></Code></Task>
              </UsingTask>
              <Target Name="RecordAdapterInputs" AfterTargets="Build">
                <EmitStderr />
                <WriteLinesToFile File="$(TargetDir)adapter-inputs.txt" Lines="$(GameIFixCoreSource);$(InjectFixRoot)" Overwrite="true" />
              </Target>
            </Project>
            """);
        File.WriteAllText(Path.Combine(DirectoryPath, "Program.cs"), """
            using System.Text.Json;
            var config = JsonDocument.Parse(File.ReadAllText(args[4])).RootElement;
            File.WriteAllText(config.GetProperty("trace").GetString()!, JsonSerializer.Serialize(args));
            var mode = config.GetProperty("mode").GetString();
            if (mode != "no-output") File.Copy(config.GetProperty("template").GetString()!, args[5]);
            Console.WriteLine(mode == "reported-error" ? "Error: synthetic backend error" : mode == "unhandled" ? "Unhandled Exception: synthetic backend exception" : "synthetic backend completed");
            if (mode == "exit-error") { Console.Error.WriteLine("synthetic backend stderr"); return 7; }
            return 0;
            """);
        Build(ProjectPath, Path.Combine(DirectoryPath, "bin"));
    }

    public static void Build(string project, string output)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "build", project, "-c", "Release", "-o", output, "-p:NuGetAudit=false" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000)) { process.Kill(true); throw new TimeoutException("Protocol stub compilation exceeded two minutes."); }
        if (process.ExitCode != 0) throw new InvalidOperationException(stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }

    internal static string Configure(PreparedProfileFixture fixture, string mode)
    {
        var config = Path.Combine(fixture.DirectoryPath, "backend-config.json");
        var trace = Path.Combine(fixture.DirectoryPath, "backend-arguments.json");
        File.WriteAllText(config, JsonSerializer.Serialize(new { mode, trace, template = fixture.PatchPath }));
        fixture.Report.ProcessConfig = config;
        fixture.Report.ProcessConfigSha256 = PathAndHash.Sha256(config);
        fixture.Save();
        return trace;
    }

    public void Dispose() => Directory.Delete(DirectoryPath, true);
}
