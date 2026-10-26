using System.CommandLine;
using System.Text.Json;
using IFixPatch.Core;
using IFixPatchDecompiler;
using IFixPatch.Core.IR;

int exitCode = 0;

var root = new RootCommand("IFix patch decompiler — 解析 / 反汇编 / 反编译 InjectFix *.patch.bytes 热更补丁");

var infoCmd = BuildInfoCommand();
var disasmCmd = BuildDisasmCommand();
var decompileCmd = BuildDecompileCommand();
var exportCmd = BuildExportCommand();

root.AddCommand(infoCmd);
root.AddCommand(disasmCmd);
root.AddCommand(decompileCmd);
root.AddCommand(exportCmd);

int parseExit = await root.InvokeAsync(args);
if (parseExit != 0) return parseExit;
return exitCode;

Command BuildInfoCommand()
{
    var patchArg = new Argument<FileInfo>("patch", "补丁文件路径 (*.patch.bytes)") { Arity = ArgumentArity.ExactlyOne };
    patchArg.HelpName = "PATCH";
    var dummyDllOpt = new Option<DirectoryInfo?>("--dummy-dll", "Il2CppDumper DummyDll 目录（首选，支持转译，提供完整类型身份）");
    var dumpOpt = new Option<FileInfo?>("--dump", "Il2CppDumper dump.cs 单文件（仅解析/反汇编，短名易歧义）");
    var opcodeOpt = new Option<FileInfo?>("--opcode-map", "单独覆盖 Code 枚举映射（手写 Name=数值 或 dump.cs）");
    var jsonOpt = new Option<bool>("--json", "以 JSON 输出概览（便于脚本处理）");
    var outputOpt = new Option<FileInfo?>("--output", "输出文件（默认 stdout）");
    outputOpt.AddAlias("-o");

    var cmd = new Command("info", "显示补丁概览：Magic、修复列表、字符串池、匿名类等");
    cmd.AddArgument(patchArg);
    cmd.AddOption(dummyDllOpt);
    cmd.AddOption(dumpOpt);
    cmd.AddOption(opcodeOpt);
    cmd.AddOption(jsonOpt);
    cmd.AddOption(outputOpt);

    cmd.SetHandler((FileInfo patch, DirectoryInfo? dummyDll, FileInfo? dump, FileInfo? opcodeMap, bool json, FileInfo? output) => { exitCode = RunInfo(patch, dummyDll, dump, opcodeMap, json, output); }, patchArg, dummyDllOpt, dumpOpt, opcodeOpt, jsonOpt, outputOpt);

    return cmd;
}

Command BuildDisasmCommand()
{
    var patchArg = new Argument<FileInfo>("patch", "补丁文件路径 (*.patch.bytes)") { Arity = ArgumentArity.ExactlyOne };
    patchArg.HelpName = "PATCH";
    var dummyDllOpt = new Option<DirectoryInfo?>("--dummy-dll", "Il2CppDumper DummyDll 目录");
    var dumpOpt = new Option<FileInfo?>("--dump", "Il2CppDumper dump.cs 单文件");
    var opcodeOpt = new Option<FileInfo?>("--opcode-map", "单独覆盖 Code 枚举映射");
    var outputOpt = new Option<FileInfo?>("--output", "输出文件（默认 stdout）");
    outputOpt.AddAlias("-o");

    var cmd = new Command("disasm", "反汇编为可编辑 il 文本（.meta/.opcode_map/.method/Lxxxx:/.eh，可由 recompiler assemble 重建）");
    cmd.AddArgument(patchArg);
    cmd.AddOption(dummyDllOpt);
    cmd.AddOption(dumpOpt);
    cmd.AddOption(opcodeOpt);
    cmd.AddOption(outputOpt);

    cmd.SetHandler((FileInfo patch, DirectoryInfo? dummyDll, FileInfo? dump, FileInfo? opcodeMap, FileInfo? output) => { exitCode = RunDisasm(patch, dummyDll, dump, opcodeMap, output); }, patchArg, dummyDllOpt, dumpOpt, opcodeOpt, outputOpt);

    return cmd;
}

