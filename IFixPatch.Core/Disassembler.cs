using System.Text;

namespace IFixPatch.Core;

/// <summary>把一个方法的 Instr[] 渲染成带控制流标签与异常区标注的反汇编清单。</summary>
public sealed class Disassembler(PatchFile p, OpCodeMap map)
{
    private readonly PatchFile _p = p;
    private readonly OpCodeMap _map = map;
    private static readonly HashSet<string> CallFamily = ["Call", "Callvirt", "CallExtern", "Newobj", "Callvirtvirt"];
    private static readonly HashSet<string> TypeOperand =
        [ "Ldtype", "Ldtoken", "Box", "Unbox", "Unbox_Any", "Castclass", "Isinst", "Newarr",
          "Initobj", "Ldobj", "Stobj", "Cpobj", "Constrained", "Sizeof", "Mkrefany",
          "Refanyval", "Ldelem_Any", "Stelem_Any", "Ldelema" ];
    private static readonly HashSet<string> FieldFamily = ["Ldfld", "Stfld", "Ldflda", "Ldsfld", "Stsfld", "Ldsflda"];
    // 相对偏移分支（target = i + operand）。Leave 单独处理（绝对目标）。
    private static readonly HashSet<string> RelBranch =
        [ "Br", "Brfalse", "Brtrue", "Beq", "Bge", "Bgt", "Ble", "Blt",
          "Bne_Un", "Bge_Un", "Bgt_Un", "Ble_Un", "Blt_Un" ];

    private sealed class Line
    {
        public int Index;
        public string Mnemonic = "";
        public int Operand;
        public int Consumed = 1;
        public string Annot = "";
    }

    /// <summary>
    /// 统计每条字符串常量被 <c>Ldstr</c> 引用的次数。必须走与反汇编相同的解码流程：
    /// <c>Ldc_I8</c>/<c>Ldc_R8</c>/<c>Switch</c> 会借用后续槽位存放立即数，
    /// 直接线性扫描 <c>Instr[]</c> 会把借位槽当成指令，误报出不存在的引用。
    /// </summary>
    public int[] CountStringUses()
    {
        var uses = new int[_p.InternStrings.Count];
        var discard = new HashSet<int>();
        foreach (var m in _p.Methods)
        {
            if (m.IsEmpty) continue;
            int i = 0;
            while (i < m.Code.Count)
            {
                var line = Decode(m.Code, i, discard);
                if (line.Mnemonic == "Ldstr" && line.Operand >= 0 && line.Operand < uses.Length)
                    uses[line.Operand]++;
                i += line.Consumed;
            }
        }
        return uses;
    }

