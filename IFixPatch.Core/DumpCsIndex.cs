using System.Text.RegularExpressions;

namespace IFixPatch.Core;

/// <summary>
/// 从 Il2CppDumper 的 dump.cs 建立类型索引，目前用途：查某个接口“已声明 public 实例方法数”。
///
/// 为什么需要它：补丁里匿名类的接口槽表长度 = 该接口的方法数（PatchManager.readSlotInfo 用
/// GetMethods(DeclaredOnly|Public|Instance) 逐个读 int32）。没有这个数字就无法推进流。
///
/// 只需要“数量”不需要“顺序”，且泛型实例化不改变方法数，所以按“开放泛型定义 + 元数”匹配即可。
/// </summary>
public sealed partial class DumpCsIndex
{
    // key: "Namespace.TypeName" 或 "Namespace.TypeName`arity"
    private readonly Dictionary<string, int> _declaredInstanceMethodCount = [];

    // 同 key → 该类型声明的方法签名（用于补齐补丁未存储的返回类型）
    private readonly Dictionary<string, List<MethodSig>> _methods = [];

    public int TypeCount => _declaredInstanceMethodCount.Count;
    public int MethodCount => _methods.Values.Sum(v => v.Count);

    /// <summary>dump.cs 里渲染出的一条方法签名。类型名保持 dump.cs 的短名形态。</summary>
    public sealed class MethodSig
    {
        public string Name = "";
        public string ReturnType = "";
        public List<string> ParamTypes = [];
        public bool IsStatic;
    }

    [GeneratedRegex(@"^//\s*Namespace:\s*(?<ns>.*)$")]
    private static partial Regex NamespaceLine();

    // 类型声明行：`public interface IEnumerator<T> : IDisposable // TypeDefIndex: N`
    [GeneratedRegex(@"^(?<mods>(?:public|private|protected|internal|abstract|sealed|static|readonly|partial|unsafe)\s+)*" +
                    @"(?<kind>class|struct|interface|enum)\s+(?<name>[A-Za-z_][\w`+.]*)\s*(?<gen><[^>]*>)?")]
    private static partial Regex TypeDeclLine();

    // 方法行（含具体实现，dump.cs 渲染为 `... ) { }`），用于抽取签名。
    [GeneratedRegex(@"^(?:public|private|protected|internal)\b[^;{}]*\([^;{}]*\)\s*(?:;|\{\s*\})\s*$")]
    private static partial Regex AnyMethodLine();

    private static readonly HashSet<string> Modifiers =
    [
        "public", "private", "protected", "internal", "static", "virtual", "abstract",
        "override", "sealed", "extern", "unsafe", "new", "readonly", "async", "partial", "const",
    ];

    public static DumpCsIndex FromFile(string path)
    {
        var idx = new DumpCsIndex();
        string ns = "";
        string? curKey = null;
        string? curArityKey = null;
        int count = 0;
        bool inMethods = false;

        foreach (var raw in File.ReadLines(path))
        {
            // 顶层 `}` 收束当前类型块
            if (raw.Length > 0 && raw[0] == '}')
            {
                Flush(idx, curKey, curArityKey, count);
                curKey = null; curArityKey = null; inMethods = false;
                continue;
            }

            var nsm = NamespaceLine().Match(raw);
            if (nsm.Success) { ns = nsm.Groups["ns"].Value.Trim(); continue; }

            // 类型声明只出现在列首（dump.cs 把嵌套类型也平铺为顶层块）
            if (raw.Length > 0 && raw[0] != '\t' && raw[0] != ' ' && raw[0] != '{' && raw[0] != '/')
            {
                var tm = TypeDeclLine().Match(raw);
                if (tm.Success)
                {
                    Flush(idx, curKey, curArityKey, count);
                    var name = tm.Groups["name"].Value;
                    int arity = GenericArity(tm.Groups["gen"].Value);
                    curKey = MakeKey(ns, name);
                    // 元数限定键：Foo 与 Foo<T> 同命名空间时可区分。接口槽长度算错会导致
                    // 整个流错位，故这条路径优先用限定键。
                    curArityKey = arity > 0 ? $"{curKey}`{arity}" : null;
                    count = 0; inMethods = false;
                    continue;
                }
            }

            if (curKey == null) continue;

            var t = raw.TrimStart('\t', ' ');
            if (t.StartsWith("// Methods")) { inMethods = true; continue; }
            if (t.StartsWith("// Fields") || t.StartsWith("// Properties") || t.StartsWith("// Events"))
            { inMethods = false; continue; }

            if (!inMethods) continue;
            if (t.StartsWith('|') || t.StartsWith("/*") || t.StartsWith("//")) continue; // GenericInstMethod 注释块

            // 签名索引：包含具体方法（`) { }`），用于补齐返回类型
            if (AnyMethodLine().IsMatch(t))
            {
                var sig = ParseSignature(t);
                if (sig != null)
                {
                    // Match GetMethods(DeclaredOnly | Public | Instance), including concrete methods.
                    if (t.StartsWith("public ", StringComparison.Ordinal) && !sig.IsStatic && sig.Name is not (".ctor" or ".cctor")) count++;
                    if (!idx._methods.TryGetValue(curKey, out var list))
                        idx._methods[curKey] = list = [];
                    list.Add(sig);
                }
            }
        }
        Flush(idx, curKey, curArityKey, count);
        return idx;
    }

