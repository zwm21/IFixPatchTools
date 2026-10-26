using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IFixPatch.Core;

public sealed class BalanceIssue
{
    public string Method = "";
    public int Offset;
    public string Kind = "";          // underflow / ret-mismatch / inconsistent / invalid-operand
    public string Detail = "";
    public List<string> Context = [];
}

/// <summary>
/// 对发射出的 Cecil 方法体做栈平衡自查。
///
/// 栈效应取自 Cecil 的 OpCode.StackBehaviourPop/Push（CIL 规范的权威建模），
/// 只有 VarPop/VarPush（调用族）需要按被调方签名展开。这样诊断结论不依赖
/// 本项目自己那套 pop/push 估计，避免用同一个可能有误的模型自证。
/// </summary>
public static class StackBalanceCheck
{
    public static List<BalanceIssue> Check(MethodDefinition m)
    {
        var issues = new List<BalanceIssue>();
        var body = m.Body;
        if (body == null || body.Instructions.Count == 0) return issues;

        var ins = body.Instructions;
        var indexOf = new Dictionary<Instruction, int>();
        for (int i = 0; i < ins.Count; i++) indexOf[ins[i]] = i;

        var depthAt = new int?[ins.Count];
        var work = new Queue<(int Pos, int Depth)>();
        work.Enqueue((0, 0));

        // 异常处理块入口的初始深度：catch/filter 处理器入栈一个异常对象
        foreach (var eh in body.ExceptionHandlers)
        {
            if (eh.HandlerStart != null && indexOf.TryGetValue(eh.HandlerStart, out var hp))
                work.Enqueue((hp, eh.HandlerType is ExceptionHandlerType.Catch or ExceptionHandlerType.Filter ? 1 : 0));
            if (eh.FilterStart != null && indexOf.TryGetValue(eh.FilterStart, out var fp))
                work.Enqueue((fp, 1));
        }

        while (work.Count > 0)
        {
            var (pos, d) = work.Dequeue();
            if (pos < 0 || pos >= ins.Count) continue;

            if (depthAt[pos].HasValue)
            {
                if (depthAt[pos]!.Value != d)
                    issues.Add(Make(m, ins, pos, "inconsistent",
                        $"同一位置两条路径深度不同: {depthAt[pos]} vs {d}"));
                continue;
            }
            depthAt[pos] = d;

            var cur = ins[pos];
            int pop, push;
            try { (pop, push) = Effect(cur, m); }
            catch (InvalidDataException exception)
            {
                issues.Add(Make(m, ins, pos, "invalid-operand", exception.Message));
                continue;
            }

            if (d - pop < 0)
            {
                issues.Add(Make(m, ins, pos, "underflow",
                    $"{cur.OpCode.Name} 需要弹出 {pop}，当前深度 {d}"));
                continue;
            }
            int nd = d - pop + push;

            switch (cur.OpCode.FlowControl)
            {
                case FlowControl.Return:
                    {
                        bool nonVoid = m.ReturnType.FullName != "System.Void";
                        int expect = nonVoid ? 1 : 0;
                        if (cur.OpCode == OpCodes.Ret && d != expect)
                            issues.Add(Make(m, ins, pos, "ret-mismatch",
                                $"Ret 处深度 {d}，按返回类型 {m.ReturnType.Name} 应为 {expect}"));
                        continue;
                    }
                case FlowControl.Throw:
                    continue;
                case FlowControl.Branch:
                    {
                        if (cur.Operand is Instruction t && indexOf.TryGetValue(t, out var tp))
                            work.Enqueue((tp, cur.OpCode == OpCodes.Leave || cur.OpCode == OpCodes.Leave_S ? 0 : nd));
                        continue;
                    }
                case FlowControl.Cond_Branch:
                    {
                        if (cur.Operand is Instruction t && indexOf.TryGetValue(t, out var tp))
                            work.Enqueue((tp, nd));
                        else if (cur.Operand is Instruction[] targets)
                            foreach (var tt in targets)
                                if (indexOf.TryGetValue(tt, out var sp)) work.Enqueue((sp, nd));
                        if (pos + 1 < ins.Count) work.Enqueue((pos + 1, nd));
                        continue;
                    }
                default:
                    if (pos + 1 < ins.Count) work.Enqueue((pos + 1, nd));
                    continue;
            }
        }
        return issues;
    }

    private static BalanceIssue Make(MethodDefinition m, Mono.Collections.Generic.Collection<Instruction> ins,
        int pos, string kind, string detail)
    {
        var issue = new BalanceIssue
        {
            Method = m.Name,
            Offset = ins[pos].Offset,
            Kind = kind,
            Detail = detail,
        };
        for (int i = Math.Max(0, pos - 6); i <= Math.Min(ins.Count - 1, pos + 1); i++)
            issue.Context.Add($"{(i == pos ? ">>" : "  ")} {ins[i]}");
        return issue;
    }

    /// <summary>该指令的 (弹出数, 压入数)。用 Cecil 的 StackBehaviour；调用族按签名展开。</summary>
    private static (int Pop, int Push) Effect(Instruction i, MethodDefinition container)
    {
        int pop = i.OpCode.StackBehaviourPop switch
        {
            StackBehaviour.Pop0 => 0,
            StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
            StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi
                or StackBehaviour.Popi_popi8 or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8
                or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
            StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi
                or StackBehaviour.Popref_popi_popi8 or StackBehaviour.Popref_popi_popr4
                or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref => 3,
            StackBehaviour.PopAll => 0,      // 只出现在 leave，单独处理
            StackBehaviour.Varpop => VarPop(i, container),
            _ => 0,
        };
        int push = i.OpCode.StackBehaviourPush switch
        {
            StackBehaviour.Push0 => 0,
            StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8
                or StackBehaviour.Pushr4 or StackBehaviour.Pushr8 or StackBehaviour.Pushref => 1,
            StackBehaviour.Push1_push1 => 2,
            StackBehaviour.Varpush => VarPush(i),
            _ => 0,
        };
        return (pop, push);
    }

    private static int VarPop(Instruction i, MethodDefinition container)
    {
        if (i.OpCode == OpCodes.Ret)
            return container.ReturnType.FullName != "System.Void" ? 1 : 0;
        var signature = CallSignature(i);
        int count = signature.Parameters.Count;
        if (i.OpCode != OpCodes.Newobj && signature.HasThis) count++;
        if (i.OpCode == OpCodes.Calli) count++;
        return count;
    }

    private static IMethodSignature CallSignature(Instruction instruction)
    {
        IMethodSignature? signature = instruction.OpCode == OpCodes.Calli
            ? instruction.Operand as CallSite : instruction.Operand as MethodReference;
        return signature ?? throw new InvalidDataException($"{instruction.OpCode.Name} requires a method signature operand of the matching instruction kind.");
    }

    private static int VarPush(Instruction i)
    {
        return CallSignature(i).ReturnType.FullName != "System.Void" ? 1 : 0;
    }
}
