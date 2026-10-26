namespace IFixPatch.Core;

/// <summary>
/// 单文件 dump.cs 作为元数据源。够用于「解析 + 反汇编」：接口方法数只需要一个整数，
/// 返回类型也能拿到渲染字符串。
///
/// 但它的签名只有短名、无命名空间，且真实数据里数百个简名重复，无法可靠消歧，
/// 所以不支持类型化引用 —— IL 转译需要 DummyDllSource。
/// </summary>
public sealed class DumpCsSource : IMetadataSource
{
    private readonly DumpCsIndex _index;
    private readonly string _path;
    private readonly OpCodeMap? _opcodes;

    public bool SupportsTypedReferences => false;

    public DumpCsSource(string path)
    {
        _path = path;
        _index = DumpCsIndex.FromFile(path);
        // dump.cs 同时是 opcode 映射的来源；提取不到不算致命（可用 --opcodes 显式给）
        try { _opcodes = OpCodeMap.FromFile(path); } catch { _opcodes = null; }
    }

    public string Describe()
        => $"dump.cs ({Path.GetFileName(_path)}: {_index.TypeCount} 类型 / {_index.MethodCount} 方法签名)";

    public OpCodeMap? TryGetOpCodeMap() => _opcodes;

    public bool TryGetDeclaredInstanceMethodCount(string aqn, out int count)
        => _index.TryGetDeclaredInstanceMethodCount(aqn, out count);

    public bool TryGetReturnType(string declaringTypeAqn, string methodName, int paramCount,
        out string returnType, out bool ambiguous)
        => _index.TryGetReturnType(declaringTypeAqn, methodName, paramCount, out returnType, out ambiguous);
}