Command BuildDecompileCommand()
{
    var patchArg = new Argument<FileInfo>("patch", "补丁文件路径 (*.patch.bytes)") { Arity = ArgumentArity.ExactlyOne };
    patchArg.HelpName = "PATCH";
    var dummyDllOpt = new Option<DirectoryInfo>("--dummy-dll", "Il2CppDumper DummyDll 目录（反编译必需）") { IsRequired = true };
    var opcodeOpt = new Option<FileInfo?>("--opcode-map", "单独覆盖 Code 枚举映射（默认从 DummyDll 自动提取）");
    var outputOpt = new Option<FileInfo>("--output", "输出 C# 文件路径") { IsRequired = true };
    outputOpt.AddAlias("-o");

    var cmd = new Command("decompile", "IL 转译并反编译为 C#（需 --dummy-dll）");
    cmd.AddArgument(patchArg);
    cmd.AddOption(dummyDllOpt);
    cmd.AddOption(opcodeOpt);
    cmd.AddOption(outputOpt);

    cmd.SetHandler((FileInfo patch, DirectoryInfo dummyDll, FileInfo? opcodeMap, FileInfo output) => { exitCode = RunDecompile(patch, dummyDll, opcodeMap, output); }, patchArg, dummyDllOpt, opcodeOpt, outputOpt);

    return cmd;
}

static IMetadataSource? CreateMetadataSource(DirectoryInfo? dummyDll, FileInfo? dump, TextWriter err)
{
    if (dummyDll != null && dump != null)
    {
        err.WriteLine("error: --dummy-dll 与 --dump 互斥，请二选一");
        return null;
    }
    try
    {
        if (dummyDll != null)
        {
            if (!dummyDll.Exists) { err.WriteLine($"error: DummyDll 目录不存在: {dummyDll.FullName}"); return null; }
            return new DummyDllSource(dummyDll.FullName);
        }
        if (dump != null)
        {
            if (!dump.Exists) { err.WriteLine($"error: dump.cs 不存在: {dump.FullName}"); return null; }
            return new DumpCsSource(dump.FullName);
        }
    }
    catch (Exception e)
    {
        err.WriteLine($"error: 建立元数据源失败: {e.Message}");
        return null;
    }
    return null;
}

static OpCodeMap ResolveOpcodeMap(FileInfo? opcodeMapFile, IMetadataSource? types, TextWriter err, out bool ok)
{
    ok = true;
    try
    {
        if (opcodeMapFile != null)
        {
            if (!opcodeMapFile.Exists) { err.WriteLine($"error: opcode 映射文件不存在: {opcodeMapFile.FullName}"); ok = false; return new OpCodeMap(); }
            return OpCodeMap.FromFile(opcodeMapFile.FullName);
        }
        return types?.TryGetOpCodeMap() ?? new OpCodeMap();
    }
    catch (Exception e)
    {
        err.WriteLine($"error: 加载 opcode 映射失败: {e.Message}");
        ok = false;
        return new OpCodeMap();
    }
}

static void PrintOpCodeValidation(OpCodeMap map, TextWriter w)
{
    if (map.IsEmpty) return;
    var v = map.Validate();
    w.WriteLine("═══ opcode 映射校验 ═══");
    w.WriteLine($"  规范名 {v.CanonicalCount} / 已映射 {v.MappedCount}  →  {(v.IsComplete ? "✔ 完整双射" : "⚠ 见下方问题")}");
    if (v.Missing.Count > 0) w.WriteLine($"  缺失 {v.Missing.Count} 个: {Preview(v.Missing)}");
    if (v.Unknown.Count > 0) w.WriteLine($"  未知名 {v.Unknown.Count} 个: {Preview(v.Unknown)}");
    foreach (var c in v.ValueConflicts) w.WriteLine($"  冲突: {c}");
    foreach (var c in v.NameConflicts) w.WriteLine($"  冲突: {c}");
    w.WriteLine();
}

static string Preview(List<string> xs) => xs.Count <= 12 ? string.Join(", ", xs) : string.Join(", ", xs.Take(12)) + $" …(+{xs.Count - 12})";

