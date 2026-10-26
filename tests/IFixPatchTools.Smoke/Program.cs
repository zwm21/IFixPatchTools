using System.Diagnostics;
using System.Text.Json;
using IFixPatch.Core;
using IFixPatch.Core.IR;
using IFixPatch.SourceBuilder.Core;

var repository = new DirectoryInfo(AppContext.BaseDirectory);
while (repository is not null && !Directory.Exists(Path.Combine(repository.FullName, "samples", "roundtrip")))
    repository = repository.Parent;
if (repository is null) throw new InvalidOperationException("Repository root was not found.");
var root = repository.FullName;
var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
var temporary = Directory.CreateTempSubdirectory("ifix-smoke-").FullName;
try
{
    var document = PatchDocument.LoadJson(Path.Combine(root, "samples/roundtrip/sample.ir.json"));
    Require(document.Validate().Count == 0, "The public IR example must validate.");
    var patch = document.ToPatchFile(out var map);
    var patchPath = Path.Combine(temporary, "sample.bytes");
    PatchWriter.WriteFile(patchPath, patch);
    var exported = Path.Combine(temporary, "exported.ir.json");
    var rebuilt = Path.Combine(temporary, "rebuilt.bytes");
    await Run("IFixPatch.Decompiler", 0, "export", patchPath, "--opcode-map",
        Path.Combine(root, "samples/roundtrip/opcodes.txt"), "-o", exported);
    await Run("IFixPatch.Recompiler", 0, "repatch", "--ir", exported, "-o", rebuilt);
    Require(File.ReadAllBytes(patchPath).SequenceEqual(File.ReadAllBytes(rebuilt)), "CLI IR round-trip changed bytes.");

    // Exercise the assembler passes across branches and borrowed integer, float and Switch slots.
    var switched = new IFixMethod { Id = 1 };
    switched.Code.AddRange([
        Op("StackSpace", 1), Op("Ldc_I4", 0), Op("Switch", 2),
        new Instr { Code = 2, Operand = 4 }, Op("Ldc_I4", 10), Op("Br", 3),
        Op("Ldc_I4", 20), Op("Br", 1), Op("Ret", 1),
    ]);
    patch.Methods.Add(switched);
    var wide = new IFixMethod { Id = 2 };
    wide.Code.AddRange([
        Op("StackSpace", 2), Op("Ldc_I8", 0), new Instr { Code = 123, Operand = 4 }, Op("Pop", 0),
        Op("Ldc_R8", 0), new Instr { Code = 0, Operand = 1073217536 }, Op("Pop", 0), Op("Ret", 0),
    ]);
    wide.Handlers.Add(new Eh { HandlerType = 2, CatchTypeId = -1, TryStart = 1, TryEnd = 4, HandlerStart = 4, HandlerEnd = 8 });
    patch.Methods.Add(wide);
    var original = PatchWriter.WriteToBytes(patch);
    var text = PatchTextAssembly.Disassemble(patch, map);
    var assembled = PatchTextAssembly.Assemble(text, patch, map);
    Require(original.SequenceEqual(PatchWriter.WriteToBytes(assembled)), "Borrowed-slot or EH round-trip changed bytes.");
    var editedDocument = PatchDocument.FromPatchFile(patch, map);
    editedDocument.Methods[0].Code[1].Operand = 43;
    Require(!original.SequenceEqual(PatchWriter.WriteToBytes(editedDocument.ToPatchFile(out _))), "An edited IR must change the output.");
    Require(PatchDocument.FromPatchFile(patch, null).Methods[0].Code[0].Op.StartsWith("code#"), "Missing opcode maps must preserve raw values.");
    Console.WriteLine("PASS: public IR and borrowed-slot/EH round-trips");

    // Verify only needs these hashed inputs for consistency checks; no target DLL is loaded by this fixture.
    var input = Path.Combine(temporary, "synthetic-input.txt");
    File.WriteAllText(input, "Synthetic prepared-input fixture; not a target assembly.");
    var hash = PathAndHash.Sha256(input);
    var metadata = Directory.CreateDirectory(Path.Combine(temporary, "metadata")).FullName;
    var profile = new PreparedProfile
    {
        SourceAssemblyPath = input, SourceAssemblySha256 = hash,
        IFixCoreAssemblyPath = input, IFixCoreAssemblySha256 = hash,
        AdapterCoreSourcePath = input, AdapterCoreSourceSha256 = hash,
        CatalogPath = input, CatalogSha256 = hash,
        FieldCatalogPath = input, FieldCatalogSha256 = hash,
        CleanAssemblyPath = input, CleanAssemblySha256 = hash,
        CompileReferenceAssemblyPath = input, CompileReferenceAssemblySha256 = hash,
        KnownPatchMagic = patch.Magic,
        ReferenceDirectories = [metadata],
    };
    var profilePath = Path.Combine(temporary, "prepared-profile.json");
    SourceBuilderJson.Save(profilePath, profile);
    // The original fixture has exactly one nonempty fix body and no internal methods.
    var validPatch = document.ToPatchFile(out _);
    PatchWriter.WriteFile(patchPath, validPatch);
    await Run("IFixPatch.SourceBuilder", 0, "verify", profilePath, patchPath);

    validPatch.Methods[0].Code.Clear();
    PatchWriter.WriteFile(patchPath, validPatch);
    await Run("IFixPatch.SourceBuilder", 2, "verify", profilePath, patchPath);
    using (var report = JsonDocument.Parse(File.ReadAllText(patchPath + ".verify.json")))
        Require(report.RootElement.GetProperty("errors").EnumerateArray().Any(error => error.GetString()!.Contains("empty method bodies")),
            "Empty method rejection must appear in the verification report.");

    validPatch = document.ToPatchFile(out _);
    validPatch.Methods.Add(new IFixMethod { Id = 1 });
    validPatch.Methods[1].Code.AddRange([Op("StackSpace", 0), Op("Ret", 0)]);
    PatchWriter.WriteFile(patchPath, validPatch);
    await Run("IFixPatch.SourceBuilder", 2, "verify", profilePath, patchPath);
    var buildReport = Path.Combine(temporary, "build-report.json");
    SourceBuilderJson.Save(buildReport, new BuildReport { Helpers = [new GraftHelperReport()] });
    await Run("IFixPatch.SourceBuilder", 0, "verify", profilePath, patchPath, "--build-report", buildReport);
    SourceBuilderJson.Save(buildReport, new BuildReport());
    await Run("IFixPatch.SourceBuilder", 2, "verify", profilePath, patchPath, "--build-report", buildReport);
    Console.WriteLine("PASS: verify rejects empty bodies and reconciles internal methods");

    var diagnostics = await Run("IFixPatch.SourceBuilder", 1, "adapter", profilePath,
        "-o", Path.Combine(temporary, "adapter"), "--project",
        Path.Combine(root, "IFixPatch.SourceBuilder/tools/InjectFix.Cli/InjectFix.Cli.csproj"),
        "--injectfix-root", Path.Combine(temporary, "missing-upstream"));
    Require(diagnostics.Contains("InjectFix source was not found"), "The explicit InjectFix root must reach MSBuild.");
    Console.WriteLine("PASS: explicit upstream path and missing-source diagnostic");

    Instr Op(string name, int operand) => new() { Code = document.OpCodeMap[name], Operand = operand };
}
finally
{
    Directory.Delete(temporary, recursive: true);
}

void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

async Task<string> Run(string project, int expectedExit, params string[] arguments)
{
    var start = new ProcessStartInfo("dotnet")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    start.ArgumentList.Add(Path.Combine(root, project, "bin", configuration, "net10.0", project + ".dll"));
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start CLI.");
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var output = await stdout + await stderr;
    Require(process.ExitCode == expectedExit, $"{project} {arguments[0]} exited {process.ExitCode}, expected {expectedExit}:\n{output}");
    return output;
}
