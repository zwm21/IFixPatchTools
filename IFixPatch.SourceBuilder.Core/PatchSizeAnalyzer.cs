using Mono.Cecil;

namespace IFixPatch.SourceBuilder.Core;

/// <summary>
/// 检查 StackSpace 的两个 16 位计数字段，以及有界调用链的共用求值栈用量。
/// 每帧计入实参、局部变量和最大运算栈；递归链单独警告。
/// 求值栈容量按固定上游的 10240 槽位计算，其他目标版本需另行核对。
/// helper 数与指令数仅使用经验警告阈值。
/// </summary>
internal static class PatchSizeAnalyzer
{
    /// <summary>IFix.Core.VirtualMachine.MAX_EVALUATION_STACK_SIZE。</summary>
    public const int EvaluationStackSlots = 1024 * 10;

    /// <summary>StackSpace 操作数里 locals 与 maxstack 各自的字段宽度上限。</summary>
    public const int StackSpaceFieldLimit = ushort.MaxValue;

    /// <summary>一帧在共用求值栈上占的槽位：实参（含 this）+ 局部 + 运算栈深度。</summary>
    public static int FrameSlots(MethodDefinition method) =>
        method.Parameters.Count + (method.HasThis ? 1 : 0)
        + method.Body.Variables.Count + method.Body.MaxStackSize;

    public static PatchSizeReport Analyze(
        IReadOnlyList<GraftedMethod> roots,
        IReadOnlyDictionary<MethodDefinition, MethodDefinition> helpers,
        IReadOnlyDictionary<MethodDefinition, List<MethodDefinition>> calls,
        PatchLimits limits,
        List<BuildDiagnostic> diagnostics)
    {
        var all = roots
            .Select(root => (root.Label, root.Implementation, root.Grafted))
            .Concat(helpers.Select(pair => (Label: pair.Value.FullName, Implementation: pair.Key, Grafted: pair.Value)))
            .ToList();

        foreach (var (label, _, grafted) in all)
        {
            CheckField(diagnostics, label, "locals", grafted.Body.Variables.Count);
            CheckField(diagnostics, label, "max-stack", grafted.Body.MaxStackSize);

            if (grafted.Body.Instructions.Count > limits.WarnInstructionCount)
                diagnostics.Add(new BuildDiagnostic
                {
                    Severity = "warn",
                    Rule = "instruction-count",
                    Method = label,
                    Message = $"{grafted.Body.Instructions.Count} instructions exceeds the warn threshold " +
                              $"{limits.WarnInstructionCount}.",
                });
        }

        if (helpers.Count > limits.WarnHelperCount)
            diagnostics.Add(new BuildDiagnostic
            {
                Severity = "warn",
                Rule = "helper-count",
                Message = $"{helpers.Count} grafted helpers exceed the warn threshold {limits.WarnHelperCount}.",
            });

        var report = new PatchSizeReport
        {
            EvaluationStackLimit = EvaluationStackSlots,
            HelperCount = helpers.Count,
            MaxInstructionCount = all.Max(entry => entry.Grafted.Body.Instructions.Count),
            MaxLocalCount = all.Max(entry => entry.Grafted.Body.Variables.Count),
            MaxStackSize = all.Max(entry => entry.Grafted.Body.MaxStackSize),
            MaxMethodSlots = all.Max(entry => FrameSlots(entry.Grafted)),
            ChainBounded = true,
        };

        var graftedOf = all.ToDictionary(entry => entry.Implementation, entry => entry.Grafted);
        var memo = new Dictionary<MethodDefinition, ChainCost>();
        var onStack = new HashSet<MethodDefinition>();
        foreach (var root in roots)
        {
            var cost = Walk(root.Implementation, graftedOf, calls, memo, onStack);
            if (!cost.Bounded)
            {
                report.ChainBounded = false;
                diagnostics.Add(new BuildDiagnostic
                {
                    Severity = "warn",
                    Rule = "evaluation-stack",
                    Method = root.Label,
                    Message = "Helper calls are recursive, so the shared evaluation stack usage has no static " +
                              "bound; keep the recursion depth small.",
                });
                continue;
            }
            if (cost.Slots <= report.DeepestChainSlots)
                continue;
            report.DeepestChainSlots = cost.Slots;
            report.DeepestChain = string.Join(" -> ", cost.Chain.Select(Describe));
        }

        // 有递归的入口只是无法静态定界，不该连带把其余入口的度量一起丢掉：
        // DeepestChainSlots 记的始终是**已定界**那部分里最深的一条，阈值照常对它生效。
        var percent = report.DeepestChainSlots * 100.0 / EvaluationStackSlots;
        report.DeepestChainPercent = Math.Round(percent, 2);
        if (percent >= limits.FailEvaluationStackPercent)
            diagnostics.Add(EvaluationStackDiagnostic("fail", report, limits.FailEvaluationStackPercent));
        else if (percent >= limits.WarnEvaluationStackPercent)
            diagnostics.Add(EvaluationStackDiagnostic("warn", report, limits.WarnEvaluationStackPercent));
        return report;
    }