static int RunInfo(FileInfo patchFile, DirectoryInfo? dummyDll, FileInfo? dump, FileInfo? opcodeMapFile, bool json, FileInfo? output)
{
    TextWriter outWriter = output != null ? new StreamWriter(output.FullName, false, new System.Text.UTF8Encoding(false)) : Console.Out;
    TextWriter err = Console.Error;
    try
    {
        if (!patchFile.Exists) { err.WriteLine($"error: 找不到补丁文件: {patchFile.FullName}"); return 2; }

        var types = CreateMetadataSource(dummyDll, dump, err);
        if (dummyDll != null && types == null) return 2;
        if (dump != null && types == null) return 2;

        TextWriter metaWriter = json ? err : outWriter;
        if (types != null)
        {
            metaWriter.WriteLine($"元数据源: {types.Describe()}{(types.SupportsTypedReferences ? "  [可转译]" : "  [仅解析/反汇编]")}");
            if (types is DummyDllSource dd && dd.Warnings.Count > 0)
                foreach (var w in dd.Warnings) metaWriter.WriteLine($"  ⚠ {w}");
            metaWriter.WriteLine();
        }

        var map = ResolveOpcodeMap(opcodeMapFile, types, err, out bool ok);
        if (!ok) return 2;
        if (!json) PrintOpCodeValidation(map, outWriter); else PrintOpCodeValidation(map, err);

        PatchFile p;
        try { p = PatchReader.ReadFile(patchFile.FullName, types); }
        catch (EndOfStreamException) { err.WriteLine("error: 读到文件尾之外——可能不是裸补丁或匿名类接口槽长度不符"); return 1; }
        catch (Exception e) { err.WriteLine($"error: 解析失败: {e.Message}"); return 1; }

        if (json)
        {
            var payload = BuildInfoJson(p, map, types);
            var jsonText = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            outWriter.WriteLine(jsonText);
        }
        else
        {
            PrintSummary(p, map, types, outWriter);
        }
        return 0;
    }
    finally
    {
        if (output != null) outWriter.Dispose();
    }
}

static int RunDisasm(FileInfo patchFile, DirectoryInfo? dummyDll, FileInfo? dump, FileInfo? opcodeMapFile, FileInfo? output)
{
    TextWriter err = Console.Error;
    if (!patchFile.Exists) { err.WriteLine($"error: 找不到补丁文件: {patchFile.FullName}"); return 2; }
    var types = CreateMetadataSource(dummyDll, dump, err);
    if (dummyDll != null && types == null) return 2;
    if (dump != null && types == null) return 2;
    var map = ResolveOpcodeMap(opcodeMapFile, types, err, out bool ok);
    if (!ok) return 2;
    PatchFile p;
    try { p = PatchReader.ReadFile(patchFile.FullName, types); }
    catch (EndOfStreamException) { err.WriteLine("error: 读到文件尾之外——可能不是裸补丁或匿名类接口槽长度不符"); return 1; }
    catch (Exception e) { err.WriteLine($"error: 解析失败: {e.Message}"); return 1; }
    if (!p.FullyParsed) err.WriteLine($"warn: 未完整解析到尾: {p.ParseNote}");
    else if (p.TrailingBytes != 0) err.WriteLine($"warn: 剩余 {p.TrailingBytes} 字节未消费——布局错位");
    if (!map.IsEmpty)
    {
        var v = map.Validate();
        if (!v.IsComplete) err.WriteLine($"warn: opcode 映射不完整: 缺失 {v.Missing.Count} 未知名 {v.Unknown.Count}");
    }
    var il = PatchTextAssembly.Disassemble(p, map);
    if (output != null)
    {
        try { File.WriteAllText(output.FullName, il); }
        catch(Exception e){ err.WriteLine($"error: 写出失败: {e.Message}"); return 1; }
        Console.WriteLine($"disasm {p.Methods.Count(m=>!m.IsEmpty)} methods -> {output.FullName}");
    }
    else
    {
        Console.Out.Write(il);
    }
    return 0;
}

