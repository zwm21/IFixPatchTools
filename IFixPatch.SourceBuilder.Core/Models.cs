using System.Text.Json;
using System.Text.Json.Serialization;

namespace IFixPatch.SourceBuilder.Core;

public sealed class SourceBuilderProfile
{
    public string Name { get; set; } = "";
    public string TargetFramework { get; set; } = "net471";
    public string AssemblyPath { get; set; } = "";
    public string IFixCoreAssemblyPath { get; set; } = "";
    public string? KnownPatchPath { get; set; }
    public List<string> ReferenceDirectories { get; set; } = [];
}

public sealed class PreparedProfile
{
    public string Name { get; set; } = "";
    public string TargetFramework { get; set; } = "net471";
    public string SourceProfilePath { get; set; } = "";
    public string SourceAssemblyPath { get; set; } = "";
    public string SourceAssemblySha256 { get; set; } = "";
    public string IFixCoreAssemblyPath { get; set; } = "";
    public string IFixCoreAssemblySha256 { get; set; } = "";
    public string AdapterCoreSourcePath { get; set; } = "";
    public string AdapterCoreSourceSha256 { get; set; } = "";
    public string CatalogPath { get; set; } = "";
    public string CatalogSha256 { get; set; } = "";
    public string FieldCatalogPath { get; set; } = "";
    public string FieldCatalogSha256 { get; set; } = "";
    public string? KnownPatchPath { get; set; }
    public string? KnownPatchSha256 { get; set; }
    public ulong? KnownPatchMagic { get; set; }
    public List<string> ReferenceDirectories { get; set; } = [];
    public string CleanAssemblyPath { get; set; } = "";
    public string CleanAssemblySha256 { get; set; } = "";
    public string CompileReferenceAssemblyPath { get; set; } = "";
    public string CompileReferenceAssemblySha256 { get; set; } = "";
}

public sealed class PatchManifest
{
    public string Name { get; set; } = "";
    public string AssemblyName { get; set; } = "PatchImplementation";
    public List<string> Sources { get; set; } = [];
    public List<PatchBinding> Patches { get; set; } = [];

    /// <summary>规模阈值。省略时用默认值，见 <see cref="PatchLimits"/>。</summary>
    public PatchLimits Limits { get; set; } = new();
}

/// <summary>
/// build 阶段的规模阈值。默认值按「已知可用规模之上」定，判据见 PatchSizeAnalyzer 的注释：
/// 求值栈那两条对着 VM 的 10240 定长共用栈，helper 数与指令数是经验线。
/// </summary>
public sealed class PatchLimits
{
    /// <summary>最深解释调用链占共用求值栈的百分比，达到即 warn。</summary>
    public int WarnEvaluationStackPercent { get; set; } = 25;

    /// <summary>同上，达到即 fail。留出的余量是给未被静态看见的 extern 回调再入的。</summary>
    public int FailEvaluationStackPercent { get; set; } = 80;

    /// <summary>单方法指令数 warn 线。</summary>
    public int WarnInstructionCount { get; set; } = 3000;

    /// <summary>helper 数的经验警告阈值，不是 VM 硬限额。</summary>
    public int WarnHelperCount { get; set; } = 128;
}

public sealed class PatchBinding
{
    public string Target { get; set; } = "";
    public string Implementation { get; set; } = "";
}

public sealed class PrepareReport
{
    public string Profile { get; set; } = "";
    public int TypeCount { get; set; }
    public int MethodCount { get; set; }
    public int IdMapFieldCount { get; set; }
    public int FieldCount { get; set; }
    public List<string> RemovedTypes { get; set; } = [];
    public int RemovedBaseProxyMethods { get; set; }
    public int StrippedCompilerGeneratedTypes { get; set; }
    public int StrippedCompilerGeneratedMethods { get; set; }
    public int StrippedCompilerGeneratedFields { get; set; }
    public int WidenedTypes { get; set; }
    public int WidenedMethods { get; set; }
    public int WidenedFields { get; set; }
    public int AdapterOpcodeCount { get; set; }
    public ulong AdapterMagic { get; set; }
}

public sealed class MethodCatalogEntry
{
    public string Signature { get; set; } = "";
    public int? IdTag { get; set; }
}

public sealed class IdMapCatalogEntry
{
    public string Field { get; set; } = "";
    public int Slot { get; set; }
}

public sealed class AssemblyCatalog
{
    public string AssemblyName { get; set; } = "";
    public List<MethodCatalogEntry> Methods { get; set; } = [];
    public List<IdMapCatalogEntry> IdMap { get; set; } = [];
}

/// <summary>
/// 目标程序集的字段表。写补丁时用来查「这个类型有哪些字段、叫什么、什么类型、能不能直接读」。
///
/// 单独成文件而不是并进 <see cref="AssemblyCatalog"/>：后者已经数十 MB，而 BodyGrafter 每次
/// build 都要把它整份反序列化，只为拿 IdMap。字段表并进去等于给每次 build 加一笔无谓开销。
/// 按类型分组而不是平铺，是为了不让声明类型名重复几十万遍。
/// </summary>
public sealed class FieldCatalog
{
    public string AssemblyName { get; set; } = "";
    public List<TypeFieldsEntry> Types { get; set; } = [];
}

public sealed class TypeFieldsEntry
{
    public string Type { get; set; } = "";
    public List<FieldCatalogEntry> Fields { get; set; } = [];
}

public sealed class FieldCatalogEntry
{
    public string Name { get; set; } = "";
    public string FieldType { get; set; } = "";