    private static BuildDiagnostic EvaluationStackDiagnostic(string severity, PatchSizeReport report, int threshold) =>
        new()
        {
            Severity = severity,
            Rule = "evaluation-stack",
            Method = report.DeepestChain,
            Message = $"The deepest interpreted call chain needs {report.DeepestChainSlots} of the VM's " +
                      $"{EvaluationStackSlots} shared evaluation stack slots ({report.DeepestChainPercent}%), " +
                      $"at or above the {threshold}% threshold. The stack is shared by the whole chain, so " +
                      "exceeding it throws StackOverflowException at run time.",
        };

    private static void CheckField(List<BuildDiagnostic> diagnostics, string label, string rule, int value)
    {
        if (value <= StackSpaceFieldLimit)
            return;
        diagnostics.Add(new BuildDiagnostic
        {
            Severity = "fail",
            Rule = rule,
            Method = label,
            Message = $"{value} does not fit the 16 bits the StackSpace instruction gives it " +
                      $"(limit {StackSpaceFieldLimit}); it would silently corrupt the operand.",
        });
    }

    private static ChainCost Walk(
        MethodDefinition method,
        IReadOnlyDictionary<MethodDefinition, MethodDefinition> graftedOf,
        IReadOnlyDictionary<MethodDefinition, List<MethodDefinition>> calls,
        Dictionary<MethodDefinition, ChainCost> memo,
        HashSet<MethodDefinition> onStack)
    {
        if (memo.TryGetValue(method, out var cached))
            return cached;
        if (!onStack.Add(method))
            return new ChainCost(0, false, []);

        var self = FrameSlots(graftedOf[method]);
        var best = new ChainCost(self, true, [method]);
        foreach (var callee in calls.GetValueOrDefault(method, []))
        {
            if (!graftedOf.ContainsKey(callee))
                continue;
            var cost = Walk(callee, graftedOf, calls, memo, onStack);
            if (!cost.Bounded)
            {
                best = new ChainCost(0, false, []);
                break;
            }
            if (self + cost.Slots > best.Slots)
                best = new ChainCost(self + cost.Slots, true, [method, .. cost.Chain]);
        }

        onStack.Remove(method);
        if (best.Bounded)
            memo[method] = best;
        return best;
    }

    private static string Describe(MethodDefinition method) =>
        method.DeclaringType.Name + "::" + method.Name;

    private readonly record struct ChainCost(int Slots, bool Bounded, IReadOnlyList<MethodDefinition> Chain);
}

/// <summary>一个补丁方法的实现侧定义与它在 grafted assembly 里的实体。</summary>
internal readonly record struct GraftedMethod(string Label, MethodDefinition Implementation, MethodDefinition Grafted);