    /// <summary>把一行方法签名拆成 返回类型 / 名字 / 参数类型。类型名保持 dump.cs 短名形态。</summary>
    private static MethodSig? ParseSignature(string line)
    {
        var body = line.TrimEnd();
        if (body.EndsWith("{ }")) body = body[..^3].TrimEnd();
        else if (body.EndsWith(';')) body = body[..^1].TrimEnd();

        int open = FirstTopLevelParen(body);
        if (open < 0 || !body.EndsWith(')')) return null;

        var head = body[..open];
        var paramList = body[(open + 1)..^1];

        var tokens = SplitTopLevel(head, ' ');
        if (tokens.Count < 2) return null;

        var sig = new MethodSig { Name = tokens[^1] };
        var typeTokens = new List<string>();
        foreach (var tk in tokens[..^1])
        {
            if (Modifiers.Contains(tk)) { if (tk == "static") sig.IsStatic = true; continue; }
            typeTokens.Add(tk);
        }
        if (typeTokens.Count == 0) return null;              // 构造函数（无返回类型）
        sig.ReturnType = string.Join(" ", typeTokens);

        // 泛型方法名去掉 <T>
        int lt = sig.Name.IndexOf('<');
        if (lt > 0) sig.Name = sig.Name[..lt];

        foreach (var p in SplitTopLevel(paramList, ','))
        {
            var pt = p.Trim();
            if (pt.Length == 0) continue;
            int eq = pt.IndexOf('=');                        // 去掉默认值 `= False`
            if (eq > 0) pt = pt[..eq].TrimEnd();
            var pts = SplitTopLevel(pt, ' ');
            if (pts.Count >= 2) pts.RemoveAt(pts.Count - 1); // 去掉参数名
            sig.ParamTypes.Add(string.Join(" ", pts));
        }
        return sig;
    }

