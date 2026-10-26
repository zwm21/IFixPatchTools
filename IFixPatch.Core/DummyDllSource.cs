using Mono.Cecil;

namespace IFixPatch.Core;

/// <summary>
/// 从 Il2CppDumper 的 DummyDll 目录读取带命名空间和程序集身份的元数据。
/// 按 AQN 按需加载程序集，并通过目录搜索解析依赖。
/// 缺失或无法读取的程序集记录为警告；依赖其类型的查询返回未解析结果。
/// </summary>
public sealed class DummyDllSource : IMetadataSource, IDisposable
{
    private readonly string _dir;
    private readonly MetadataResolver _resolver;
    private readonly List<string> _directories;
    private readonly Dictionary<string, AssemblyDefinition?> _byAssemblyName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TypeDefinition?> _typeCache = [];
    private readonly List<string> _warnings = [];

    public IReadOnlyList<string> Warnings => _warnings;
    public bool SupportsTypedReferences => true;

    /// <summary>合成程序集需要用同一个解析器，否则 ImportReference/Resolve 找不到 DummyDll 里的程序集。</summary>
    public IAssemblyResolver Resolver => _resolver;

    /// <summary>DummyDll 目录；反编译器的解析器也必须搜索它，否则类型全部退化为 Unknown。</summary>
    public string DllDirectory => _dir;
    public IReadOnlyList<string> DllDirectories => _directories;

    public DummyDllSource(string dir) : this([dir]) { }

    /// <summary>Explicit assemblies are indexed by metadata identity even when their file names differ.</summary>
    public DummyDllSource(IEnumerable<string> directories, IEnumerable<string>? explicitAssemblies = null)
    {
        _directories = directories.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).ToList();
        if (_directories.Count == 0) throw new ArgumentException("At least one metadata directory is required.");
        _dir = _directories[0];
        _resolver = new MetadataResolver();
        foreach (var directory in _directories) _resolver.AddSearchDirectory(directory);
        try
        {
            foreach (var path in (explicitAssemblies ?? []).Select(Path.GetFullPath).Distinct(StringComparer.Ordinal))
            {
                var assembly = _resolver.LoadFile(path);
                if (_byAssemblyName.TryGetValue(assembly.Name.Name, out var previous))
                    throw new InvalidDataException("Duplicate explicit metadata assembly: " + assembly.Name.Name);
                _byAssemblyName.Add(assembly.Name.Name, assembly);
            }
        }
        catch { _resolver.Dispose(); throw; }
    }

    private sealed class MetadataResolver : DefaultAssemblyResolver
    {
        public AssemblyDefinition LoadFile(string path)
        {
            var assembly = AssemblyDefinition.ReadAssembly(path,
                new ReaderParameters { AssemblyResolver = this, ReadingMode = ReadingMode.Deferred });
            RegisterAssembly(assembly);
            return assembly;
        }
    }

    public string Describe()
    {
        int loaded = _byAssemblyName.Values.Count(a => a != null);
        int available = _directories.Where(Directory.Exists).Sum(directory => Directory.GetFiles(directory, "*.dll").Length);
        return $"DummyDll ({available} 个程序集可用，已按需加载 {loaded})";
    }

    private AssemblyDefinition? Load(string assemblyShortName)
    {
        if (_byAssemblyName.TryGetValue(assemblyShortName, out var cached)) return cached;

        var path = _directories.Select(directory => Path.Combine(directory, assemblyShortName + ".dll")).FirstOrDefault(File.Exists);
        AssemblyDefinition? asm = null;
        if (path is not null)
        {
            try
            {
                asm = _resolver.LoadFile(path);
                if (!StringComparer.OrdinalIgnoreCase.Equals(asm.Name.Name, assemblyShortName))
                    throw new InvalidDataException("Metadata assembly identity differs from requested name: " + assemblyShortName);
            }
            catch (Exception e) { asm = null; _warnings.Add($"读取 {assemblyShortName}.dll 失败: {e.Message}"); }
        }
        else _warnings.Add($"DummyDll 里缺少 {assemblyShortName}.dll");

        _byAssemblyName[assemblyShortName] = asm;
        return asm;
    }

    /// <summary>限定 AQN 只查询指定程序集；未限定类型名查询已加载的程序集。</summary>
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
        else
        {
            // 未提供程序集限定名时，才允许在已加载的类型定义中查找。
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
            _warnings.Add("DummyDll 里找不到 IFix.Core.Code，无法取 opcode 映射（可改用 --opcode-map）");
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
        return t?.Methods.Where(m => m.IsPublic && !m.IsStatic && !m.IsConstructor).ToList();
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
    /// 已知参数类型没有匹配项时返回 null，避免静默选择签名不兼容的方法。
    /// </summary>
    public MethodDefinition? FindMethodByParamTypes(string declaringTypeAqn, string methodName,
        IReadOnlyList<string?> expected, Func<TypeReference, string> render)
        => FindMethodByParamTypes(declaringTypeAqn, methodName, expected, render, null);

    /// <summary>按参数类型和可选泛型元数选择方法；null 元数允许查询任意泛型元数。</summary>
    public MethodDefinition? FindMethodByParamTypes(string declaringTypeAqn, string methodName,
        IReadOnlyList<string?> expected, Func<TypeReference, string> render, int? genericParameterCount)
    {
        var t = ResolveType(declaringTypeAqn);
        if (t == null) return null;
        var cands = t.Methods.Where(m => m.Name == methodName && m.Parameters.Count == expected.Count &&
            (genericParameterCount is null || m.GenericParameters.Count == genericParameterCount)).ToList();

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
        return null;
    }

    /// <summary>IL 转译用：AQN → Cecil 类型定义。</summary>
    public TypeDefinition? FindType(string aqn) => ResolveType(aqn);

    /// <summary>
    /// 按 Cecil 全名（嵌套用 '/'，泛型定义保留 `N）定位类型定义。
    /// 供 AqnResolver 用：类型名语法已由 TypeName.Parse 拆好，这里只做查找。
    /// 显式程序集短名查找失败时返回 null；只有未限定查询才搜索其它已加载程序集。
    /// </summary>
    public TypeDefinition? FindTypeByCecilName(string cecilFullName, string? assemblyShortName)
    {
        if (assemblyShortName != null)
            return Load(assemblyShortName)?.MainModule.GetType(cecilFullName);
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
        _resolver.Dispose();
        _byAssemblyName.Clear();
    }
}