    public string Format(IFixMethod m)
    {
        var lines = new List<Line>();
        var labels = new HashSet<int>();

        // pass 1：解码 + 收集跳转目标 / 异常区边界作为标签
        int i = 0;
        while (i < m.Code.Count)
        {
            var line = Decode(m.Code, i, labels);
            lines.Add(line);
            i += line.Consumed;
        }
        foreach (var eh in m.Handlers)
        {
            labels.Add(eh.TryStart); labels.Add(eh.TryEnd);
            labels.Add(eh.HandlerStart); labels.Add(eh.HandlerEnd);
            if (eh.HandlerEnd == -1) labels.Remove(-1);
            if (eh.TryEnd == -1) labels.Remove(-1);
            if (eh.HandlerStart == -1) labels.Remove(-1);
            if (eh.TryStart == -1) labels.Remove(-1);
        }

        // pass 2：渲染
        var sb = new StringBuilder();
        foreach (var eh in m.Handlers)
        {
            static string fmt(int v) => v == -1 ? "-1" : $"L{v:0000}";
            sb.Append($"    .eh {HandlerKind(eh)} try[{fmt(eh.TryStart)}..{fmt(eh.TryEnd)}) " +
                      $"handler[{fmt(eh.HandlerStart)}..{fmt(eh.HandlerEnd)})" +
                      $"{(eh.HandlerType == 0 ? $" catch {(eh.CatchTypeId < 0 ? "object" : PrettyType(eh.CatchTypeId))}" : "")}\n");
        }

        foreach (var line in lines)
        {
            if (labels.Contains(line.Index)) sb.Append($"  L{line.Index:0000}:\n");
            sb.Append($"    {line.Index,5:0000}  {line.Mnemonic,-14} {line.Operand,10}");
            if (line.Annot.Length > 0) sb.Append($"   ; {line.Annot}");
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>解码 code[i]，填充跳转目标到 labels，返回渲染信息（含借用槽数）。</summary>
    private Line Decode(List<Instr> code, int i, HashSet<int> labels)
    {
        var ins = code[i];
        bool named = _map.TryName(ins.Code, out var mn);
        mn ??= $"code#{ins.Code}";
        var line = new Line { Index = i, Mnemonic = mn, Operand = ins.Operand };

        if (!named)
        {
            // 无映射：无法判断是否借用后续槽，逐槽线性打印。
        }
        else if (mn == "StackSpace")
        {
            line.Annot = $"locals={ins.Operand >> 16}, maxStack={ins.Operand & 0xFFFF}";
        }
        else if (mn == "Ldc_I8" || mn == "Ldc_R8")
        {
            if (i + 1 < code.Count)
            {
                var slot = code[i + 1];
                long bits = (uint)slot.Code | ((long)slot.Operand << 32);
                line.Annot = mn == "Ldc_I8" ? $"= {bits}" : $"= {BitConverter.Int64BitsToDouble(bits)}";
                line.Consumed = 2;
            }
        }
        else if (mn == "Ldc_R4")
        {
            line.Annot = $"= {BitConverter.Int32BitsToSingle(ins.Operand)}";
        }
        else if (mn == "Ldstr")
        {
            line.Annot = ins.Operand >= 0 && ins.Operand < _p.InternStrings.Count
                ? $"\"{Escape(_p.InternStrings[ins.Operand])}\"" : $"str#{ins.Operand}";
        }
        else if (mn == "Switch")
        {
            int n = ins.Operand;
            int slots = (n + 1) >> 1;
            var targets = new List<int>();
            for (int s = 0; s < slots && i + 1 + s < code.Count; s++)
            {
                var slot = code[i + 1 + s];
                targets.Add(slot.Code);
                if (targets.Count < n) targets.Add(slot.Operand);
            }
            var abs = targets.Select(t => i + t).ToList();
            foreach (var t in abs) labels.Add(t);
            line.Annot = $"{n} cases → [{string.Join(", ", abs.Select(t => $"L{t:0000}"))}]";
            line.Consumed = 1 + slots;
        }
        else if (CallFamily.Contains(mn))
        {
            int id = ins.Operand & 0xFFFF, pc = ins.Operand >> 16;
            line.Annot = mn switch
            {
                "CallExtern" or "Newobj" => $"argc={pc}, extern#{id} = {FormatExtern(id)}",
                "Callvirtvirt" => $"argc={pc}, vtable[{id}]",
                _ => $"argc={pc}, method#{id}",
            };
        }
        else if (TypeOperand.Contains(mn))
        {
            line.Annot = ins.Operand >= 0 ? PrettyType(ins.Operand) : $"type#{ins.Operand}";
        }
        else if (FieldFamily.Contains(mn))
        {
            if (ins.Operand >= 0 && ins.Operand < _p.Fields.Count)
            {
                var f = _p.Fields[ins.Operand];
                line.Annot = $"{PrettyType(f.DeclaringTypeId)}.{f.Name}{(f.IsNew ? " (new)" : "")}";
            }
            else
            {
                // 负数操作数分两种语义：实例形式寻址栈上闭包实例的字段（下标各闭包自算），
                // 静态形式才是全局唯一的 VM 静态字段表。标注必须区分，否则读者会当成同一批全局量。
                int neg = -(ins.Operand + 1);
                line.Annot = mn is "Ldfld" or "Stfld" or "Ldflda"
                    ? $"storeyField[{neg}]  (接收者闭包的第 {neg} 个字段)"
                    : $"vmStaticField[{neg}]";
            }
        }
        else if (mn == "Newanon")
        {
            line.Annot = $"anonStorey#{ins.Operand}";
        }
        else if (mn == "Leave")
        {
            labels.Add(ins.Operand);
            line.Annot = $"→ L{ins.Operand:0000} (abs)";
        }
        else if (RelBranch.Contains(mn))
        {
            int target = i + ins.Operand;
            labels.Add(target);
            line.Annot = $"→ L{target:0000}";
        }
        return line;
    }

    private static string HandlerKind(Eh eh) => eh.HandlerType switch
    {
        0 => "catch",
        1 => "filter",
        2 => "finally",
        4 => "fault",
        _ => $"type{eh.HandlerType}"
    };

    public string FormatExtern(int id) =>
        id < 0 || id >= _p.ExternMethods.Count ? $"<externMethod#{id}>" : FormatSig(_p.ExternMethods[id]);

    public string FormatSig(ExternMethodSig sig)
    {
        var ps = new List<string>();
        foreach (var pr in sig.Params)
            ps.Add(pr.IsGeneric ? (pr.GenericName ?? "T") : PrettyType(pr.TypeId));

        string generic = sig.IsGenericInstance && sig.GenericArgTypeIds.Count > 0
            ? "<" + string.Join(", ", sig.GenericArgTypeIds.Select(PrettyType)) + ">"
            : "";

        return $"{PrettyType(sig.DeclaringTypeId)}::{sig.Name}{generic}({string.Join(", ", ps)})";
    }

    /// <summary>取外部类型的可读全名：裁掉 AQN 的程序集后缀（按括号深度找顶层第一个逗号）。</summary>
    private string PrettyType(int id) => Pretty(_p.TypeName(id));

    private static string Pretty(string aqn)
    {
        int depth = 0;
        for (int i = 0; i < aqn.Length; i++)
        {
            char c = aqn[i];
            if (c == '[') depth++;
            else if (c == ']') depth--;
            else if (c == ',' && depth == 0) return aqn[..i];
        }
        return aqn;
    }

    public static string Escape(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
}
