using System.Globalization;
using System.Text.RegularExpressions;

namespace IFixPatch.Core;

/// <summary>校验报告：把解析出的映射和内置规范名字集对比的结果。</summary>
public sealed class OpCodeValidation
{
    public int CanonicalCount;              // 规范名总数（181）
    public int MappedCount;                 // 成功映射的项数
    public List<string> Unknown = [];    // 出现在输入、但不是规范名（拼错/串了别的枚举）
    public List<string> Missing = [];    // 规范名里没被映射覆盖（literal 被裁剪的信号）
    public List<string> ValueConflicts = []; // 同一整数值被赋给多个名字
    public List<string> NameConflicts = [];  // 同一名字出现在多个整数值

    public bool IsComplete => Missing.Count == 0 && Unknown.Count == 0
        && ValueConflicts.Count == 0 && NameConflicts.Count == 0;
}

/// <summary>
/// 逐游戏的 Code 枚举整数值 → 助记符 映射。
/// 这是被 ShuffleInstruction 打乱的那套编号，是让反汇编“有语义”的必需输入。
/// 来源：Il2CppDumper 的 dump.cs（含 `public const Code Nop = N;`），或 DummyDll，或手写映射文件。
/// </summary>
public sealed partial class OpCodeMap
{
    private readonly Dictionary<int, string> _byValue = [];

    public bool IsEmpty => _byValue.Count == 0;
    public int Count => _byValue.Count;

    public string Name(int code) => _byValue.TryGetValue(code, out var n) ? n : $"code#{code}";
    public bool TryName(int code, out string name) => _byValue.TryGetValue(code, out name!);

    // 规范名（固定 181 个，来自 IFix.Core.Code）。ShuffleInstruction 只置换“值”，名字恒定，
    // 所以这份清单可用来校验任何一份提取出的映射是否完整、是否串入了别的枚举。
    private static readonly string[] Canonical =
    [
        "Nop", "Break", "Ldarg", "Ldloc", "Stloc", "Ldarga", "Starg", "Ldloca",
        "Ldnull", "Ldc_I4", "Ldc_I8", "Ldc_R4", "Ldc_R8", "Dup", "Pop", "Jmp",
        "Call", "CallExtern", "Ret", "Br", "Brfalse", "Brtrue", "Beq", "Bge",
        "Bgt", "Ble", "Blt", "Bne_Un", "Bge_Un", "Bgt_Un", "Ble_Un", "Blt_Un",
        "Switch", "Ldind_I1", "Ldind_U1", "Ldind_I2", "Ldind_U2", "Ldind_I4", "Ldind_U4", "Ldind_I8",
        "Ldind_I", "Ldind_R4", "Ldind_R8", "Ldind_Ref", "Stind_Ref", "Stind_I1", "Stind_I2", "Stind_I4",
        "Stind_I8", "Stind_R4", "Stind_R8", "Add", "Sub", "Mul", "Div", "Div_Un",
        "Rem", "Rem_Un", "And", "Or", "Xor", "Shl", "Shr", "Shr_Un",
        "Neg", "Not", "Conv_I1", "Conv_I2", "Conv_I4", "Conv_I8", "Conv_R4", "Conv_R8",
        "Conv_U4", "Conv_U8", "Callvirt", "Callvirtvirt", "Ldvirtftn2", "Cpobj", "Ldobj", "Ldstr",
        "Newobj", "Castclass", "Isinst", "Conv_R_Un", "Unbox", "Throw", "Ldfld", "Ldflda",
        "Stfld", "Ldsfld", "Ldsflda", "Stsfld", "Stobj", "Conv_Ovf_I1_Un", "Conv_Ovf_I2_Un", "Conv_Ovf_I4_Un",
        "Conv_Ovf_I8_Un", "Conv_Ovf_U1_Un", "Conv_Ovf_U2_Un", "Conv_Ovf_U4_Un", "Conv_Ovf_U8_Un", "Conv_Ovf_I_Un", "Conv_Ovf_U_Un", "Box",
        "Newarr", "Ldlen", "Ldelema", "Ldelem_I1", "Ldelem_U1", "Ldelem_I2", "Ldelem_U2", "Ldelem_I4",
        "Ldelem_U4", "Ldelem_I8", "Ldelem_I", "Ldelem_R4", "Ldelem_R8", "Ldelem_Ref", "Stelem_I", "Stelem_I1",
        "Stelem_I2", "Stelem_I4", "Stelem_I8", "Stelem_R4", "Stelem_R8", "Stelem_Ref", "Ldelem_Any", "Stelem_Any",
        "Unbox_Any", "Conv_Ovf_I1", "Conv_Ovf_U1", "Conv_Ovf_I2", "Conv_Ovf_U2", "Conv_Ovf_I4", "Conv_Ovf_U4", "Conv_Ovf_I8",
        "Conv_Ovf_U8", "Refanyval", "Ckfinite", "Mkrefany", "Ldtoken", "Ldtype", "Conv_U2", "Conv_U1",
        "Conv_I", "Conv_Ovf_I", "Conv_Ovf_U", "Add_Ovf", "Add_Ovf_Un", "Mul_Ovf", "Mul_Ovf_Un", "Sub_Ovf",
        "Sub_Ovf_Un", "Endfinally", "Leave", "Stind_I", "Conv_U", "Arglist", "Ceq", "Cgt",
        "Cgt_Un", "Clt", "Clt_Un", "Ldftn", "Newanon", "Ldvirtftn", "Localloc", "Endfilter",
        "Unaligned", "Volatile", "Tail", "Initobj", "Constrained", "Cpblk", "Initblk", "No",
        "Rethrow", "Sizeof", "Refanytype", "Readonly", "StackSpace",
    ];
    private static readonly HashSet<string> CanonicalSet = [.. Canonical];

