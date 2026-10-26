namespace IFixPatch.Core;

/// <summary>
/// 推断补丁内部方法（349 个方法槽）的签名。补丁只在修复列表里存了 47 个方法的签名，
/// 其余是被牵连进来的辅助/闭包方法，没有名字也没有签名，但可以推出来：
///
/// · 参数个数：调用点的 operand 高 16 位就是 argc；退化时用 Ldarg 最大下标 + 1。
/// · 是否有返回值：模拟求值栈深度，若某个 Ret 处深度 &gt; 0 则有返回值。
///   内部调用的 push 数依赖被调方是否有返回值，存在循环依赖，故用单调不动点迭代
///   （全部初始化为 void，只会从 false 变 true，必然收敛）。
/// </summary>
public sealed class InternalSignatures
{
    public int[] ArgCount { get; }
    public bool[] ReturnsValue { get; }

    private readonly PatchFile _p;
    private readonly OpCodeMap _map;

    /// <summary>
    /// externReturnsValue 必须由构造函数传入：它参与栈深度推断，
    /// 若用对象初始化器在构造后赋值，推断时拿到的仍是默认值，会把所有外部调用
    /// 当成无返回值，进而把有返回值的内部方法误判为 void。
    /// </summary>
    public InternalSignatures(PatchFile p, OpCodeMap map, Func<int, bool> externReturnsValue)
    {
        _p = p; _map = map; ExternReturnsValue = externReturnsValue;
        int n = p.Methods.Count;
        ArgCount = new int[n];
        ReturnsValue = new bool[n];

        CollectArgCounts();
        IterateReturnsValue();
    }

    private void CollectArgCounts()
    {
        for (int i = 0; i < ArgCount.Length; i++) ArgCount[i] = -1;

        // 调用点提供 argc
        foreach (var m in _p.Methods)
            foreach (var (mn, operand) in WalkPairs(m))
            {
                if (mn is "Call" or "Callvirt" or "Callvirtvirt")
                {
                    int id = operand & 0xFFFF, argc = operand >> 16;
                    if (id >= 0 && id < ArgCount.Length && argc > ArgCount[id]) ArgCount[id] = argc;
                }
            }

        // 修复列表的签名更权威（含 this）
        foreach (var fx in _p.FixList)
            if (fx.MethodId >= 0 && fx.MethodId < ArgCount.Length)
                ArgCount[fx.MethodId] = Math.Max(ArgCount[fx.MethodId], fx.Method.Params.Count);

        // 兜底：用 Ldarg 最大下标 + 1
        for (int i = 0; i < _p.Methods.Count; i++)
        {
            int maxArg = -1;
            foreach (var (mn, operand) in WalkPairs(_p.Methods[i]))
                if ((mn is "Ldarg" or "Ldarga" or "Starg") && operand > maxArg) maxArg = operand;
            if (maxArg + 1 > ArgCount[i]) ArgCount[i] = maxArg + 1;
            if (ArgCount[i] < 0) ArgCount[i] = 0;
        }
    }

    private void IterateReturnsValue()
    {
        for (int round = 0; round < 12; round++)
        {
            bool changed = false;
            for (int id = 0; id < _p.Methods.Count; id++)
            {
                if (ReturnsValue[id]) continue;
                if (RetDepthPositive(_p.Methods[id])) { ReturnsValue[id] = true; changed = true; }
            }
            if (!changed) break;
        }
    }

    /// <summary>模拟栈深度，判断是否存在深度 &gt; 0 的 Ret。分支目标用工作表传播深度。</summary>
    private bool RetDepthPositive(IFixMethod m)
    {
        var decoded = Walk(m).ToList();
        if (decoded.Count == 0) return false;

        var indexOf = new Dictionary<int, int>();
        for (int i = 0; i < decoded.Count; i++) indexOf[decoded[i].Index] = i;

        var depth = new int?[decoded.Count];
        var work = new Queue<(int Pos, int Depth)>();
        work.Enqueue((0, 0));

        while (work.Count > 0)
        {
            var (pos, d) = work.Dequeue();
            if (pos < 0 || pos >= decoded.Count) continue;
            if (depth[pos].HasValue) continue;
            depth[pos] = d;

            var (mn, operand, ifixIndex) = (decoded[pos].Mnemonic, decoded[pos].Operand, decoded[pos].Index);
            if (mn == "Ret") { if (d > 0) return true; continue; }

            var (pop, push) = Effect(mn, operand);
            int nd = Math.Max(0, d - pop) + push;

            switch (mn)
            {
                case "Br":
                case "Leave":
                    {
                        int target = mn == "Leave" ? operand : ifixIndex + operand;
                        if (indexOf.TryGetValue(target, out var tp)) work.Enqueue((tp, mn == "Leave" ? 0 : nd));
                        continue;
                    }
                case "Throw":
                case "Endfinally":
                case "Endfilter":
                    continue;
                case "Switch":
                    foreach (var t in decoded[pos].SwitchTargets)
                        if (indexOf.TryGetValue(t, out var sp)) work.Enqueue((sp, nd));
                    work.Enqueue((pos + 1, nd));
                    continue;
                default:
                    if (mn.StartsWith("Br") || mn.StartsWith('B') && mn.Length > 1 && IsCondBranch(mn))
                    {
                        int target = ifixIndex + operand;
                        if (indexOf.TryGetValue(target, out var bp)) work.Enqueue((bp, nd));
                    }
                    work.Enqueue((pos + 1, nd));
                    continue;
            }
        }
        return false;
    }

