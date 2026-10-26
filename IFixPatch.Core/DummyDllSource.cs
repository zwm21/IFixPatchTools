using Mono.Cecil;

namespace IFixPatch.Core;

/// <summary>
/// 从 Il2CppDumper 的 DummyDll 目录读元数据。相对 dump.cs 的决定性优势是类型带完整身份
/// （命名空间 + 程序集），dump.cs 的签名只有短名且大量重名，无法可靠消歧。
///
/// 针对「文件集」的稳健性处理：
///   · 按需加载：AQN 自带程序集名，只打开用到的那个（Assembly-CSharp.dll 有 56MB，不预加载全部）。
///   · 依赖解析：DefaultAssemblyResolver + 目录搜索路径；解析不到不抛，降级并记录。
///   · 缺文件容错：IFix.Core.dll 缺失只影响 opcode 映射，其余功能照常。
/// </summary>
public sealed class DummyDllSource : IMetadataSource, IDisposable
{
    private readonly string _dir;
    private readonly DefaultAssemblyResolver _resolver;
    private readonly Dictionary<string, AssemblyDefinition?> _byAssemblyName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TypeDefinition?> _typeCache = [];
    private readonly List<string> _warnings = [];

    public IReadOnlyList<string> Warnings => _warnings;
    public bool SupportsTypedReferences => true;

    /// <summary>合成程序集需要用同一个解析器，否则 ImportReference/Resolve 找不到 DummyDll 里的程序集。</summary>
    public IAssemblyResolver Resolver => _resolver;

    /// <summary>DummyDll 目录；反编译器的解析器也必须搜索它，否则类型全部退化为 Unknown。</summary>
    public string DllDirectory => _dir;

    public DummyDllSource(string dir)
    {
        _dir = dir;
        _resolver = new DefaultAssemblyResolver();
        _resolver.AddSearchDirectory(dir);
    }

    public string Describe()
    {
        int loaded = _byAssemblyName.Values.Count(a => a != null);
        int available = Directory.Exists(_dir) ? Directory.GetFiles(_dir, "*.dll").Length : 0;
        return $"DummyDll ({available} 个程序集可用，已按需加载 {loaded})";
    }

    private AssemblyDefinition? Load(string assemblyShortName)
    {
        if (_byAssemblyName.TryGetValue(assemblyShortName, out var cached)) return cached;

        var path = Path.Combine(_dir, assemblyShortName + ".dll");
        AssemblyDefinition? asm = null;
        if (File.Exists(path))
        {
            try
            {
                asm = AssemblyDefinition.ReadAssembly(path,
                    new ReaderParameters { AssemblyResolver = _resolver, ReadingMode = ReadingMode.Deferred });
            }
            catch (Exception e) { _warnings.Add($"读取 {assemblyShortName}.dll 失败: {e.Message}"); }
        }
        else _warnings.Add($"DummyDll 里缺少 {assemblyShortName}.dll");

        _byAssemblyName[assemblyShortName] = asm;
        return asm;
    }

    /// <summary>由 AQN 定位 TypeDefinition。AQN 自带程序集名，故先精确定位再回退全目录扫描。</summary>
    private TypeDefinition? ResolveType(string aqn)
    {
        if (_typeCache.TryGetValue(aqn, out var cached)) return cached;

        var (typeName, asmName) = SplitAqn(aqn);
        // Cecil 的嵌套类型全名用 '/'，AQN 用 '+'
        var cecilName = typeName.Replace('+', '/');

        TypeDefinition? found = null;
        if (asmName != null)
        {
            var asm = Load(asmName);
            found = asm?.MainModule.GetType(cecilName);
        }
        if (found == null)
        {
            // 回退：类型可能被转发到别的程序集（type forwarding），扫已加载的
            foreach (var asm in _byAssemblyName.Values)
            {
                if (asm == null) continue;
                found = asm.MainModule.GetType(cecilName);
                if (found != null) break;
            }
        }
        _typeCache[aqn] = found;
        return found;
    }

    /// <summary>拆 AQN：顶层第一个逗号前是类型名，其后第一段是程序集短名；并剥掉泛型实参。</summary>
    public static (string TypeName, string? AssemblyName) SplitAqn(string aqn)
    {
        int depth = 0, cut = -1;
        for (int i = 0; i < aqn.Length; i++)
        {
            char c = aqn[i];
            if (c == '[') depth++;
            else if (c == ']') depth--;
            else if (c == ',' && depth == 0) { cut = i; break; }
        }
        var typePart = (cut < 0 ? aqn : aqn[..cut]).Trim();
        string? asmName = null;
        if (cut >= 0)
        {
            var rest = aqn[(cut + 1)..].Trim();
            int comma = rest.IndexOf(',');
            asmName = (comma < 0 ? rest : rest[..comma]).Trim();
        }

        // 泛型实例 `Foo`1[[...]]` → `Foo`1`（保留反引号元数，Cecil 全名同样带它）
        int br = typePart.IndexOf('[');
        if (br >= 0) typePart = typePart[..br];

        return (typePart.Trim(), string.IsNullOrEmpty(asmName) ? null : asmName);
    }