    /// <summary>C# 写法的可见性（private / internal / protected / public …）。</summary>
    public string Visibility { get; set; } = "";
    public bool IsStatic { get; set; }
    public bool IsInitOnly { get; set; }
    public bool IsLiteral { get; set; }
    public bool IsBackingField { get; set; }

    /// <summary>backing field 对应的属性名；非 backing field 为 null。</summary>
    public string? BackedProperty { get; set; }
}

/// <summary>一个补丁源文件及其内容哈希。</summary>
public sealed class SourceFileRecord
{
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
}

public sealed class GraftBindingReport
{
    public string Target { get; set; } = "";
    public string Implementation { get; set; } = "";
    public int IdTag { get; set; }
    public string NativeIdMapField { get; set; } = "";
    public int NativeSlot { get; set; }
    public int SourceInstructionCount { get; set; }
    public int ResultInstructionCount { get; set; }
    public int LocalCount { get; set; }
    public int MaxStackSize { get; set; }
    public int ExceptionHandlerCount { get; set; }
}

public sealed class BuildReport
{
    public string PreparedProfile { get; set; } = "";
    public string PreparedProfileSha256 { get; set; } = "";
    public string Manifest { get; set; } = "";
    public string ManifestSha256 { get; set; } = "";

    /// <summary>
    /// 参与本次编译的补丁源文件（通配符已展开），按喂给 csc 的顺序记录。
    /// <c>generate</c> 会重新核对哈希：manifest 本身没变、但源码改了却忘记重跑 <c>build</c> 时，
    /// 其余哈希全都对得上，只有这一段能发现产物已经过期。
    /// </summary>
    public List<SourceFileRecord> Sources { get; set; } = [];
    public string ImplementationAssembly { get; set; } = "";
    public string ImplementationAssemblySha256 { get; set; } = "";
    public string GraftedAssembly { get; set; } = "";
    public string GraftedAssemblySha256 { get; set; } = "";
    public string ProcessConfig { get; set; } = "";
    public string ProcessConfigSha256 { get; set; } = "";
    public List<GraftBindingReport> Bindings { get; set; } = [];

    /// <summary>本次 graft 搬运的补丁内部 helper。它们不占 fix list，也不需要 Native IDMAP slot。</summary>
    public List<GraftHelperReport> Helpers { get; set; } = [];

    /// <summary>
    /// 本次 graft 引用到的闭合泛型实例。<c>proven</c> 为 false 的需要人对着 dump 核一遍：
    /// 工具只能证明存在，证明不了不存在（见 GenericInstanceProbe）。
    /// </summary>
    public List<GenericInstanceReport> GenericInstances { get; set; } = [];

    /// <summary>规模度量与 VM 限额的对照。</summary>
    public PatchSizeReport Size { get; set; } = new();

    /// <summary>规模检查的结果。含 <c>fail</c> 时 build 以非零码退出。</summary>
    public List<BuildDiagnostic> Diagnostics { get; set; } = [];
}

/// <summary>规模度量。槽位口径见 PatchSizeAnalyzer。</summary>
public sealed class PatchSizeReport
{
    /// <summary>VM 共用求值栈的槽位总数（MAX_EVALUATION_STACK_SIZE）。</summary>
    public int EvaluationStackLimit { get; set; }

    /// <summary>
    /// 最深解释调用链上各帧 (实参 + 局部 + 运算栈) 之和。只统计**能静态定界**的链；
    /// 含递归的入口不计入，由 <see cref="ChainBounded"/> 与对应的 warn 单独反映。
    /// </summary>
    public int DeepestChainSlots { get; set; }
    public double DeepestChainPercent { get; set; }
    public string? DeepestChain { get; set; }

    /// <summary>false = 至少有一个入口经 helper 递归，其栈占用取决于运行期递归深度。</summary>
    public bool ChainBounded { get; set; } = true;

    public int MaxMethodSlots { get; set; }
    public int MaxInstructionCount { get; set; }
    public int MaxLocalCount { get; set; }
    public int MaxStackSize { get; set; }
    public int HelperCount { get; set; }
}

/// <summary>一条规模检查结果。<c>severity</c> 取 <c>warn</c> 或 <c>fail</c>。</summary>
public sealed class BuildDiagnostic
{
    public string Severity { get; set; } = "";
    public string Rule { get; set; } = "";
    public string? Method { get; set; }
    public string Message { get; set; } = "";
}

/// <summary>补丁引用的闭合泛型实例及其元数据引用证据。</summary>
public sealed class GenericInstanceReport
{
    public string Instance { get; set; } = "";

    /// <summary>true 表示找到元数据签名引用，不保证 AOT 代码或运行时调用可用。</summary>
    public bool Proven { get; set; }

    /// <summary>元数据引用出处；未找到时为 null。</summary>
    public string? ProvenBy { get; set; }
}

/// <summary>一个被搬进目标程序集、并登记进 process_cfg 第 2 段（new methods）的 helper。</summary>
public sealed class GraftHelperReport
{
    public string Implementation { get; set; } = "";
    public string Grafted { get; set; } = "";
    public int SourceInstructionCount { get; set; }
    public int ResultInstructionCount { get; set; }
    public int LocalCount { get; set; }
    public int MaxStackSize { get; set; }
    public int ExceptionHandlerCount { get; set; }
}

public static class SourceBuilderJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static T Load<T>(string path) where T : class =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException($"Unable to deserialize {path} as {typeof(T).Name}.");

    public static void Save<T>(string path, T value) where T : class
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, Options));
    }
}