static int RunDecompile(FileInfo patchFile, DirectoryInfo dummyDll, FileInfo? opcodeMapFile, FileInfo output)
{
    TextWriter err = Console.Error;
    if (!patchFile.Exists) { err.WriteLine($"error: 找不到补丁文件: {patchFile.FullName}"); return 2; }
    if (!dummyDll.Exists) { err.WriteLine($"error: DummyDll 目录不存在: {dummyDll.FullName}"); return 2; }

    IMetadataSource? types;
    try { types = new DummyDllSource(dummyDll.FullName); }
    catch (Exception e) { err.WriteLine($"error: 建立元数据源失败: {e.Message}"); return 2; }

    Console.WriteLine($"元数据源: {types.Describe()}  [可转译]");
    if (types is DummyDllSource dd && dd.Warnings.Count > 0)
        foreach (var w in dd.Warnings) Console.WriteLine($"  ⚠ {w}");

    var map = ResolveOpcodeMap(opcodeMapFile, types, err, out bool ok);
    if (!ok) return 2;
    if (map.IsEmpty) { err.WriteLine("error: --decompile 需要 opcode 映射（从 DummyDll 自动提取失败可用 --opcode-map 覆盖）"); return 2; }
    PrintOpCodeValidation(map, Console.Out);

    PatchFile p;
    try { p = PatchReader.ReadFile(patchFile.FullName, types); }
    catch (EndOfStreamException) { err.WriteLine("error: 读到文件尾之外——可能不是裸补丁或匿名类接口槽长度不符"); return 1; }
    catch (Exception e) { err.WriteLine($"error: 解析失败: {e.Message}"); return 1; }

    PrintSummary(p, map, types, Console.Out);

    if (types is not DummyDllSource dds) { err.WriteLine("error: --decompile 需要 --dummy-dll"); return 2; }

    var rep = PatchDecompiler.Run(p, map, dds);
    Console.WriteLine("\n═══ IL 转译 + 反编译 ═══");
    Console.WriteLine($"  修复列表方法: {rep.FixTranspiled}/{rep.FixMethods} 转译成功");
    Console.WriteLine($"  内部方法    : {rep.InternalTranspiled}/{rep.InternalMethods} 转译成功");
    Console.WriteLine($"  有降级点的方法: {rep.MethodsWithProblems}");
    if (rep.Problems.Count > 0)
    {
        var byKind = rep.Problems.Select(x => x.Contains(": ") ? x[..x.IndexOf(": ")] : x).GroupBy(k => k).OrderByDescending(g => g.Count());
        Console.WriteLine("  降级点分布:");
        foreach (var g in byKind) Console.WriteLine($"    {g.Count(),5}×  {g.Key}");
    }
    Console.WriteLine($"  内部桩签名推断: 参数 {rep.TypedParams}/{rep.TotalParams} 得到具体类型，返回 {rep.TypedRets}/{rep.TotalRets}");
    Console.WriteLine($"  孤立方法槽: {rep.OrphanMethods.Count} 个有方法体但无任何引用点" + (rep.OrphanMethods.Count > 0 ? $"  示例: {string.Join(", ", rep.OrphanMethods.Take(8).Select(i => $"method#{i}"))}" : ""));
    Console.WriteLine($"  外部方法解析: {rep.ExternResolved}/{rep.ExternTotal}" + (rep.ExternFailures.Count > 0 ? $"  未解析示例: {string.Join(", ", rep.ExternFailures.Take(5))}" : ""));
    if (rep.Storeys > 0)
        Console.WriteLine($"  闭包: 合成 {rep.Storeys} 个类型，绑定 {rep.BoundStoreyMethods} 个方法的 arg0，字段定型 {rep.TypedStoreyFields}/{rep.TotalStoreyFields}" + (rep.UntouchedStoreyFields > 0 ? $"（另有 {rep.UntouchedStoreyFields} 个字段无读写现场）" : ""));
    if (rep.Balance.Count > 0)
    {
        Console.WriteLine($"\n  ═══ 栈平衡自查 ═══");
        var byKind = rep.Balance.GroupBy(b => b.Kind).OrderByDescending(g => g.Count());
        foreach (var g in byKind) Console.WriteLine($"    {g.Count(),5}×  {g.Key}");
        Console.WriteLine($"    受影响方法: {rep.Balance.Select(b => b.Method).Distinct().Count()}");
        Console.WriteLine("\n  前 3 处失衡上下文:");
        foreach (var b in rep.Balance.Take(3))
        {
            Console.WriteLine($"    --- {b.Method} @IL_{b.Offset:x4}  [{b.Kind}] {b.Detail}");
            foreach (var c in b.Context) Console.WriteLine($"        {c}");
        }
    }
    if (rep.CSharp.Length > 0)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output.FullName)) ?? ".");
        File.WriteAllText(output.FullName, rep.CSharp);
        Console.WriteLine($"  C# 已写出: {output.FullName} ({rep.CSharp.Length} 字符)");
    }
    return 0;
}

