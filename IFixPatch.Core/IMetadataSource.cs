namespace IFixPatch.Core;

/// <summary>
/// 目标程序集元数据的来源。补丁本身不自描述，需要外部元数据补齐三样东西：
/// 被打乱的 Code 枚举、接口方法数（决定 slot 表长度）、外部方法返回类型。
///
/// 两个实现各自独立可用，不需要同时提供：
///   · DumpCsSource   —— 单文件 dump.cs。够用于「解析 + 反汇编」。
///   · DummyDllSource —— Il2CppDumper 的 DummyDll 目录。额外提供带命名空间的类型身份，
///                       是 IL 转译/反编译的必要条件（dump.cs 的签名只有短名，无法消歧）。
/// </summary>
public interface IMetadataSource
{
    /// <summary>数据来源描述，用于报告。</summary>
    string Describe();

    /// <summary>该源能否提供完整类型身份（带命名空间/程序集），即能否支撑 CIL 构造。</summary>
    bool SupportsTypedReferences { get; }

    /// <summary>取被 ShuffleInstruction 打乱的 Code 枚举映射；该源提供不了时返回 null。</summary>
    OpCodeMap? TryGetOpCodeMap();

    /// <summary>接口“已声明 public 实例方法数”，决定匿名类 slot 表长度。</summary>
    bool TryGetDeclaredInstanceMethodCount(string assemblyQualifiedName, out int count);

    /// <summary>
    /// 外部方法返回类型。补丁的外部签名不存返回类型，按 (声明类型, 名字, 参数个数) 查询；
    /// 同名同参数个数的重载返回类型不一致时置 ambiguous。
    /// </summary>
    bool TryGetReturnType(string declaringTypeAqn, string methodName, int paramCount,
        out string returnType, out bool ambiguous);
}
