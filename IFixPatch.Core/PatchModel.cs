namespace IFixPatch.Core;

/// <summary>
/// 一条 IFix VM 指令。对应 IFix.Core.Instruction（[StructLayout(Sequential)] { int Code; int Operand; }）。
/// Code 是被 ShuffleInstruction 打乱过的枚举整数值，语义需借助逐游戏恢复的 opcode 映射表。
/// </summary>
public struct Instr
{
    public int Code;
    public int Operand;
}

/// <summary>异常处理块，对应 IFix.Core.ExceptionHandler。区间下标都以“指令”为单位。</summary>
public sealed class Eh
{
    public int HandlerType;   // Catch=0 Filter=1 Finally=2 Fault=4
    public int CatchTypeId;   // 外部类型下标，-1 表示 object
    public int TryStart, TryEnd, HandlerStart, HandlerEnd;
}

/// <summary>补丁里的一个方法体，数组下标即 methodId（0..methodCount-1，稠密排列，多数为空占位）。</summary>
public sealed class IFixMethod
{
    public int Id;
    public List<Instr> Code = [];
    public List<Eh> Handlers = [];
    public bool IsEmpty => Code.Count == 0;
}

/// <summary>外部方法签名，对应 readMethod/writeMethod。所有类型都是外部类型表的下标。</summary>
public sealed class ExternMethodSig
{
    public bool IsGenericInstance;
    public int DeclaringTypeId;
    public string Name = "";
    public List<int> GenericArgTypeIds = [];
    public List<Param> Params = [];

    public sealed class Param
    {
        public bool IsGeneric;        // 仅泛型实例方法用到
        public string? GenericName;   // IsGeneric 时为类型名（可能含 !!0 占位符）
        public int TypeId = -1;       // 否则为外部类型表下标
    }
}

public sealed class FieldEntry
{
    public bool IsNew;
    public int DeclaringTypeId;
    public string Name = "";
    public int FieldTypeId = -1;   // 仅新增字段
    public int CctorId = -1;       // 仅新增字段，-1 表示无
}

public sealed class StaticFieldEntry
{
    public int FieldTypeId;
    public int CctorId;
}

/// <summary>匿名类/闭包信息，对应 AnonymousStoreyInfo。</summary>
public sealed class AnonStorey
{
    public List<int> FieldTypes = [];  // 0=primitive, -2=引用类型, 其它=外部类型id+1（值类型）
    public int CtorId;
    public int CtorParamNum;
    public bool HasInterfaces;
    public List<ItfSlots> Interfaces = [];
    // 该闭包自身的虚方法表，长度由补丁写入、并非固定 4 槽。槽 0..3 是 object 的
    // Equals/Finalize/GetHashCode/ToString，闭包一般不 override（记 -1）；接口实现从槽 4 起。
    public List<int> VTable = [];      // -1 = 该槽未 override

    /// <summary>一个接口的实现槽：接口类型 + 按接口方法顺序排列的 methodId。</summary>
    public sealed class ItfSlots
    {
        public int TypeId;
        public List<int> MethodIds = [];
    }
}

/// <summary>修复列表的一项：把补丁内 MethodId 绑定到它替换的原始方法。</summary>
public sealed class FixEntry
{
    public ExternMethodSig Method = null!;
    public int MethodId;
}

/// <summary>解析后的整份补丁。</summary>
public sealed class PatchFile
{
    public ulong Magic;
    public string InterfaceBridgeType = "";
    public List<string> ExternTypes = [];
    public List<IFixMethod> Methods = [];
    public List<ExternMethodSig> ExternMethods = [];
    public List<string> InternStrings = [];
    public List<FieldEntry> Fields = [];
    public List<StaticFieldEntry> StaticFields = [];
    public List<AnonStorey> AnonStoreys = [];
    public string WrapperManagerType = "";
    public string IdMapSuffix = "";
    public List<FixEntry> FixList = [];
    public List<string> NewClasses = [];

    /// <summary>是否完整解析到文件尾。缺少 dump.cs 索引时会在含接口的匿名类处停下。</summary>
    public bool FullyParsed;

    /// <summary>解析结束后未消费的字节数；结合 FullyParsed 检查布局，0 不保证内容语义正确。</summary>
    public long TrailingBytes;
    public string? ParseNote;

    public string TypeName(int id) =>
        id >= 0 && id < ExternTypes.Count ? ExternTypes[id] : $"<type#{id}>";
}