    /// <summary>找参数列表起始的 '('，跳过 &lt;&gt; / [] 内部。</summary>
    private static int FirstTopLevelParen(string s)
    {
        int ang = 0, sq = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '<') ang++;
            else if (c == '>') ang--;
            else if (c == '[') sq++;
            else if (c == ']') sq--;
            else if (c == '(' && ang == 0 && sq == 0) return i;
        }
        return -1;
    }

    /// <summary>按分隔符切分，忽略 &lt;&gt; / [] / () 内部。</summary>
    private static List<string> SplitTopLevel(string s, char sep)
    {
        var parts = new List<string>();
        int ang = 0, sq = 0, par = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '<') ang++;
            else if (c == '>') ang--;
            else if (c == '[') sq++;
            else if (c == ']') sq--;
            else if (c == '(') par++;
            else if (c == ')') par--;
            else if (c == sep && ang == 0 && sq == 0 && par == 0)
            {
                var seg = s[start..i].Trim();
                if (seg.Length > 0) parts.Add(seg);
                start = i + 1;
            }
        }
        var last = s[start..].Trim();
        if (last.Length > 0) parts.Add(last);
        return parts;
    }

    /// <summary>
    /// 查外部方法的返回类型。按 (声明类型, 名字, 参数个数) 定位；
    /// 同名同参数个数的重载若返回类型一致仍可确定，否则视为不确定。
    /// </summary>
    public bool TryGetReturnType(string declaringTypeAqn, string methodName, int paramCount,
        out string returnType, out bool ambiguous)
    {
        returnType = ""; ambiguous = false;
        List<MethodSig>? list = null;
        foreach (var key in LookupKeys(declaringTypeAqn))
            if (_methods.TryGetValue(key, out list)) break;
        if (list == null) return false;

        var cands = list.Where(m => m.Name == methodName && m.ParamTypes.Count == paramCount).ToList();
        if (cands.Count == 0) return false;

        var distinct = cands.Select(c => c.ReturnType).Distinct().ToList();
        returnType = distinct[0];
        ambiguous = distinct.Count > 1;
        return !ambiguous;
    }

    /// <summary>落盘一个类型块的实例方法数：限定键精确、无限定键为兼容回退（首次写入优先）。</summary>
    private static void Flush(DumpCsIndex idx, string? key, string? arityKey, int count)
    {
        if (key == null) return;
        if (arityKey != null) idx._declaredInstanceMethodCount[arityKey] = count;
        if (!idx._declaredInstanceMethodCount.ContainsKey(key)) idx._declaredInstanceMethodCount[key] = count;
    }

    private static int GenericArity(string gen)
    {
        if (string.IsNullOrEmpty(gen)) return 0;
        int depth = 0, n = 1;
        foreach (var c in gen)
        {
            if (c == '<') depth++;
            else if (c == '>') depth--;
            else if (c == ',' && depth == 1) n++;
        }
        return n;
    }

    /// <summary>
    /// 索引 key：去掉全部泛型信息（`N 与 &lt;...&gt;）的点分类型名。
    /// dump.cs 对嵌套类型的渲染是「Namespace 为空 + 名字写作 Outer.Inner」，
    /// 所以嵌套类型天然落成不带命名空间的 "Outer.Inner"，查询侧用尾键匹配它。
    /// 丢弃元数可能让 Foo 与 Foo&lt;T&gt; 撞键，靠返回类型歧义检测兜底。
    /// </summary>
    private static string MakeKey(string ns, string name)
        => Norm(string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}");

    /// <summary>去掉 `N 与 &lt;...&gt;，得到纯点分名。</summary>
    internal static string Norm(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        int ang = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '<') { ang++; continue; }
            if (c == '>') { ang--; continue; }
            if (ang > 0) continue;
            if (c == '`') { while (i + 1 < s.Length && char.IsDigit(s[i + 1])) i++; continue; }
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    /// <summary>嵌套类型的尾键：A.B.Outer+Inner → Outer.Inner（对应 dump.cs 的渲染）。</summary>
    private static string? NestedTailKey(string typeName)
    {
        int plus = typeName.IndexOf('+');
        if (plus < 0) return null;
        var outer = typeName[..plus];
        int lastDot = outer.LastIndexOf('.');
        var outerSimple = lastDot >= 0 ? outer[(lastDot + 1)..] : outer;
        return Norm(outerSimple + typeName[plus..].Replace('+', '.'));
    }

    /// <summary>由 AQN 生成候选查询键（先全名，再嵌套尾键）。</summary>
    private static IEnumerable<string> LookupKeys(string aqn)
    {
        var t = NormalizeAqn(aqn);
        // AQN 的 `N 给出元数，优先用限定键匹配，避免 Foo 与 Foo<T> 撞键
        var arity = AqnArity(t);
        var plain = Norm(t.Replace('+', '.'));
        if (arity > 0) yield return $"{plain}`{arity}";
        yield return plain;
        var tail = NestedTailKey(t);
        if (tail != null)
        {
            if (arity > 0) yield return $"{tail}`{arity}";
            yield return tail;
        }
    }

    /// <summary>AQN 里所有 `N 的元数之和（嵌套泛型的元数分散在各层）。</summary>
    private static int AqnArity(string typeName)
    {
        int total = 0;
        for (int i = 0; i < typeName.Length; i++)
            if (typeName[i] == '`')
            {
                int n = 0;
                while (i + 1 < typeName.Length && char.IsDigit(typeName[i + 1])) { n = n * 10 + (typeName[++i] - '0'); }
                total += n;
            }
        return total;
    }

    /// <summary>由补丁 externTypes 里的 AQN 查方法数。</summary>
    public bool TryGetDeclaredInstanceMethodCount(string assemblyQualifiedName, out int count)
    {
        foreach (var key in LookupKeys(assemblyQualifiedName))
            if (_declaredInstanceMethodCount.TryGetValue(key, out count)) return true;
        count = 0;
        return false;
    }

    /// <summary>AQN → 索引 key：剥掉程序集限定与泛型实参，保留 `arity。</summary>
    public static string NormalizeAqn(string aqn)
    {
        // 1) 去掉顶层逗号之后的程序集部分
        int depth = 0, cut = aqn.Length;
        for (int i = 0; i < aqn.Length; i++)
        {
            char c = aqn[i];
            if (c == '[') depth++;
            else if (c == ']') depth--;
            else if (c == ',' && depth == 0) { cut = i; break; }
        }
        var t = aqn[..cut].Trim();

        // 2) 去掉泛型实参 `N[[...]] → `N
        int br = t.IndexOf('[');
        if (br >= 0) t = t[..br];

        return t.Trim();
    }
}
