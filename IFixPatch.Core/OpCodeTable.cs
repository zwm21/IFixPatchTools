using System.Reflection;
using Mono.Cecil.Cil;

namespace IFixPatch.Core;

/// <summary>
/// IFix 的 Code 助记符 → Cecil OpCode。IFix 的枚举名大多直接取自 CIL，所以主体靠反射
/// Cecil 的 OpCodes 自动对齐；剩下的是 IFix 自定义码或需要特殊发射的，单独列出。
/// </summary>
public static class OpCodeTable
{
    private static readonly Dictionary<string, OpCode> ByName = BuildFromCecil();

    /// <summary>IFix 自定义/需特殊处理的码，不做直接名字映射。</summary>
    public static readonly HashSet<string> Custom =
    [
        "StackSpace",     // 伪指令：栈帧尺寸，转译时丢弃
        "Call",           // 补丁内部调用，operand 高 16 位是参数个数
        "CallExtern",     // 外部调用
        "Callvirtvirt",   // 内部虚调用，走 vtable 槽位
        "Ldvirtftn2",     // 同上，取函数指针
        "Newanon",        // 分配闭包/匿名类
        "Ldtype",         // ldtoken 的定制变体，直接 push System.Type
        "Jmp",            // IFix 未真正使用
    ];

    private static Dictionary<string, OpCode> BuildFromCecil()
    {
        var map = new Dictionary<string, OpCode>(StringComparer.Ordinal);
        foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (f.FieldType != typeof(OpCode)) continue;
            if (f.GetValue(null) is OpCode oc) map[f.Name] = oc;
        }
        return map;
    }

    public static bool TryGet(string ifixName, out OpCode opcode) => ByName.TryGetValue(ifixName, out opcode);

    /// <summary>诊断用：列出既非自定义、又在 Cecil 里找不到同名 OpCode 的助记符。</summary>
    public static List<string> Unmapped(IEnumerable<string> ifixNames)
        => [.. ifixNames.Where(n => !Custom.Contains(n) && !ByName.ContainsKey(n))];
}