static object BuildInfoJson(PatchFile p, OpCodeMap map, IMetadataSource? types)
{
    var dis = new Disassembler(p, map);
    int nonEmpty = p.Methods.Count(m => !m.IsEmpty);
    return new
    {
        magic = $"0x{p.Magic:X}",
        magicDec = p.Magic,
        interfaceBridge = p.InterfaceBridgeType,
        wrapperManager = p.WrapperManagerType,
        idMapSuffix = p.IdMapSuffix,
        counts = new
        {
            externTypes = p.ExternTypes.Count,
            methods = p.Methods.Count,
            nonEmptyMethods = nonEmpty,
            externMethods = p.ExternMethods.Count,
            internStrings = p.InternStrings.Count,
            fields = p.Fields.Count,
            vmStaticFields = p.StaticFields.Count,
            anonStoreys = p.AnonStoreys.Count,
            newClasses = p.NewClasses.Count,
            fixList = p.FixList.Count,
        },
        fullyParsed = p.FullyParsed,
        trailingBytes = p.TrailingBytes,
        parseNote = p.ParseNote,
        opcodeMap = map.IsEmpty ? null : new { count = map.Count, validation = map.Validate().IsComplete ? "complete" : "incomplete" },
        fixList = p.FixList.Select(fx => new { methodId = fx.MethodId, sig = dis.FormatSig(fx.Method), decl = p.TypeName(fx.Method.DeclaringTypeId) }),
        newClasses = p.NewClasses,
        anonStoreys = p.AnonStoreys.Select((a, i) => new
        {
            index = i, fieldTypes = a.FieldTypes, ctorId = a.CtorId, ctorParams = a.CtorParamNum,
            interfaces = a.Interfaces.Select(itf => new { type = p.TypeName(itf.TypeId), methodIds = itf.MethodIds }),
            vtableSize = a.VTable.Count, vtable = a.VTable
        }),
        externTypes = p.ExternTypes,
        internStrings = p.InternStrings.Select((s, i) => new { index = i, value = s }),
    };
}

