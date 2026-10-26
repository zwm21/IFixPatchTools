using System.CommandLine;
using IFixPatch.Core;
using IFixPatch.Core.IR;

var root = new RootCommand("IFix patch recompiler — patch.bytes 重建（assemble / repatch）; 导出请用 IFixPatch.Decompiler export / export-il / disasm");

root.AddCommand(BuildAssembleCommand());
root.AddCommand(BuildRepatchCommand());
root.AddCommand(BuildValidateCommand());

Command BuildAssembleCommand()
{
    var ilArg = new Argument<FileInfo>("il", "input il text") { Arity = ArgumentArity.ExactlyOne };
    var baseOpt = new Option<FileInfo>("--base", "base patch.bytes for unchanged sections") { IsRequired = true };
    var outOpt = new Option<FileInfo>("--output", "output patch.bytes") { IsRequired = true };
    outOpt.AddAlias("-o");
    var dummyDllOpt = new Option<DirectoryInfo?>("--dummy-dll", "DummyDll for opcode map");
    var cmd = new Command("assemble", "patch.il (+ base) -> patch.bytes") { ilArg, baseOpt, outOpt, dummyDllOpt };
    cmd.SetHandler((il, basePatch, output, dummyDll) =>
    {
        IMetadataSource? src = null;
        if (dummyDll != null) src = new DummyDllSource(dummyDll.FullName);
        var basePf = PatchReader.ReadFile(basePatch.FullName, src);
        var map = src?.TryGetOpCodeMap() ?? new OpCodeMap();
        var text = File.ReadAllText(il.FullName);
        var pf = PatchTextAssembly.Assemble(text, basePf, map);
        var doc = PatchDocument.FromPatchFile(pf, map);
        var errs = doc.Validate();
        // lightweight code bounds check (branch targets within range, not into borrowed slots)
        foreach (var m in pf.Methods.Where(mm => mm.Code.Count > 0))
        {
            var boundsErrs = ValidateMethodBounds(m, map);
            errs.AddRange(boundsErrs);
        }
        if (errs.Count > 0)
        {
            Console.Error.WriteLine("assemble validation warnings:");
            foreach (var e in errs) Console.Error.WriteLine($"  - {e}");
        }
        PatchWriter.WriteFile(output.FullName, pf);
        var rt = PatchReader.ReadFile(output.FullName, src);
        if (rt.TrailingBytes != 0) Console.Error.WriteLine($"warn: trailing {rt.TrailingBytes}");
        else if (errs.Count == 0) Console.WriteLine($"assembled {pf.Methods.Count(m => !m.IsEmpty)} methods -> {output.FullName} (TrailingBytes=0)");
        else Console.WriteLine($"assembled {pf.Methods.Count(m => !m.IsEmpty)} methods -> {output.FullName} (TrailingBytes=0, {errs.Count} validation warnings)");
    }, ilArg, baseOpt, outOpt, dummyDllOpt);
    return cmd;
}

