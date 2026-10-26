using System.CommandLine;
using IFixPatch.Core;
using IFixPatch.Core.IR;

var root = new RootCommand("IFix patch recompiler — validated IR rebuilding and IL assembly");
var exitCode = 0;
void Guard(Action action)
{
    try { action(); }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or FormatException or OverflowException or System.Text.Json.JsonException)
    { Console.Error.WriteLine("error: " + error.Message); exitCode = 2; }
}
var assemble = new Command("assemble", "patch.il (+ base) -> patch.bytes");
var il = new Argument<FileInfo>("il");
var baseFile = new Option<FileInfo>("--base") { IsRequired = true };
var assembledOutput = new Option<FileInfo>("--output") { IsRequired = true }; assembledOutput.AddAlias("-o");
var metadata = new Option<DirectoryInfo?>("--dummy-dll");
assemble.AddArgument(il); assemble.AddOption(baseFile); assemble.AddOption(assembledOutput); assemble.AddOption(metadata);
assemble.SetHandler((FileInfo input, FileInfo basis, FileInfo output, DirectoryInfo? directory) => Guard(() =>
{
    using var source = directory is null ? null : new DummyDllSource(directory.FullName);
    var patch = PatchReader.ReadFile(basis.FullName, source);
    var rebuilt = PatchTextAssembly.Assemble(File.ReadAllText(input.FullName), patch, source?.TryGetOpCodeMap() ?? new OpCodeMap(), out var map);
    PatchWriter.WriteValidatedFile(output.FullName, rebuilt, map);
    Console.WriteLine($"assembled {rebuilt.Methods.Count} methods -> {output.FullName}");
}), il, baseFile, assembledOutput, metadata);
root.AddCommand(assemble);
var repatch = new Command("repatch", "IR or fully parsed binary patch -> patch.bytes");
var ir = new Option<FileInfo?>("--ir"); var binary = new Option<FileInfo?>("--patch");
var repatchedOutput = new Option<FileInfo>("--output") { IsRequired = true }; repatchedOutput.AddAlias("-o");
var repatchMetadata = new Option<DirectoryInfo?>("--dummy-dll");
repatch.AddOption(ir); repatch.AddOption(binary); repatch.AddOption(repatchedOutput); repatch.AddOption(repatchMetadata);
repatch.SetHandler((FileInfo? input, FileInfo? patchFile, FileInfo output, DirectoryInfo? directory) => Guard(() =>
{
    if ((input is null) == (patchFile is null)) throw new InvalidDataException("Specify exactly one of --ir and --patch.");
    using var source = directory is null ? null : new DummyDllSource(directory.FullName);
    PatchFile patch; OpCodeMap map;
    if (input is not null)
    {
        var document = PatchDocument.LoadJson(input.FullName);
        var errors = document.Validate();
        if (errors.Count != 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        patch = document.ToPatchFile(out map);
    }
    else { patch = PatchReader.ReadFile(patchFile!.FullName, source); map = source?.TryGetOpCodeMap() ?? new OpCodeMap(); }
    PatchWriter.WriteValidatedFile(output.FullName, patch, map);
    Console.WriteLine($"repatched {patch.Methods.Count} methods -> {output.FullName}");
}), ir, binary, repatchedOutput, repatchMetadata);
root.AddCommand(repatch);
var validate = new Command("validate", "validate IR structure, references and instruction boundaries");
var documentFile = new Argument<FileInfo>("ir"); validate.AddArgument(documentFile);
validate.SetHandler((FileInfo input) => Guard(() =>
{
    var errors = PatchDocument.LoadJson(input.FullName).Validate();
    if (errors.Count != 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
    Console.WriteLine("ok");
}), documentFile);
root.AddCommand(validate);
var parseExit = await root.InvokeAsync(args);
return parseExit == 0 ? exitCode : parseExit;