    private static bool IsCondBranch(string mn) => mn is "Brfalse" or "Brtrue" or "Beq" or "Bge" or "Bgt"
        or "Ble" or "Blt" or "Bne_Un" or "Bge_Un" or "Bgt_Un" or "Ble_Un" or "Blt_Un";

    /// <summary>该指令的 (弹出数, 压入数)。内部调用的压入数取决于被调方，用当前估计值。</summary>
    private (int Pop, int Push) Effect(string mn, int operand)
    {
        switch (mn)
        {
            case "StackSpace": return (0, 0);
            case "Ldarg" or "Ldloc" or "Ldc_I4" or "Ldc_I8" or "Ldc_R4" or "Ldc_R8"
                or "Ldstr" or "Ldnull" or "Ldtype" or "Ldsfld" or "Ldsflda" or "Ldarga"
                or "Ldloca" or "Ldftn" or "Ldvirtftn" or "Ldvirtftn2" or "Newanon" or "Arglist":
                return (0, 1);
            case "Stloc" or "Starg" or "Stsfld" or "Pop" or "Throw" or "Brfalse" or "Brtrue"
                or "Switch" or "Endfilter":
                return (1, 0);
            case "Dup": return (1, 2);
            case "Ldfld" or "Ldflda" or "Ldlen" or "Ldobj" or "Castclass" or "Isinst" or "Box"
                or "Unbox" or "Unbox_Any" or "Newarr" or "Neg" or "Not" or "Ldtoken" or "Sizeof"
                or "Refanytype" or "Ckfinite" or "Localloc":
                return (1, 1);
            case "Stfld" or "Stobj" or "Cpobj" or "Beq" or "Bge" or "Bgt" or "Ble" or "Blt"
                or "Bne_Un" or "Bge_Un" or "Bgt_Un" or "Ble_Un" or "Blt_Un":
                return (2, 0);
            case "Add" or "Sub" or "Mul" or "Div" or "Div_Un" or "Rem" or "Rem_Un" or "And" or "Or"
                or "Xor" or "Shl" or "Shr" or "Shr_Un" or "Ceq" or "Cgt" or "Cgt_Un" or "Clt" or "Clt_Un"
                or "Add_Ovf" or "Add_Ovf_Un" or "Mul_Ovf" or "Mul_Ovf_Un" or "Sub_Ovf" or "Sub_Ovf_Un"
                or "Ldelema" or "Ldelem_Any":
                return (2, 1);
            case "Initobj": return (1, 0);
            case "Stelem_Any": return (3, 0);

            case "CallExtern":
                {
                    int id = operand & 0xFFFF, argc = operand >> 16;
                    bool nonVoid = id >= 0 && id < _p.ExternMethods.Count && ExternReturnsValue(id);
                    return (argc, nonVoid ? 1 : 0);
                }
            case "Newobj": return (operand >> 16, 1);
            case "Call" or "Callvirt" or "Callvirtvirt":
                {
                    int id = operand & 0xFFFF, argc = operand >> 16;
                    bool nonVoid = id >= 0 && id < ReturnsValue.Length && ReturnsValue[id];
                    return (argc, nonVoid ? 1 : 0);
                }
            default:
                if (mn.StartsWith("Conv")) return (1, 1);
                if (mn.StartsWith("Ldind")) return (1, 1);
                if (mn.StartsWith("Stind")) return (2, 0);
                if (mn.StartsWith("Ldelem")) return (2, 1);
                if (mn.StartsWith("Stelem")) return (3, 0);
                return (0, 0);
        }
    }

    /// <summary>外部方法是否有返回值。构造时注入，推断过程依赖它。</summary>
    private Func<int, bool> ExternReturnsValue { get; }

    private sealed record Step(int Index, string Mnemonic, int Operand, List<int> SwitchTargets);

    /// <summary>按借槽规则遍历一个方法的指令（与 Disassembler/ILTranspiler 的解码一致）。</summary>
    private List<Step> Walk(IFixMethod m)
    {
        var outp = new List<Step>();
        int i = 0;
        while (i < m.Code.Count)
        {
            var ins = m.Code[i];
            if (!_map.TryName(ins.Code, out var mn)) break;
            int slots = 1;
            var targets = new List<int>();
            if (mn is "Ldc_I8" or "Ldc_R8") slots = 2;
            else if (mn == "Switch")
            {
                int n = ins.Operand, sl = (n + 1) >> 1;
                var t = new List<int>();
                for (int s = 0; s < sl && i + 1 + s < m.Code.Count; s++)
                {
                    var x = m.Code[i + 1 + s];
                    t.Add(x.Code);
                    if (t.Count < n) t.Add(x.Operand);
                }
                targets = [.. t.Select(x => i + x)];
                slots = 1 + sl;
            }
            outp.Add(new Step(i, mn, ins.Operand, targets));
            i += slots;
        }
        return outp;
    }

    private IEnumerable<(string Mnemonic, int Operand)> WalkPairs(IFixMethod m)
        => Walk(m).Select(s => (s.Mnemonic, s.Operand));
}