static List<string> ValidateMethodBounds(IFixMethod m, OpCodeMap map)
{
    var errs = new List<string>();
    var borrowed = new HashSet<int>();
    int i = 0;
    while (i < m.Code.Count)
    {
        var ins = m.Code[i];
        string? mn = map.TryName(ins.Code, out var n) ? n : null;
        if (mn == "Ldc_I8" || mn == "Ldc_R8")
        {
            if (i + 1 < m.Code.Count) borrowed.Add(i + 1);
            i += 2; continue;
        }
        if (mn == "Switch")
        {
            int n2 = ins.Operand;
            int slots = (n2 + 1) / 2;
            for (int s = 1; s <= slots && i + s < m.Code.Count; s++) borrowed.Add(i + s);
            i += 1 + slots; continue;
        }
        i++;
    }
    for (int idx = 0; idx < m.Code.Count; idx++)
    {
        if (borrowed.Contains(idx)) continue;
        var ins = m.Code[idx];
        if (!map.TryName(ins.Code, out var mn2)) continue;
        if (mn2 == "Br" || mn2 == "Brfalse" || mn2 == "Brtrue" || mn2 == "Beq" || mn2 == "Bge" || mn2 == "Bgt" || mn2 == "Ble" || mn2 == "Blt" || mn2 == "Bne_Un" || mn2 == "Bge_Un" || mn2 == "Bgt_Un" || mn2 == "Ble_Un" || mn2 == "Blt_Un")
        {
            int target = idx + ins.Operand;
            if (target < 0 || target >= m.Code.Count) errs.Add($"method#{m.Id} {mn2} at {idx} target {target} out of range");
            else if (borrowed.Contains(target)) errs.Add($"method#{m.Id} {mn2} at {idx} target {target} lands in borrowed slot");
        }
        else if (mn2 == "Leave")
        {
            int target = ins.Operand;
            if (target < 0 || target >= m.Code.Count) errs.Add($"method#{m.Id} Leave at {idx} target {target} out of range");
        }
        else if (mn2 == "Switch")
        {
            int n2 = ins.Operand;
            int slots = (n2 + 1) / 2;
            for (int s = 0; s < slots; s++)
            {
                if (idx + 1 + s >= m.Code.Count) { errs.Add($"method#{m.Id} Switch at {idx} missing slot {s}"); continue; }
                var slot = m.Code[idx + 1 + s];
                int t0 = idx + slot.Code;
                int t1 = idx + slot.Operand;
                if (s * 2 < n2 && (t0 < 0 || t0 >= m.Code.Count)) errs.Add($"method#{m.Id} Switch at {idx} target[{s * 2}] {t0} out of range");
                if (s * 2 + 1 < n2 && (t1 < 0 || t1 >= m.Code.Count)) errs.Add($"method#{m.Id} Switch at {idx} target[{s * 2 + 1}] {t1} out of range");
            }
        }
    }
    foreach (var eh in m.Handlers)
    {
        if (eh.TryStart < 0 || eh.TryEnd > m.Code.Count || eh.HandlerStart < 0 || eh.HandlerEnd > m.Code.Count) errs.Add($"method#{m.Id} eh range out of bounds try[{eh.TryStart}..{eh.TryEnd}) handler[{eh.HandlerStart}..{eh.HandlerEnd}) code={m.Code.Count}");
        if (eh.TryEnd != -1 && borrowed.Contains(eh.TryEnd)) errs.Add($"method#{m.Id} eh TryEnd {eh.TryEnd} borrowed");
        if (eh.HandlerEnd != -1 && eh.HandlerEnd != m.Code.Count && borrowed.Contains(eh.HandlerEnd)) errs.Add($"method#{m.Id} eh HandlerEnd {eh.HandlerEnd} borrowed");
    }
    return errs;
}

return await root.InvokeAsync(args);

Command BuildRepatchCommand()
{
    var irOpt = new Option<FileInfo?>("--ir", "input IR json (PatchDocument)");
    var patchOpt = new Option<FileInfo?>("--patch", "input patch.bytes (alternative to --ir)");
    var outOpt = new Option<FileInfo>("--output", "output patch.bytes") { IsRequired = true };
    outOpt.AddAlias("-o");
    var cmd = new Command("repatch", "IR/json or patch.bytes -> patch.bytes (with validation)") { irOpt, patchOpt, outOpt };
    cmd.SetHandler((ir, patch, output) =>
    {
        if (ir == null && patch == null) { Console.Error.WriteLine("need --ir or --patch"); Environment.Exit(2); }
        PatchFile pf;
        OpCodeMap map;
        if (ir != null)
        {
            var doc = PatchDocument.LoadJson(ir.FullName);
            var errs = doc.Validate();
            if (errs.Count > 0)
            {
                Console.Error.WriteLine("IR validation failed:");
                foreach (var e in errs) Console.Error.WriteLine($"  - {e}");
                Environment.Exit(2);
            }
            pf = doc.ToPatchFile(out map);
        }
        else
        {
            pf = PatchReader.ReadFile(patch!.FullName);
            map = new OpCodeMap();
        }
        // If both provided, merge: IR methods override patch methods
        if (ir != null && patch != null)
        {
            // patch is already overridden by ir in this simple model; for fine merge use ir-only
        }
        PatchWriter.WriteFile(output.FullName, pf);
        // verify round-trip
        var rt = PatchReader.ReadFile(output.FullName);
        if (rt.TrailingBytes != 0) Console.Error.WriteLine($"warn: repatch trailing {rt.TrailingBytes}");
        else Console.WriteLine($"repatched {pf.Methods.Count} methods -> {output.FullName} (TrailingBytes=0)");
    }, irOpt, patchOpt, outOpt);
    return cmd;
}

Command BuildValidateCommand()
{
    var irArg = new Argument<FileInfo>("ir", "IR json") { Arity = ArgumentArity.ExactlyOne };
    var cmd = new Command("validate", "validate IR json") { irArg };
    cmd.SetHandler(ir =>
    {
        var doc = PatchDocument.LoadJson(ir.FullName);
        var errs = doc.Validate();
        if (errs.Count == 0) Console.WriteLine("ok");
        else { foreach (var e in errs) Console.WriteLine(e); Environment.Exit(1); }
    }, irArg);
    return cmd;
}