    // 值可为十进制或十六进制（不同 Il2CppDumper 版本）。
    [GeneratedRegex(@"public\s+const\s+Code\s+(?<name>\w+)\s*=\s*(?<val>-?0[xX][0-9a-fA-F]+|-?\d+)\s*;")]
    private static partial Regex ConstCodeLine();

    // 锚定到 IFix.Core.Code 枚举：`// Namespace: IFix.Core` 后面（容忍中间的特性/空行）跟 `public enum Code`。
    [GeneratedRegex(@"//\s*Namespace:\s*IFix\.Core\s*(?:\r?\n\s*(?:\[[^\]]*\][^\n]*)?)*?\r?\n\s*public\s+enum\s+Code\b",
        RegexOptions.None)]
    private static partial Regex IFixCoreCodeAnchor();

    [GeneratedRegex(@"^\s*(?<name>\w+)\s*[=:]?\s*(?<val>-?0[xX][0-9a-fA-F]+|-?\d+)\s*$")]
    private static partial Regex PlainLine();

    private static int ParseIntLiteral(string s) =>
        s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || s.StartsWith("-0x", StringComparison.OrdinalIgnoreCase)
            ? (int)long.Parse(s.Replace("0x", "").Replace("0X", ""), NumberStyles.HexNumber)
            : int.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>由 (值, 名字) 对构造，供 DummyDll 等结构化来源使用。</summary>
    public static OpCodeMap FromPairs(IEnumerable<(int Value, string Name)> pairs)
    {
        var map = new OpCodeMap();
        var nameToVal = new Dictionary<string, int>();
        foreach (var (v, n) in pairs) map.Add(n, v, nameToVal);
        return map;
    }

    /// <summary>从文件加载：.cs 走 dump.cs 枚举解析；其它按 `Name = value` / `Name value` 逐行。</summary>
    public static OpCodeMap FromFile(string path)
    {
        var text = File.ReadAllText(path);
        return path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            ? FromDumpCs(text, path)
            : FromPlain(text, path);
    }