    public OpCodeMap? TryGetOpCodeMap()
    {
        var t = ResolveType("IFix.Core.Code, IFix.Core");
        if (t == null)
        {
            _warnings.Add("DummyDll 里找不到 IFix.Core.Code，无法取 opcode 映射（可改用 --opcodes）");
            return null;
        }
        var pairs = t.Fields
            .Where(f => f.IsLiteral && f.HasConstant && f.Constant != null)
            .Select(f => (Convert.ToInt32(f.Constant), f.Name));
        var map = OpCodeMap.FromPairs(pairs);
        return map.IsEmpty ? null : map;
    }

    /// <summary>对应 readSlotInfo 的 GetMethods(DeclaredOnly|Public|Instance)。</summary>
    public bool TryGetDeclaredInstanceMethodCount(string assemblyQualifiedName, out int count)
    {
        count = 0;
        var list = FindDeclaredInstanceMethods(assemblyQualifiedName);
        if (list == null) return false;
        count = list.Count;
        return true;
    }

    /// <summary>
    /// 已声明的 public 实例方法，顺序与 TryGetDeclaredInstanceMethodCount 的计数一致。
    /// 匿名类接口槽表里第 k 个 methodId 实现的就是这里第 k 个方法，故顺序必须同源。
    /// </summary>
    public List<MethodDefinition>? FindDeclaredInstanceMethods(string assemblyQualifiedName)
    {
        var t = ResolveType(assemblyQualifiedName);
        return t?.Methods.Where(m => m.IsPublic && !m.IsStatic).ToList();
    }

    public bool TryGetReturnType(string declaringTypeAqn, string methodName, int paramCount,
        out string returnType, out bool ambiguous)
    {
        returnType = ""; ambiguous = false;
        var t = ResolveType(declaringTypeAqn);
        if (t == null) return false;

        var cands = t.Methods.Where(m => m.Name == methodName && m.Parameters.Count == paramCount).ToList();
        if (cands.Count == 0) return false;

        var distinct = cands.Select(m => m.ReturnType.FullName).Distinct().ToList();
        returnType = distinct[0];
        ambiguous = distinct.Count > 1;
        return !ambiguous;
    }

    /// <summary>供后续 IL 转译使用：拿到真正的 Cecil 方法定义（带完整类型身份）。</summary>
    public MethodDefinition? FindMethod(string declaringTypeAqn, string methodName, int paramCount)
        => ResolveType(declaringTypeAqn)?.Methods
            .FirstOrDefault(m => m.Name == methodName && m.Parameters.Count == paramCount);

    /// <summary>
    /// 按参数类型精确选择重载。补丁存了每个参数的类型（ExternMethodSig.Params[].TypeId），
    /// 只按名字 + 参数个数匹配会选错重载——例如 List&lt;T&gt; 的 .ctor(int capacity) 与
    /// .ctor(IEnumerable&lt;T&gt;) 都是一个参数。expected 里的 null 表示该位不参与比较。
    /// 找不到精确匹配时回退到按个数匹配，保持原有行为。
    /// </summary>
    public MethodDefinition? FindMethodByParamTypes(string declaringTypeAqn, string methodName,
        IReadOnlyList<string?> expected, Func<TypeReference, string> render)
    {
        var t = ResolveType(declaringTypeAqn);
        if (t == null) return null;
        var cands = t.Methods.Where(m => m.Name == methodName && m.Parameters.Count == expected.Count).ToList();
        if (cands.Count <= 1) return cands.FirstOrDefault();

        foreach (var c in cands)
        {
            bool ok = true;
            for (int i = 0; i < expected.Count && ok; i++)
            {
                if (expected[i] == null) continue;
                if (render(c.Parameters[i].ParameterType) != expected[i]) ok = false;
            }
            if (ok) return c;
        }
        return cands[0];
    }

    /// <summary>IL 转译用：AQN → Cecil 类型定义。</summary>
    public TypeDefinition? FindType(string aqn) => ResolveType(aqn);

    /// <summary>
    /// 按 Cecil 全名（嵌套用 '/'，泛型定义保留 `N）定位类型定义。
    /// 供 AqnResolver 用：类型名语法已由 TypeName.Parse 拆好，这里只做查找。
    /// </summary>
    public TypeDefinition? FindTypeByCecilName(string cecilFullName, string? assemblyShortName)
    {
        if (assemblyShortName != null)
        {
            var td = Load(assemblyShortName)?.MainModule.GetType(cecilFullName);
            if (td != null) return td;
        }
        foreach (var asm in _byAssemblyName.Values)
        {
            var td = asm?.MainModule.GetType(cecilFullName);
            if (td != null) return td;
        }
        return null;
    }

    /// <summary>IL 转译用：按名字找字段（补丁的字段表只有声明类型 + 名字）。</summary>
    public FieldDefinition? FindField(string declaringTypeAqn, string fieldName)
        => ResolveType(declaringTypeAqn)?.Fields.FirstOrDefault(f => f.Name == fieldName);

    public void Dispose()
    {
        foreach (var asm in _byAssemblyName.Values) asm?.Dispose();
        _byAssemblyName.Clear();
    }
}
