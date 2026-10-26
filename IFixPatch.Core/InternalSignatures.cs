namespace IFixPatch.Core;

/// <summary>
/// Reads internal return flags and infers argument counts from call sites and argument access.
/// VM vtable slots are resolved through each anonymous type's table.
/// </summary>
public sealed class InternalSignatures
{
    public int[] ArgCount { get; }
    public bool[] ReturnsValue { get; }

    private readonly PatchFile _p;
    private readonly OpCodeMap _map;

    public InternalSignatures(PatchFile p, OpCodeMap map)
    {
        _p = p; _map = map;
        int n = p.Methods.Count;
        ArgCount = new int[n];
        ReturnsValue = new bool[n];

        CollectArgCounts();
        for (var id = 0; id < n; id++)
            ReturnsValue[id] = WalkPairs(p.Methods[id]).Any(instruction => instruction.Mnemonic == "Ret" && instruction.Operand != 0);
    }

    /// <summary>Compatibility overload; Ret operands now provide the return flag directly.</summary>
    public InternalSignatures(PatchFile p, OpCodeMap map, Func<int, bool> externReturnsValue) : this(p, map) { }

    private void CollectArgCounts()
    {
        for (int i = 0; i < ArgCount.Length; i++) ArgCount[i] = -1;

        // 调用点提供 argc
        foreach (var m in _p.Methods)
            foreach (var (mn, operand) in WalkPairs(m))
            {
                if (mn is "Call" or "Callvirt" or "Callvirtvirt")
                {
                    int id = operand & 0xFFFF, argc = (int)((uint)operand >> 16);
                    foreach (var target in mn == "Callvirtvirt" ? PatchDispatch.Targets(_p, id) : [id])
                        if (target >= 0 && target < ArgCount.Length && argc > ArgCount[target]) ArgCount[target] = argc;
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

    // Borrowed slots hold raw data rather than opcodes.
    private IEnumerable<(string Mnemonic, int Operand)> WalkPairs(IFixMethod method)
    {
        for (var at = 0; at < method.Code.Count;)
        {
            var instruction = method.Code[at];
            if (!_map.TryName(instruction.Code, out var name)) yield break;
            yield return (name, instruction.Operand);
            long length = name is "Ldc_I8" or "Ldc_R8" ? 2
                : name == "Switch" ? 1L + ((long)instruction.Operand + 1) / 2 : 1;
            if (length < 1 || length > method.Code.Count - at) yield break;
            at += (int)length;
        }
    }
}