    private static OpCodeMap FromDumpCs(string text, string path)
    {
        // 只在 IFix.Core.Code 枚举块内提取——避免别的命名空间同名 `Code` 枚举污染。
        string scope = ExtractIFixCoreCodeBlock(text)
            ?? throw new InvalidOperationException(
                $"在 {path} 里没定位到 `IFix.Core` 命名空间下的 `public enum Code`。" +
                "可能该 dump 不含 IFix.Core.Code，或格式与预期不符。");

        var map = new OpCodeMap();
        var nameToVal = new Dictionary<string, int>();
        foreach (Match mt in ConstCodeLine().Matches(scope))
            map.Add(mt.Groups["name"].Value, ParseIntLiteral(mt.Groups["val"].Value), nameToVal);

        if (map.IsEmpty)
            throw new InvalidOperationException(
                $"在 {path} 的 IFix.Core.Code 块里没找到 `public const Code <name> = <n>;`——" +
                "可能枚举 literal 被裁剪了（需转 native 恢复）。");
        return map;
    }

    /// <summary>定位 IFix.Core.Code 枚举，返回其 { … } 块内文本（枚举不嵌套花括号，取首个匹配 }）。</summary>
    private static string? ExtractIFixCoreCodeBlock(string text)
    {
        var anchor = IFixCoreCodeAnchor().Match(text);
        int enumPos;
        if (anchor.Success)
        {
            enumPos = anchor.Index;
        }
        else
        {
            // 回退：找任意 `public enum Code`，但要求其前 200 字符内出现 IFix.Core，否则放弃（不冒污染风险）。
            var decl = MyRegex().Match(text);
            if (!decl.Success) return null;
            int from = Math.Max(0, decl.Index - 200);
            if (!text.AsSpan(from, decl.Index - from).ToString().Contains("IFix.Core")) return null;
            enumPos = decl.Index;
        }

        int open = text.IndexOf('{', enumPos);
        if (open < 0) return null;
        int close = text.IndexOf('}', open);
        if (close < 0) return null;
        return text[open..close];
    }

    private static OpCodeMap FromPlain(string text, string path)
    {
        var map = new OpCodeMap();
        var nameToVal = new Dictionary<string, int>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//") || line.StartsWith('#')) continue;
            var mt = PlainLine().Match(line);
            if (mt.Success)
                map.Add(mt.Groups["name"].Value, ParseIntLiteral(mt.Groups["val"].Value), nameToVal);
        }
        if (map.IsEmpty)
            throw new InvalidOperationException($"{path} 未解析出任何 opcode 映射。");
        return map;
    }

    private readonly List<string> _valueConflicts = [];
    private readonly List<string> _nameConflicts = [];

    private void Add(string name, int value, Dictionary<string, int> nameToVal)
    {
        if (_byValue.TryGetValue(value, out var existing) && existing != name)
            _valueConflicts.Add($"值 {value} 同时映射到 '{existing}' 和 '{name}'");
        if (nameToVal.TryGetValue(name, out var prevVal) && prevVal != value)
            _nameConflicts.Add($"名字 '{name}' 同时出现在值 {prevVal} 和 {value}");
        _byValue[value] = name;
        nameToVal[name] = value;
    }


    public IEnumerable<KeyValuePair<int, string>> Enumerate() => _byValue;
    public void LoadFromNameToValue(Dictionary<string, int> nameToVal)
    {
        _byValue.Clear();
        _valueConflicts.Clear();
        _nameConflicts.Clear();
        var tmp = new Dictionary<string, int>();
        foreach (var kv in nameToVal) Add(kv.Key, kv.Value, tmp);
    }

    /// <summary>拿解析结果和内置规范名字集对比。</summary>
    public OpCodeValidation Validate()
    {
        var mappedNames = new HashSet<string>(_byValue.Values);
        var v = new OpCodeValidation
        {
            CanonicalCount = Canonical.Length,
            MappedCount = _byValue.Count,
            ValueConflicts = _valueConflicts,
            NameConflicts = _nameConflicts,
        };
        foreach (var n in mappedNames)
            if (!CanonicalSet.Contains(n)) v.Unknown.Add(n);
        foreach (var n in Canonical)
            if (!mappedNames.Contains(n)) v.Missing.Add(n);
        return v;
    }

    [GeneratedRegex(@"public\s+enum\s+Code\b")]
    private static partial Regex MyRegex();
}