static void PrintSummary(PatchFile p, OpCodeMap map, IMetadataSource? types, TextWriter w)
{
    var dis = new Disassembler(p, map);
    int nonEmpty = p.Methods.Count(m => !m.IsEmpty);

    w.WriteLine("═══ IFix 补丁概览 ═══");
    w.WriteLine($"  magic (header)     : 0x{p.Magic:X}  ({p.Magic})");
    w.WriteLine($"  接口桥类型         : {Short(p.InterfaceBridgeType)}");
    w.WriteLine($"  Wrapper 管理器     : {Short(p.WrapperManagerType)}");
    w.WriteLine($"  IDMAP 后缀         : {p.IdMapSuffix}");
    w.WriteLine($"  外部类型           : {p.ExternTypes.Count}");
    w.WriteLine($"  方法槽 / 非空方法  : {p.Methods.Count} / {nonEmpty}");
    w.WriteLine($"  外部方法           : {p.ExternMethods.Count}");
    w.WriteLine($"  字符串常量         : {p.InternStrings.Count}");
    w.WriteLine($"  字段 / VM静态字段  : {p.Fields.Count} / {p.StaticFields.Count}");
    w.WriteLine($"  匿名类/闭包        : {p.AnonStoreys.Count}");
    w.WriteLine($"  新增类             : {p.NewClasses.Count}");
    w.WriteLine($"  opcode 映射        : {(map.IsEmpty ? "未提供（只显示数字）" : $"{map.Count} 项")}");
    w.WriteLine($"  完整解析到尾       : {(p.FullyParsed ? "是" : "否")}" + $"{(p.FullyParsed ? $"（剩余 {p.TrailingBytes} 字节{(p.TrailingBytes == 0 ? " ✔ 对齐无误" : " ⚠ 错位")}）" : "")}");
    if (p.ParseNote != null) w.WriteLine($"  说明               : {p.ParseNote}");

    w.WriteLine("\n═══ 修复列表（补丁替换了哪些原始方法） ═══");
    if (p.FixList.Count == 0) w.WriteLine("  （空）");
    foreach (var fx in p.FixList)
        w.WriteLine($"  method#{fx.MethodId,-4} ← {dis.FormatSig(fx.Method)}");

    if (p.NewClasses.Count > 0)
    {
        w.WriteLine("\n═══ 新增类 ═══");
        foreach (var c in p.NewClasses) w.WriteLine($"  {Short(c)}");
    }

    if (p.AnonStoreys.Count > 0)
    {
        w.WriteLine("\n═══ 匿名类/闭包（storeyField[k] 的寻址依据） ═══");
        for (int i = 0; i < p.AnonStoreys.Count; i++)
        {
            var a = p.AnonStoreys[i];
            var fields = a.FieldTypes.Select((enc, k) => $"f{k}:{FieldEnc(p, enc)}");
            w.WriteLine($"  storey#{i,-2} 字段 {a.FieldTypes.Count}  ctor=method#{a.CtorId}({a.CtorParamNum} 参)  [{string.Join(", ", fields)}]");
            foreach (var itf in a.Interfaces)
                w.WriteLine($"            实现 {DumpCsIndex.NormalizeAqn(p.TypeName(itf.TypeId))} → [{string.Join(", ", itf.MethodIds.Select(m => $"method#{m}"))}]");
            var vt = a.VTable.Select((m, s) => (Slot: s, M: m)).Where(x => x.M >= 0).Select(x => $"[{x.Slot}]=method#{x.M}").ToList();
            if (a.VTable.Count > 0)
                w.WriteLine($"            vtable {a.VTable.Count} 槽" + (vt.Count > 0 ? $"，已 override {vt.Count}: {string.Join(", ", vt)}" : "，无 override"));
        }
    }

    var uses = map.IsEmpty ? null : dis.CountStringUses();
    w.WriteLine($"\n═══ 字符串常量（Ldstr 的 str#N 对照） ═══");
    if (uses == null)
    {
        w.WriteLine($"  {p.InternStrings.Count} 条（未提供 opcode 映射，认不出 Ldstr，故不统计用量）");
    }
    else
    {
        int refd = uses.Count(u => u > 0);
        w.WriteLine($"  {p.InternStrings.Count} 条，被引用 {uses.Sum()} 次" + (refd < p.InternStrings.Count ? $"；{p.InternStrings.Count - refd} 条未被任何 Ldstr 引用" : ""));
    }
    for (int i = 0; i < p.InternStrings.Count; i++)
        w.WriteLine($"  str#{i,-3} {(uses == null ? "   " : uses[i] == 0 ? "  -" : $"×{uses[i],3}")}  \"{Disassembler.Escape(p.InternStrings[i])}\"");

    static string FieldEnc(PatchFile p, int enc) => enc switch
    {
        0 => "基元",
        -2 => "引用",
        _ => enc > 0 ? Short(p.TypeName(enc - 1)) : $"?{enc}",
    };

    if (types != null)
    {
        int ok = 0, ambig = 0, miss = 0;
        var missing = new List<string>();
        void Probe(int declTypeId, string name, int paramCount)
        {
            var aqn = declTypeId >= 0 && declTypeId < p.ExternTypes.Count ? p.ExternTypes[declTypeId] : null;
            bool am = false;
            if (aqn != null && types!.TryGetReturnType(aqn, name, paramCount, out _, out am)) ok++;
            else if (am) ambig++;
            else { miss++; if (missing.Count < 8) missing.Add($"{(aqn == null ? "?" : DumpCsIndex.NormalizeAqn(aqn))}::{name}/{paramCount}"); }
        }
        foreach (var em in p.ExternMethods) Probe(em.DeclaringTypeId, em.Name, em.Params.Count);
        foreach (var fx in p.FixList) Probe(fx.Method.DeclaringTypeId, fx.Method.Name, fx.Method.Params.Count);
        int total = p.ExternMethods.Count + p.FixList.Count;
        w.WriteLine("\n═══ 返回类型解析率（预检，只按「名字 + 参数个数」探测） ═══");
        w.WriteLine($"  待解析 {total}：确定 {ok}，重载歧义 {ambig}，未找到 {miss}  →  {(total == 0 ? 0 : 100.0 * ok / total):F1}%");
        if (ambig > 0) w.WriteLine("  歧义仅指本探针的口径：转译会按补丁存的参数类型选重载，实际解析率见下方转译报告。");
        if (missing.Count > 0) w.WriteLine($"  未找到示例: {string.Join(", ", missing)}");
    }
}

Command BuildExportCommand()
{
    var patchArg = new Argument<FileInfo>("patch", "补丁文件路径 (*.patch.bytes)") { Arity = ArgumentArity.ExactlyOne };
    patchArg.HelpName = "PATCH";
    var dummyDllOpt = new Option<DirectoryInfo?>("--dummy-dll", "Il2CppDumper DummyDll 目录（首选）");
    var dumpOpt = new Option<FileInfo?>("--dump", "Il2CppDumper dump.cs 单文件");
    var opcodeOpt = new Option<FileInfo?>("--opcode-map", "单独覆盖 Code 枚举映射");
    var outputOpt = new Option<FileInfo>("--output", "输出 IR json 路径") { IsRequired = true };
    outputOpt.AddAlias("-o");
    var cmd = new Command("export", "导出为规范 IR patch.ir.json（内嵌 magic 与 opcodeMap，可强校验、往返无损）") { patchArg, outputOpt, opcodeOpt, dummyDllOpt, dumpOpt };
    cmd.SetHandler((FileInfo patch, FileInfo output, FileInfo? opcodeMap, DirectoryInfo? dummyDll, FileInfo? dump) => { exitCode = RunExport(patch, output, opcodeMap, dummyDll, dump); }, patchArg, outputOpt, opcodeOpt, dummyDllOpt, dumpOpt);
    return cmd;
}

static int RunExport(FileInfo patchFile, FileInfo output, FileInfo? opcodeMapFile, DirectoryInfo? dummyDll, FileInfo? dump)
{
    TextWriter err = Console.Error;
    if (!patchFile.Exists) { err.WriteLine($"error: 找不到补丁文件: {patchFile.FullName}"); return 2; }
    var types = CreateMetadataSource(dummyDll, dump, err);
    if (dummyDll != null && types == null) return 2;
    if (dump != null && types == null) return 2;
    var map = ResolveOpcodeMap(opcodeMapFile, types, err, out bool ok);
    if (!ok) return 2;
    PatchFile p;
    try { p = PatchReader.ReadFile(patchFile.FullName, types); }
    catch (EndOfStreamException) { err.WriteLine("error: 读到文件尾之外——可能不是裸补丁或匿名类接口槽长度不符"); return 1; }
    catch (Exception e) { err.WriteLine($"error: 解析失败: {e.Message}"); return 1; }
    var doc = PatchDocument.FromPatchFile(p, map);
    var errs = doc.Validate();
    if (errs.Count>0) foreach(var e in errs) err.WriteLine($"warn: {e}");
    try { doc.SaveJson(output.FullName); }
    catch(Exception e){ err.WriteLine($"error: 写出失败: {e.Message}"); return 1; }
    Console.WriteLine($"exported {p.Methods.Count} methods, {p.ExternTypes.Count} extern types -> {output.FullName}");
    if (!p.FullyParsed) err.WriteLine($"warn: 未完整解析到尾: {p.ParseNote}");
    else if (p.TrailingBytes!=0) err.WriteLine($"warn: 剩余 {p.TrailingBytes} 字节未消费——布局错位");
    return 0;
}

static string Short(string aqn)
{
    int comma = aqn.IndexOf(',');
    return comma > 0 ? aqn[..comma] : aqn;
}

partial class Program
{
    [System.Text.RegularExpressions.GeneratedRegex(@"[#\[]\d+\]?")]
    private static partial System.Text.RegularExpressions.Regex MyRegex();
}
