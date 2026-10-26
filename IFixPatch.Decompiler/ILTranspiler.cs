using IFixPatch.Core;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MethodBody = Mono.Cecil.Cil.MethodBody;

namespace IFixPatchDecompiler;

public sealed class TranspileResult
{
    public bool Ok;
    public MethodDefinition? Method;
    public List<string> Problems = [];
}

/// <summary>
/// 把补丁方法的 Instr[] 转成 Cecil 方法体，供 ICSharpCode.Decompiler 出 C#。
///
/// 两个设计要点：
/// · 实例方法发射为「静态方法 + 首参是真实声明类型」。这样 Ldarg 0 是类型正确的普通参数，
///   字段访问能对上真实类型，避免合成类型与真实类型不一致导致的反编译劣化。
/// · 补丁不存局部变量类型（StackSpace 只有个数），所以用带类型的栈模拟推断：
///   在 stloc 处取栈顶类型，多次赋值冲突则退化为 object。
/// </summary>
public sealed class ILTranspiler
{
    private readonly PatchFile _p;
    private readonly OpCodeMap _map;
    private readonly DummyDllSource _src;
    private readonly ModuleDefinition _module;
    private readonly AqnResolver _aqn;
    private readonly TypeReference _object, _int32, _int64, _single, _double, _string, _boolean, _intptr, _type;

    /// <summary>补丁内部方法 id → 合成桩；由 PatchDecompiler 预先声明好，使 Call 可前向引用。</summary>
    public Dictionary<int, MethodDefinition> InternalStubs { get; } = [];

    /// <summary>VM 内静态字段下标 → 合成静态字段（类型来自补丁第 8 节，已知）。</summary>
    public Dictionary<int, FieldDefinition> VmStatics { get; } = [];

    /// <summary>
    /// 一个匿名类/闭包的合成类型。IFix 里这类对象的字段不在原程序集，VM 用「负数字段操作数」
    /// 寻址：<c>Ldfld -(k+1)</c> 取的是<b>栈上那个闭包实例</b>的第 k 个字段，下标按闭包各自从 0 起算。
    /// </summary>
    public sealed class StoreyDef
    {
        public int Index;
        public TypeDefinition Type = null!;
        public MethodDefinition Ctor = null!;
        public List<FieldDefinition> Fields = [];
        /// <summary>补丁元数据已给出确切类型（值类型）的槽，方法体推断不得覆盖。</summary>
        public bool[] Locked = [];
        /// <summary>从 Stfld 现场观测到的字段类型；null = 尚未观测到。</summary>
        public TypeReference?[] Observed = [];
    }

    /// <summary>匿名类下标 → 合成类型。由 PatchDecompiler 在第一遍声明。</summary>
    public Dictionary<int, StoreyDef> Storeys { get; } = [];

    /// <summary>补丁内部方法 id → 它所属的闭包下标（该方法的 arg0 就是这个闭包实例）。</summary>
    public Dictionary<int, int> MethodStorey { get; } = [];

    /// <summary>接收者推不出闭包时的兜底 holder（由 PatchDecompiler 声明），避免与 VM 静态字段混淆。</summary>
    public TypeDefinition? UnresolvedStoreyHolder { get; set; }

    private readonly List<FieldDefinition> _unresolvedStoreyFields = [];

    private FieldDefinition? UnresolvedField(int idx)
    {
        if (UnresolvedStoreyHolder == null || idx < 0 || idx > 255) return null;
        while (_unresolvedStoreyFields.Count <= idx)
        {
            var fd = new FieldDefinition($"f{_unresolvedStoreyFields.Count}",
                FieldAttributes.Public | FieldAttributes.Static, _object);
            UnresolvedStoreyHolder.Fields.Add(fd);
            _unresolvedStoreyFields.Add(fd);
        }
        return _unresolvedStoreyFields[idx];
    }

    /// <summary>
    /// 补丁新增字段（isNew 且原程序集里不存在）的访问器。运行时这些值不在真实字段里，
    /// 而在 NewFieldInfo 的旁表（Dictionary&lt;对象key, Dictionary&lt;字段名, 值&gt;&gt;）里，
    /// 所以呈现为访问器方法而不是静态字段，才不误导读者。
    /// </summary>
    public Dictionary<int, NewFieldAccessors> NewFields { get; } = [];

    public sealed class NewFieldAccessors
    {
        public MethodDefinition GetInstance = null!, SetInstance = null!;
        public MethodDefinition GetStatic = null!, SetStatic = null!;
    }

    public ILTranspiler(PatchFile p, OpCodeMap map, DummyDllSource src, ModuleDefinition module)
    {
        _p = p; _map = map; _src = src; _module = module;
        _aqn = new AqnResolver(src, module);
        var ts = module.TypeSystem;
        _object = ts.Object; _int32 = ts.Int32; _int64 = ts.Int64;
        _single = ts.Single; _double = ts.Double; _string = ts.String;
        _boolean = ts.Boolean; _intptr = ts.IntPtr;
        _type = Import("System.Type, mscorlib") ?? ts.Object;
    }

    // ---------- 引用解析 ----------

    private TypeReference? Import(string aqn) => _aqn.Resolve(aqn);

    private TypeReference TypeOf(int externTypeId)
        => (externTypeId >= 0 && externTypeId < _p.ExternTypes.Count
            ? Import(_p.ExternTypes[externTypeId]) : null) ?? _object;

    private MethodReference? MethodOf(int externMethodId)
    {
        if (externMethodId < 0 || externMethodId >= _p.ExternMethods.Count) return null;
        var sig = _p.ExternMethods[externMethodId];
        var aqn = sig.DeclaringTypeId >= 0 && sig.DeclaringTypeId < _p.ExternTypes.Count
            ? _p.ExternTypes[sig.DeclaringTypeId] : null;
        if (aqn == null) return null;
        // 按参数类型选重载：补丁存了参数类型，只按个数匹配会选错（如 List<T> 的两个单参 ctor）
        var declRef = Import(aqn);
        // 递归替换：参数类型可能是 IEnumerable<T> 这类嵌套形态，只替换顶层不够。
        // 此处只有类级实参可用（方法级实参要等重载选定后才知道），取不到就保留原样按开放形态比。
        string Render(TypeReference t)
            => (Concretize(t, declRef as GenericInstanceType, null) ?? t).FullName;
        var expected = sig.Params
            .Select(pr => pr.IsGeneric ? null
                : (pr.TypeId >= 0 && pr.TypeId < _p.ExternTypes.Count
                    ? Import(_p.ExternTypes[pr.TypeId])?.FullName : null))
            .ToList();
        var md = _src.FindMethodByParamTypes(aqn, sig.Name, expected, Render)
                 ?? _src.FindMethod(aqn, sig.Name, sig.Params.Count);
        if (md == null) return null;

        try
        {
            MethodReference imported = _module.ImportReference(md);
            // 声明类型是构造泛型（如 Dictionary<string,int>）时，必须把方法引用重建到
            // GenericInstanceType 上；否则引用挂在开放定义上，反编译会漏出 TKey/!0。
            if (Import(aqn) is GenericInstanceType git)
                imported = MakeHostInstanceGeneric(imported, git);

            // 泛型方法实例（如 EventExtension.Call<T>）：必须补上方法级类型实参，
            // 否则引用停在开放定义上，签名与调用点不符，反编译会报栈失衡。
            if (sig.IsGenericInstance && sig.GenericArgTypeIds.Count > 0)
            {
                var gim = new GenericInstanceMethod(imported);
                foreach (var argId in sig.GenericArgTypeIds) gim.GenericArguments.Add(TypeOf(argId));
                imported = gim;
            }
            return imported;
        }
        catch { return null; }
    }

    /// <summary>把成员引用的宿主换成构造泛型实例（Cecil 的标准做法）。</summary>
    private static MethodReference MakeHostInstanceGeneric(MethodReference self, GenericInstanceType host)
    {
        var r = new MethodReference(self.Name, self.ReturnType, host)
        {
            HasThis = self.HasThis,
            ExplicitThis = self.ExplicitThis,
            CallingConvention = self.CallingConvention,
        };
        foreach (var p in self.Parameters)
            r.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));
        foreach (var gp in self.GenericParameters)
            r.GenericParameters.Add(new GenericParameter(gp.Name, r));
        return r;
    }

    private FieldReference? FieldOf(int fieldId)
    {
        if (fieldId < 0 || fieldId >= _p.Fields.Count) return null;
        var f = _p.Fields[fieldId];
        var aqn = f.DeclaringTypeId >= 0 && f.DeclaringTypeId < _p.ExternTypes.Count
            ? _p.ExternTypes[f.DeclaringTypeId] : null;
        if (aqn == null) return null;
        var fd = _src.FindField(aqn, f.Name);
        if (fd == null) return null;
        try
        {
            var imported = _module.ImportReference(fd);
            if (Import(aqn) is GenericInstanceType git)
                imported = new FieldReference(imported.Name, imported.FieldType, git);
            return imported;
        }
        catch { return null; }
    }

    // ---------- 指令解码 ----------

    private sealed class Decoded
    {
        public int Index;
        public string Mnemonic = "";
        public int Operand;
        public int Slots = 1;
        public long InlineI8;
        public double InlineR8;
        public List<int> SwitchTargets = [];
    }

    private List<Decoded> Decode(IFixMethod m, List<string> problems)
    {
        var outp = new List<Decoded>();
        int i = 0;
        while (i < m.Code.Count)
        {
            var ins = m.Code[i];
            if (!_map.TryName(ins.Code, out var mn))
            {
                problems.Add($"[{i}] 未知 opcode 值 {ins.Code}");
                return outp;
            }
            var d = new Decoded { Index = i, Mnemonic = mn, Operand = ins.Operand };
            switch (mn)
            {
                case "Ldc_I8":
                case "Ldc_R8":
                    if (i + 1 < m.Code.Count)
                    {
                        var s = m.Code[i + 1];
                        long bits = (uint)s.Code | ((long)s.Operand << 32);
                        d.InlineI8 = bits;
                        d.InlineR8 = BitConverter.Int64BitsToDouble(bits);
                        d.Slots = 2;
                    }
                    break;
                case "Switch":
                    {
                        int n = ins.Operand, slots = (n + 1) >> 1;
                        var t = new List<int>();
                        for (int s = 0; s < slots && i + 1 + s < m.Code.Count; s++)
                        {
                            var sl = m.Code[i + 1 + s];
                            t.Add(sl.Code);
                            if (t.Count < n) t.Add(sl.Operand);
                        }
                        d.SwitchTargets = [.. t.Select(x => i + x)];
                        d.Slots = 1 + slots;
                        break;
                    }
            }
            outp.Add(d);
            i += d.Slots;
        }
        return outp;
    }

    // ---------- 主转译 ----------

    /// <summary>
    /// 只声明修复列表方法的签名并登记到桩表，不填方法体。
    /// 必须与填体分成两遍：修复列表方法之间也会互相调用（如 method#174 调 method#175），
    /// 若边声明边填体，先处理的那个引用后面还没声明的方法就会失败。
    /// </summary>
    public MethodDefinition? DeclareFix(FixEntry fx, TypeDefinition container, List<string> problems)
    {
        var res = new TranspileResult();
        var m = _p.Methods[fx.MethodId];
        var sig = fx.Method;

        var declAqn = sig.DeclaringTypeId >= 0 && sig.DeclaringTypeId < _p.ExternTypes.Count
            ? _p.ExternTypes[sig.DeclaringTypeId] : null;
        if (declAqn == null) { problems.Add("声明类型无效"); return null; }

        var declType = Import(declAqn);
        var realMethod = _src.FindMethod(declAqn, sig.Name, sig.Params.Count);
        var returnType = realMethod != null
            ? (_module.ImportReference(realMethod.ReturnType) ?? _object)
            : _object;
        if (realMethod == null) res.Problems.Add("目标程序集里找不到原方法，返回类型退化为 object");

        // 统一发射为静态方法；实例方法把 this 显式化为首参，保证类型正确
        bool hasThis = realMethod?.HasThis ?? true;
        var name = $"{SanitizeName(sig.Name)}__m{fx.MethodId}";
        var method = new MethodDefinition(name,
            MethodAttributes.Public | MethodAttributes.Static, returnType);

        if (hasThis && declType != null)
            method.Parameters.Add(new ParameterDefinition("self", ParameterAttributes.None, declType));
        for (int i = 0; i < sig.Params.Count; i++)
        {
            var pt = sig.Params[i].IsGeneric ? _object : TypeOf(sig.Params[i].TypeId);
            var pn = realMethod != null && i < realMethod.Parameters.Count
                ? realMethod.Parameters[i].Name : $"arg{i}";
            method.Parameters.Add(new ParameterDefinition(pn, ParameterAttributes.None, pt));
        }

        container.Methods.Add(method);
        InternalStubs[fx.MethodId] = method;
        problems.AddRange(res.Problems);
        return method;
    }

    /// <summary>为已声明的内部方法桩填充方法体（签名由 InternalSignatures 推出）。</summary>
    public TranspileResult TranspileInternal(IFixMethod m, MethodDefinition stub)
    {
        var res = new TranspileResult();
        EmitBody(m, stub, res.Problems);
        res.Method = stub;
        res.Ok = true;
        return res;
    }

    /// <summary>解码 → 推断局部类型 → 发射指令 → 回填分支 → 建异常块。</summary>
    private void EmitBody(IFixMethod m, MethodDefinition method, List<string> problems)
    {
        var body = method.Body;
        body.Instructions.Clear();
        body.Variables.Clear();
        body.ExceptionHandlers.Clear();
        var il = body.GetILProcessor();

        var decoded = Decode(m, problems);
        if (decoded.Count == 0) { problems.Add("解码失败"); il.Append(Instruction.Create(OpCodes.Ret)); return; }
        // StackSpace 给出局部变量个数；类型靠后面的栈模拟推断
        int localCount = 0;
        if (decoded[0].Mnemonic == "StackSpace") localCount = decoded[0].Operand >> 16;
        var localTypes = new TypeReference?[localCount];

        // 第一遍：栈模拟。除了推断局部变量类型，还要定位每个实例形式负数字段操作数的接收者
        // 属于哪个闭包——发射时要靠它选对 FieldDefinition。
        var simObs = new SimObservations();
        SimulateForLocals(decoded, method, localTypes, simObs);
        _siteStorey = simObs.FieldSiteStorey;
        _ldftnRewrite = PlanDelegateTargets(decoded);
        for (int i = 0; i < localCount; i++)
            body.Variables.Add(new VariableDefinition(localTypes[i] ?? _object));

        // 第二遍：发射指令
        var byIndex = new Dictionary<int, Instruction>();
        var pending = new List<(Instruction Ins, int TargetIfix)>();
        var pendingSwitch = new List<(Instruction Ins, List<int> Targets)>();

        foreach (var d in decoded)
        {
            if (d.Mnemonic == "StackSpace") continue;
            var emitted = Emit(il, d, method, body, problems, pending, pendingSwitch);
            if (emitted != null) byIndex[d.Index] = emitted;
        }

        // 分支回填
        foreach (var (ins, target) in pending)
            ins.Operand = byIndex.TryGetValue(target, out var t) ? t : body.Instructions.Last();
        foreach (var (ins, targets) in pendingSwitch)
            ins.Operand = targets.Select(t => byIndex.TryGetValue(t, out var x) ? x : body.Instructions.Last()).ToArray();

        // 异常处理块
        foreach (var eh in m.Handlers)
        {
            Instruction? At(int idx) => byIndex.TryGetValue(idx, out var x) ? x : null;
            var tryStart = At(eh.TryStart); var tryEnd = At(eh.TryEnd);
            var hStart = At(eh.HandlerStart); var hEnd = At(eh.HandlerEnd);
            if (tryStart == null || tryEnd == null || hStart == null)
            {
                problems.Add($"异常块边界越界 try[{eh.TryStart}..{eh.TryEnd}) handler[{eh.HandlerStart}..{eh.HandlerEnd})");
                continue;
            }
            var h = new ExceptionHandler(eh.HandlerType switch
            {
                0 => ExceptionHandlerType.Catch,
                1 => ExceptionHandlerType.Filter,
                2 => ExceptionHandlerType.Finally,
                _ => ExceptionHandlerType.Fault,
            })
            {
                TryStart = tryStart,
                TryEnd = tryEnd,
                HandlerStart = hStart,
                HandlerEnd = hEnd,
            };
            if (h.HandlerType == ExceptionHandlerType.Catch)
                h.CatchType = eh.CatchTypeId < 0 ? _object : TypeOf(eh.CatchTypeId);
            body.ExceptionHandlers.Add(h);
        }

        body.InitLocals = localCount > 0;
        if (body.Instructions.Count == 0 || body.Instructions[^1].OpCode != OpCodes.Ret)
            il.Append(Instruction.Create(OpCodes.Ret));
    }
    /// <summary>当前方法内，实例形式负数字段操作数的接收者闭包（EmitBody 每次重置）。</summary>
    private Dictionary<int, int> _siteStorey = [];

    /// <summary>当前方法内，应改写成 Ldftn 的 Ldc_I4 站点：指令下标 → 委托目标方法。</summary>
    private Dictionary<int, MethodDefinition> _ldftnRewrite = [];

    /// <summary>
    /// IFix 给「补丁内部方法」做委托时发的是 <c>Ldc_I4 &lt;methodId&gt;</c> 塞进 ctor 的 IntPtr 槽，
    /// 直译出来就是 <c>new Action(storey, (IntPtr)28)</c> 这种看不出目标的形态。这里把它还原成
    /// <c>Ldftn &lt;目标桩&gt;</c>，读者就能直接看到委托指向哪个方法（栈效果一致，都是压一个 native int）。
    /// </summary>
    private Dictionary<int, MethodDefinition> PlanDelegateTargets(List<Decoded> decoded)
    {
        var plan = new Dictionary<int, MethodDefinition>();
        for (int k = 1; k < decoded.Count; k++)
        {
            if (decoded[k].Mnemonic != "Newobj" || decoded[k - 1].Mnemonic != "Ldc_I4") continue;
            if (!IsDelegateCtor(decoded[k].Operand & 0xFFFF)) continue;
            if (InternalStubs.TryGetValue(decoded[k - 1].Operand, out var target))
                plan[decoded[k - 1].Index] = target;
        }
        return plan;
    }

    /// <summary>内部方法是否有返回值；由 PatchDecompiler 注入（来自 InternalSignatures）。</summary>
    public Func<int, bool> InternalReturnsValue { get; set; } = _ => false;

    /// <summary>
    /// 是否按匿名类的接口槽契约播种签名，默认关闭。
    /// 接口契约可能比方法体推断更宽，例如 IEnumerator.Current 的 object 返回类型。
    /// 此选项不包含 object 虚方法槽映射。
    /// </summary>
    public bool SeedAnonStoreys { get; set; } = false;

    /// <summary>
    /// 对内部方法的参数/返回类型做不动点迭代。参数类型来自各调用点的实参类型（多点冲突退化为
    /// object），返回类型来自被调方自己 Ret 处的栈顶类型。补丁不存这些签名，只能这样反推。
    /// </summary>
    public (TypeReference?[] Ret, TypeReference?[][] Args) AnalyzeInternalTypes(
        InternalSignatures sigs, Func<int, TypeReference?[]?> knownParams, int rounds = 4)
    {
        int n = _p.Methods.Count;

        // 先按委托契约校正元数：ArgCount 取自 Call 现场的 argc 与 Ldarg 最大下标，
        // 方法体未读取某个参数时可能低估（空方法体直接算成 0 个参数）。
        // IFix 包装委托时恒以 arg0 承接 target 对象，故真实元数 = Invoke 参数数 + 1。
        FixDelegateArities(sigs);

        var ret = new TypeReference?[n];
        var args = new TypeReference?[n][];
        for (int i = 0; i < n; i++) args[i] = new TypeReference?[sigs.ArgCount[i]];

        // 契约锁：接口/委托播种是声明签名（权威），方法体推断只能填补未锁定的槽。
        // 否则 Boolean 与 Int32 这类「IL 栈上同为 I4」的差异会被误判为冲突而退化成 object。
        _retLocked = new bool[n];
        _argLocked = new bool[n][];
        for (int i = 0; i < n; i++) _argLocked[i] = new bool[sigs.ArgCount[i]];

        var decodedCache = DecodeAll();

        // 播种：闭包方法没有 Call 调用点（经 wrapper 走委托），但 IFix 对闭包发射的是
        // `Ldc_I4 <methodId>` 紧跟 `Newobj <委托>::.ctor`，于是委托的 Invoke 签名
        // 就是该方法的精确签名。实测 31 处该模式里 30 处是委托 ctor 且目标槽有效。
        SeedFromDelegateSites(decodedCache, sigs, knownParams, ret, args);
        if (SeedAnonStoreys) SeedFromAnonStoreys(sigs, knownParams, ret, args);

        // 播种：闭包方法的 arg0 就是该闭包实例。这一步是「负数字段操作数按实例寻址」得以成立的前提
        // ——arg0 有了确切的闭包类型，方法体里的 Ldfld -(k+1) 才能定位到具体是哪个闭包的第 k 个字段。
        foreach (var (mid, si) in MethodStorey)
        {
            if (mid < 0 || mid >= n || knownParams(mid) != null) continue;
            if (args[mid].Length == 0) continue;
            var st = StoreyType(si);
            if (st == null) continue;
            args[mid][0] = st;
            _argLocked[mid][0] = true;
        }

        for (int round = 0; round < rounds; round++)
        {
            bool changed = false;
            for (int id = 0; id < n; id++)
            {
                var dec = decodedCache[id];
                if (dec == null || dec.Count == 0) continue;

                var ps = knownParams(id) ?? args[id];
                int localCount = dec[0].Mnemonic == "StackSpace" ? dec[0].Operand >> 16 : 0;
                var obs = new SimObservations();
                Simulate(dec, ps, sigs.ReturnsValue[id], new TypeReference?[localCount], obs,
                    t => t >= 0 && t < n ? ret[t] : null);

                // 返回类型：本方法各 Ret 的栈顶类型
                if (!_retLocked[id])
                    foreach (var rt in obs.RetTypes)
                        if (Merge(ref ret[id], rt)) changed = true;

                // 参数类型：本方法内各内部调用点的实参类型，写给被调方
                foreach (var (target, argTypes) in obs.InternalCalls)
                {
                    if (target < 0 || target >= n || knownParams(target) != null) continue;
                    var slot = args[target];
                    for (int k = 0; k < Math.Min(slot.Length, argTypes.Count); k++)
                        if (!_argLocked[target][k] && Merge(ref slot[k], argTypes[k])) changed = true;
                }

                // 闭包字段类型：补丁只存「基元/引用/值类型 id」三档，引用类型一律退化成 object。
                // 真正的类型只能从赋值现场反推，故并入同一个不动点——闭包字段定型后又会让
                // 读取它的那些局部变量定型，互相促进。
                // 委托目标对象 → 目标方法的 arg0。闭包类的 arg0 已由 MethodStorey 锁定，
                // 这里补的是「目标是原类型实例」的那批（如 new Action(self, m35)）。
                foreach (var (mid, tt) in obs.DelegateTargets)
                {
                    if (mid < 0 || mid >= n || knownParams(mid) != null) continue;
                    if (args[mid].Length == 0 || _argLocked[mid][0]) continue;
                    if (Merge(ref args[mid][0], tt)) changed = true;
                }

                foreach (var (si, fi, vt) in obs.StoreyStores)
                    if (RefineStoreyField(si, fi, vt)) changed = true;
                foreach (var (vi, si) in obs.VmStaticStoreyStores)
                    if (RefineVmStatic(vi, si)) changed = true;
            }
            if (!changed) break;
        }

        // 闭包 ctor 的形参类型：Newanon 现场按伪调用记进了 args[CtorId]，这里回填到合成 ctor 上，
        // 免得 Newobj 处出现 int→object 的隐式错配（ILSpy 会为此吐 "Expected O, but got I4"）。
        foreach (var sd in Storeys.Values)
        {
            int cid = _p.AnonStoreys[sd.Index].CtorId;
            if (cid < 0 || cid >= n) continue;
            for (int k = 0; k < sd.Ctor.Parameters.Count && k + 1 < args[cid].Length; k++)
                if (args[cid][k + 1] is { } t) sd.Ctor.Parameters[k].ParameterType = t;
        }
        return (ret, args);
    }

    /// <summary>
    /// 播种：匿名类/闭包的接口方法可能没有 Call 调用点（由 VM 按槽位派发），
    /// 接口槽第 k 个 methodId 对应接口的第 k 个已声明实例方法。
    /// </summary>
    private void SeedFromAnonStoreys(InternalSignatures sigs,
        Func<int, TypeReference?[]?> knownParams, TypeReference?[] ret, TypeReference?[][] args)
    {
        foreach (var storey in _p.AnonStoreys)
        {
            // 接口槽
            foreach (var itf in storey.Interfaces)
            {
                var aqn = itf.TypeId >= 0 && itf.TypeId < _p.ExternTypes.Count
                    ? _p.ExternTypes[itf.TypeId] : null;
                if (aqn == null) continue;
                var decls = _src.FindDeclaredInstanceMethods(aqn);
                if (decls == null) continue;

                var gi = Import(aqn) as GenericInstanceType;
                for (int k = 0; k < Math.Min(decls.Count, itf.MethodIds.Count); k++)
                    SeedOne(sigs, knownParams, ret, args, itf.MethodIds[k], decls[k], gi);
            }
        }
    }

    /// <summary>把一个已知签名的方法（接口方法/虚方法）播种给对应的补丁内部方法。</summary>
    private void SeedOne(InternalSignatures sigs, Func<int, TypeReference?[]?> knownParams,
        TypeReference?[] ret, TypeReference?[][] args, int target,
        MethodDefinition model, GenericInstanceType? gi)
    {
        if (target < 0 || target >= ret.Length || knownParams(target) != null) return;

        TypeReference? Subst(TypeReference t) => ImportOrNull(Concretize(t, gi, null));

        try
        {
            if (model.ReturnType.FullName != "System.Void")
                Merge(ref ret[target], Subst(model.ReturnType));

            // 首参是实现该方法的闭包对象（this），其后才是接口/虚方法的参数。
            // ArgCount 由 Ldarg 最大下标推出，方法未用到全部参数时会低估；此时 offset 会从
            // 应有的 1 变成 0，导致参数整体错位一格、把错误类型播种出去。匿名类方法必为
            // 实例形式，故只在 offset 恰好为 1 时采信。
            var slot = args[target];
            int offset = sigs.ArgCount[target] - model.Parameters.Count;
            if (offset != 1) return;
            for (int i = 0; i < model.Parameters.Count; i++)
            {
                int pos = offset + i;
                if (pos >= 0 && pos < slot.Length)
                {
                    var pt = Subst(model.Parameters[i].ParameterType);
                    if (pt == null) continue;
                    slot[pos] = pt;
                    _argLocked[target][pos] = true;
                }
            }
        }
        catch { /* 引用导入失败就跳过这一条播种 */ }
    }

    private bool[] _retLocked = [];
    private bool[][] _argLocked = [];

    /// <summary>
    /// 用委托的 Invoke 契约把被低估的内部方法元数补齐（只增不减）。没有这一步，像
    /// <c>void m249()</c> 这种空实现会被当成 0 参，做委托目标时签名对不上，
    /// 反编译只能退化成函数指针强转而不是方法组。
    /// </summary>
    private void FixDelegateArities(InternalSignatures sigs)
    {
        foreach (var code in DecodeAll())
        {
            if (code == null) continue;
            for (int k = 1; k < code.Count; k++)
            {
                if (code[k].Mnemonic != "Newobj" || code[k - 1].Mnemonic != "Ldc_I4") continue;
                int target = code[k - 1].Operand;
                if (target < 0 || target >= sigs.ArgCount.Length) continue;
                if (!IsDelegateCtor(code[k].Operand & 0xFFFF)) continue;

                var ctor = MethodOf(code[k].Operand & 0xFFFF);
                MethodDefinition? invoke = null;
                try { invoke = ctor?.DeclaringType?.Resolve()?.Methods.FirstOrDefault(x => x.Name == "Invoke"); }
                catch { }
                if (invoke == null) continue;
                sigs.ArgCount[target] = Math.Max(sigs.ArgCount[target], invoke.Parameters.Count + 1);
            }
        }
    }

    private void SeedFromDelegateSites(List<Decoded>?[] decodedCache, InternalSignatures sigs,
        Func<int, TypeReference?[]?> knownParams, TypeReference?[] ret, TypeReference?[][] args)
    {
        for (int id = 0; id < decodedCache.Length; id++)
        {
            var dec = decodedCache[id];
            if (dec == null) continue;
            for (int k = 0; k + 1 < dec.Count; k++)
            {
                if (dec[k].Mnemonic != "Ldc_I4" || dec[k + 1].Mnemonic != "Newobj") continue;
                int target = dec[k].Operand;
                if (target < 0 || target >= ret.Length || knownParams(target) != null) continue;

                var ctor = MethodOf(dec[k + 1].Operand & 0xFFFF);
                var declType = ctor?.DeclaringType;
                if (declType == null) continue;

                // 只认委托：其 ctor 的声明类型派生自 MulticastDelegate/Delegate
                TypeDefinition? td = null;
                try { td = declType.Resolve(); } catch { }
                if (td?.BaseType == null) continue;
                if (td.BaseType.FullName is not ("System.MulticastDelegate" or "System.Delegate")) continue;

                var invoke = td.Methods.FirstOrDefault(x => x.Name == "Invoke");
                if (invoke == null) continue;

                // 委托可能是构造泛型（Action<int>），需按实参替换 Invoke 签名里的类型形参
                TypeReference? Subst(TypeReference t)
                    => ImportOrNull(Concretize(t, declType as GenericInstanceType, null));

                if (invoke.ReturnType.FullName != "System.Void" && Subst(invoke.ReturnType) is { } rt0)
                { ret[target] = rt0; _retLocked[target] = true; }

                // 闭包方法的首参是闭包对象（委托的 target），其后才是 Invoke 的参数
                var slot = args[target];
                int offset = sigs.ArgCount[target] - invoke.Parameters.Count;
                if (offset is < 0 or > 1) continue;
                for (int i = 0; i < invoke.Parameters.Count; i++)
                {
                    int pos = offset + i;
                    if (pos >= 0 && pos < slot.Length)
                    {
                        var pt = Subst(invoke.Parameters[i].ParameterType);
                        if (pt == null) continue;
                        slot[pos] = pt;
                        _argLocked[target][pos] = true;
                    }
                }
            }
        }
    }

    /// <summary>求值栈上的类型族。ECMA-335 里小于 4 字节的整数一律扩展为 int32、浮点扩展为 F，
    /// 故 Boolean/Char/枚举/Int32 等在栈上无从区分，把它们判成「冲突」再退化成 object 是过度保守的。</summary>
    private enum StackKind { Other, I4, I8, F }

    private static StackKind KindOf(TypeReference? t)
    {
        if (t == null) return StackKind.Other;
        switch (t.MetadataType)
        {
            case MetadataType.Boolean:
            case MetadataType.Char:
            case MetadataType.SByte:
            case MetadataType.Byte:
            case MetadataType.Int16:
            case MetadataType.UInt16:
            case MetadataType.Int32:
            case MetadataType.UInt32:
                return StackKind.I4;
            case MetadataType.Int64:
            case MetadataType.UInt64:
                return StackKind.I8;
            case MetadataType.Single:
            case MetadataType.Double:
                return StackKind.F;
            case MetadataType.ValueType:
                // 枚举按底层类型归族：value__ 是它唯一的实例字段
                try
                {
                    if (t.Resolve() is { IsEnum: true } td)
                    {
                        var underlying = td.Fields.FirstOrDefault(f => !f.IsStatic)?.FieldType;
                        return underlying == null ? StackKind.Other : KindOf(underlying);
                    }
                }
                catch { /* 解析不到就当普通值类型 */ }
                return StackKind.Other;
            default:
                return StackKind.Other;
        }
    }

    /// <summary>
    /// 同族类型的合并。Int32/Int64/Double 是各族的「无类型字面量」形态（Ldc_I4 压出来的就是它），
    /// 信息量最低，故让位于同族的具体类型；两边都具体且不同才退回族的规范形态。
    /// 不同族返回 null，交由调用方按原来的规则退化为 object。
    /// </summary>
    private TypeReference? UnifySameKind(TypeReference a, TypeReference b)
    {
        var ka = KindOf(a);
        if (ka == StackKind.Other || ka != KindOf(b)) return null;
        var canon = ka switch { StackKind.I4 => _int32, StackKind.I8 => _int64, _ => _double };
        if (a.FullName == canon.FullName) return b;
        if (b.FullName == canon.FullName) return a;
        return canon;
    }

    /// <summary>
    /// 两个引用类型的最近公共基类。同一个访问器桩会被不同子类调用（<c>m4</c> 同时收到
    /// CustomMissionTask 和 TotalRechargeTask），直接判冲突退化成 object 就丢掉了它们真正的
    /// 声明类型 BaseTask。接口不参与——多重实现下没有唯一解。
    /// </summary>
    private TypeReference? CommonBase(TypeReference a, TypeReference b)
    {
        if (a.IsValueType || b.IsValueType || a.IsArray || b.IsArray) return null;
        try
        {
            var chain = new List<TypeReference>();
            for (TypeReference? t = a; t != null;)
            {
                chain.Add(t);
                var td = t.Resolve();
                if (td?.BaseType == null) break;
                t = td.BaseType;
            }
            var names = chain.Select(x => x.FullName).ToHashSet();
            for (TypeReference? t = b; t != null;)
            {
                if (names.Contains(t.FullName))
                    return t.FullName == _object.FullName ? null : ImportOrNull(t);
                var td = t.Resolve();
                if (td?.BaseType == null) break;
                t = td.BaseType;
            }
        }
        catch { /* 解析不到基类链就放弃 */ }
        return null;
    }

    /// <summary>合并一次观测：首次直接采用；同族收敛到族内类型，引用类型退到最近公共基类，
    /// 都不成立才退化为 object（返回是否变化）。</summary>
    private bool Merge(ref TypeReference? slot, TypeReference? seen)
    {
        if (seen == null) return false;
        // 栈上的 object 绝大多数只是「这一侧还没推出来」，不是真的观测到了 object。
        // 让它参与合并会把已经推出的具体类型压回 object，而 object 一旦落定就再也升不回来
        // ——不动点的轮次顺序会决定结果，先观测到的未定型来源直接毁掉整个槽。
        if (seen.FullName == _object.FullName) return false;
        if (slot == null) { slot = seen; return true; }
        if (slot.FullName == seen.FullName) return false;
        if (slot.FullName == _object.FullName) return false;
        var unified = UnifySameKind(slot, seen) ?? CommonBase(slot, seen);
        if (unified != null)
        {
            if (unified.FullName == slot.FullName) return false;
            slot = unified;
            return true;
        }
        slot = _object;
        return true;
    }

    private TypeReference? VmStaticType(int idx)
        => VmStatics.TryGetValue(idx, out var f) ? f.FieldType : null;

    // ---------- 泛型具体化 ----------

    /// <summary>
    /// 把签名里的泛型形参按调用现场的实参具体化：类级形参（<c>!N</c>）取自声明类型的
    /// <see cref="GenericInstanceType"/>，方法级形参（<c>!!N</c>）取自 <see cref="GenericInstanceMethod"/>。
    /// Cecil 的 <c>GenericParameter.Type</c> 区分这两类，只看 <c>Position</c> 会把 <c>!!0</c>
    /// 错当成类的第一个类型实参。
    ///
    /// 只用于**类型推断**。IL 发射必须保留开放形态——CIL 本来就把成员引用挂在构造泛型宿主上、
    /// 签名里留 <c>!N</c>，改成具体类型反而不合法。但把 <c>!1</c> 当作某个局部变量的类型毫无意义，
    /// 推断侧必须具体化，取不到就返回 null 退化为 object。
    /// </summary>
    private static TypeReference? Concretize(TypeReference? t, GenericInstanceType? classArgs,
        GenericInstanceMethod? methodArgs)
    {
        switch (t)
        {
            case null:
                return null;
            case GenericParameter gp:
                {
                    var src = gp.Type == GenericParameterType.Method
                        ? methodArgs?.GenericArguments
                        : classArgs?.GenericArguments;
                    return src != null && gp.Position < src.Count ? src[gp.Position] : null;
                }
            case GenericInstanceType git:
                {
                    var r = new GenericInstanceType(git.ElementType);
                    foreach (var a in git.GenericArguments)
                    {
                        var c = Concretize(a, classArgs, methodArgs);
                        if (c == null) return null;
                        r.GenericArguments.Add(c);
                    }
                    return r;
                }
            case ArrayType at:
                {
                    var el = Concretize(at.ElementType, classArgs, methodArgs);
                    return el == null ? null : new ArrayType(el, at.Rank);
                }
            case ByReferenceType brt:
                {
                    var el = Concretize(brt.ElementType, classArgs, methodArgs);
                    return el == null ? null : new ByReferenceType(el);
                }
            case PointerType pt:
                {
                    var el = Concretize(pt.ElementType, classArgs, methodArgs);
                    return el == null ? null : new PointerType(el);
                }
            default:
                return t;
        }
    }

    /// <summary>按某个方法引用的宿主/方法实参具体化其签名里的类型。</summary>
    private TypeReference? Concretize(TypeReference? t, MethodReference? mr)
        => Concretize(t, mr?.DeclaringType as GenericInstanceType, mr as GenericInstanceMethod);

    /// <summary>
    /// 找出「补丁里带方法体、却没有任何引用点」的方法槽。引用点共六类：内部调用、委托创建点、
    /// 修复列表、闭包 ctor / 接口实现槽 / vtable 槽、以及字段表与 VM 静态字段表里的 cctor。
    ///
    /// 这类槽是 IFix 编译时连带序列化进来的死代码（其创建路径本身没被打补丁）。它们的签名
    /// 无法从现有引用点推断，报告中与有引用的方法签名推断失败分别统计。
    /// </summary>
    public List<int> FindOrphanMethods()
    {
        var referenced = new HashSet<int>();

        foreach (var fx in _p.FixList) referenced.Add(fx.MethodId);
        foreach (var a in _p.AnonStoreys)
        {
            referenced.Add(a.CtorId);
            foreach (var itf in a.Interfaces) foreach (var m in itf.MethodIds) referenced.Add(m);
            foreach (var m in a.VTable) referenced.Add(m);
        }
        foreach (var sf in _p.StaticFields) referenced.Add(sf.CctorId);
        foreach (var f in _p.Fields) if (f.IsNew) referenced.Add(f.CctorId);

        foreach (var code in DecodeAll())
        {
            if (code == null) continue;
            for (int k = 0; k < code.Count; k++)
            {
                var d = code[k];
                if (d.Mnemonic is "Call" or "Callvirt" or "Callvirtvirt")
                    referenced.Add(d.Operand & 0xFFFF);
                else if (d.Mnemonic == "Newobj" && k > 0 && code[k - 1].Mnemonic == "Ldc_I4"
                         && IsDelegateCtor(d.Operand & 0xFFFF))
                    referenced.Add(code[k - 1].Operand);
            }
        }

        var orphans = new List<int>();
        for (int id = 0; id < _p.Methods.Count; id++)
            if (!_p.Methods[id].IsEmpty && !referenced.Contains(id)) orphans.Add(id);
        return orphans;
    }

    /// <summary>
    /// 按转译真正走的那条路径（<see cref="MethodOf"/>，含按参数类型选重载）统计外部方法解析率。
    /// 概览里那份「返回类型解析率」是不依赖 --decompile 的预检，只按「名字 + 参数个数」探测，
    /// 同名同参数个数的重载一律记成歧义——那是探针的口径，不代表转译没解析出来。
    /// </summary>
    public (int Resolved, int Total, List<string> Failures) ProbeExternResolution()
    {
        int ok = 0;
        var failures = new List<string>();
        for (int i = 0; i < _p.ExternMethods.Count; i++)
        {
            if (MethodOf(i) != null) { ok++; continue; }
            var sig = _p.ExternMethods[i];
            var aqn = sig.DeclaringTypeId >= 0 && sig.DeclaringTypeId < _p.ExternTypes.Count
                ? DumpCsIndex.NormalizeAqn(_p.ExternTypes[sig.DeclaringTypeId]) : "?";
            if (failures.Count < 8) failures.Add($"extern#{i} {aqn}::{sig.Name}/{sig.Params.Count}");
        }
        return (ok, _p.ExternMethods.Count, failures);
    }

    /// <summary>
    /// 推断侧的字段类型：字段引用为 IL 发射保留开放签名（<c>List&lt;T&gt;.Item</c> 的 <c>!0</c>），
    /// 这里按宿主的构造泛型实参具体化后再交给类型推断。
    /// </summary>
    private TypeReference? FieldTypeOf(int fieldId)
    {
        var fr = FieldOf(fieldId);
        if (fr == null) return null;
        return ImportOrNull(Concretize(fr.FieldType, fr.DeclaringType as GenericInstanceType, null));
    }

    /// <summary>导入到合成模块；具体化失败（含未消解的泛型形参）时返回 null，交由上层退化为 object。</summary>
    private TypeReference? ImportOrNull(TypeReference? t)
    {
        if (t == null || t.ContainsGenericParameter) return null;
        try { return _module.ImportReference(t); }
        catch { return null; }
    }

    // ---------- 匿名类/闭包（storey） ----------

    private readonly Dictionary<string, int> _storeyByTypeName = [];

    /// <summary>
    /// 被补丁代码真正访问过的闭包字段 (闭包下标, 字段下标)。<c>&lt;&gt;c</c> 这类无捕获闭包的
    /// 字段在补丁里没有任何读写现场，类型无从推断——报告要把它和「推断失败」区分开，
    /// 缺少读写现场时不计入可推断字段的分母。
    /// </summary>
    public HashSet<(int Storey, int Field)> TouchedStoreyFields { get; } = [];

    private List<Decoded>?[]? _decodeCache;
    private readonly Dictionary<int, bool> _isDelegateCtorCache = [];

    /// <summary>把已声明好的合成闭包类型登记进来，供接收者→闭包的反查使用。</summary>
    public void RegisterStorey(StoreyDef sd)
    {
        Storeys[sd.Index] = sd;
        _storeyByTypeName[sd.Type.FullName] = sd.Index;
    }

    private TypeDefinition? StoreyType(int idx)
        => Storeys.TryGetValue(idx, out var sd) ? sd.Type : null;

    /// <summary>栈上这个类型是不是某个闭包实例；是则返回闭包下标，否则 -1。</summary>
    private int StoreyOf(TypeReference? t)
        => t != null && _storeyByTypeName.TryGetValue(t.FullName, out var i) ? i : -1;

    private TypeReference? StoreyFieldType(int storey, int field)
        => Storeys.TryGetValue(storey, out var sd) && field >= 0 && field < sd.Fields.Count
            ? sd.Fields[field].FieldType : null;

    private List<Decoded>?[] DecodeAll()
    {
        if (_decodeCache != null) return _decodeCache;
        var cache = new List<Decoded>?[_p.Methods.Count];
        for (int i = 0; i < _p.Methods.Count; i++)
            if (!_p.Methods[i].IsEmpty) cache[i] = Decode(_p.Methods[i], []);
        return _decodeCache = cache;
    }

    private bool IsDelegateCtor(int externMethodId)
    {
        if (_isDelegateCtorCache.TryGetValue(externMethodId, out var known)) return known;
        bool ok = false;
        try
        {
            var ctor = MethodOf(externMethodId);
            // 委托 ctor 恒为 .ctor(object, native int)；声明类型派生自 MulticastDelegate/Delegate
            if (ctor is { Name: ".ctor", Parameters.Count: 2 } &&
                ctor.DeclaringType?.Resolve() is { BaseType: not null } td)
                ok = td.BaseType.FullName is "System.MulticastDelegate" or "System.Delegate";
        }
        catch { /* 解析不到就当不是委托 */ }
        return _isDelegateCtorCache[externMethodId] = ok;
    }

    /// <summary>
    /// 判定「哪个补丁内部方法属于哪个闭包」，即该方法的 arg0 是哪个闭包实例。三个来源：
    /// <list type="bullet">
    /// <item>补丁元数据：闭包的 ctor / 接口实现槽 / vtable 槽——权威且免费；迭代器闭包全靠这条。</item>
    /// <item>委托创建点：<c>&lt;载入闭包&gt;; Ldc_I4 &lt;methodId&gt;; Newobj &lt;委托&gt;::.ctor</c>
    ///       是 C# 编译器发射 lambda 的固定形态，第三条指令确认是委托 ctor 才采信。</item>
    /// <item>VM 静态字段：<c>&lt;&gt;c</c> 单例存在那里（<c>Newanon i; Stsfld -k</c>），跨方法可见，
    ///       故整体迭代到不动点。</item>
    /// </list>
    /// </summary>
    public void BuildStoreyBindings()
    {
        var dec = DecodeAll();

        for (int i = 0; i < _p.AnonStoreys.Count; i++)
        {
            var a = _p.AnonStoreys[i];
            Bind(a.CtorId, i);
            foreach (var itf in a.Interfaces)
                foreach (var mid in itf.MethodIds) Bind(mid, i);
            foreach (var mid in a.VTable) Bind(mid, i);
        }

        var vmStaticStorey = new Dictionary<int, int>();
        for (int round = 0; round < 4; round++)
        {
            bool changed = false;
            for (int id = 0; id < dec.Length; id++)
            {
                var code = dec[id];
                if (code == null) continue;
                var localStorey = new Dictionary<int, int>();

                for (int k = 0; k < code.Count; k++)
                {
                    var d = code[k];

                    if (d.Mnemonic == "Newanon" && k + 1 < code.Count)
                    {
                        var nx = code[k + 1];
                        if (nx.Mnemonic == "Stloc") localStorey[nx.Operand] = d.Operand;
                        else if (nx.Mnemonic == "Stsfld" && nx.Operand < 0)
                        {
                            int vs = -(nx.Operand + 1);
                            if (!vmStaticStorey.TryGetValue(vs, out var old) || old != d.Operand)
                            { vmStaticStorey[vs] = d.Operand; changed = true; }
                        }
                    }

                    if (d.Mnemonic != "Newobj" || k < 2) continue;
                    if (code[k - 1].Mnemonic != "Ldc_I4") continue;
                    if (!IsDelegateCtor(d.Operand & 0xFFFF)) continue;

                    int recv = code[k - 2].Mnemonic switch
                    {
                        "Ldloc" => localStorey.GetValueOrDefault(code[k - 2].Operand, -1),
                        "Ldsfld" when code[k - 2].Operand < 0
                            => vmStaticStorey.GetValueOrDefault(-(code[k - 2].Operand + 1), -1),
                        "Ldarg" when code[k - 2].Operand == 0 => MethodStorey.GetValueOrDefault(id, -1),
                        _ => -1,
                    };
                    if (recv >= 0 && Bind(code[k - 1].Operand, recv)) changed = true;
                }
            }
            if (!changed) break;
        }

        bool Bind(int methodId, int storey)
        {
            if (methodId < 0 || methodId >= _p.Methods.Count || _p.Methods[methodId].IsEmpty) return false;
            if (storey < 0 || storey >= _p.AnonStoreys.Count) return false;
            if (MethodStorey.TryGetValue(methodId, out var old)) return false;  // 首次绑定为准
            MethodStorey[methodId] = storey;
            return true;
        }
    }

    /// <summary>观测到一次 <c>storey.f[field] = 值</c>，用来给闭包字段定型。</summary>
    private bool RefineStoreyField(int storey, int field, TypeReference? seen)
    {
        // object 这个观测结果不含信息（字段初值本来就是 object），却会因 Merge 的冲突规则
        // 把已经推出来的具体类型压回去，直接忽略。
        if (seen == null || seen.FullName == _object.FullName) return false;
        if (!Storeys.TryGetValue(storey, out var sd)) return false;
        if (field < 0 || field >= sd.Fields.Count || sd.Locked[field]) return false;
        if (!Merge(ref sd.Observed[field], seen)) return false;
        sd.Fields[field].FieldType = sd.Observed[field] ?? _object;
        return true;
    }

    private readonly Dictionary<int, int> _vmStaticStoreyKind = [];   // vm 下标 → 闭包下标，-1 = 冲突

    /// <summary>VM 静态字段若只被某个闭包实例赋值过（典型：<c>&lt;&gt;c</c> 单例），就把它定型成该闭包。</summary>
    private bool RefineVmStatic(int vmIndex, int storey)
    {
        if (!VmStatics.TryGetValue(vmIndex, out var fd)) return false;
        if (_vmStaticStoreyKind.TryGetValue(vmIndex, out var old))
        {
            if (old == storey || old < 0) return false;
            _vmStaticStoreyKind[vmIndex] = -1;
            fd.FieldType = _object;
            return true;
        }
        var st = StoreyType(storey);
        if (st == null || fd.FieldType.FullName != _object.FullName) return false;
        _vmStaticStoreyKind[vmIndex] = storey;
        fd.FieldType = st;
        return true;
    }

    private static string SanitizeName(string n)
    {
        var chars = n.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray();
        return new string(chars);
    }

    // ---------- 指令发射 ----------

    private Instruction? Emit(ILProcessor il, Decoded d, MethodDefinition method, MethodBody body,
        List<string> problems, List<(Instruction, int)> pending, List<(Instruction, List<int>)> pendingSwitch)
    {
        var mn = d.Mnemonic;
        Instruction ins;

        switch (mn)
        {
            // ---- 参数/局部 ----
            case "Ldarg":
                ins = ArgAccess(OpCodes.Ldarg, d.Operand, method); break;
            case "Ldarga":
                ins = ArgAccess(OpCodes.Ldarga, d.Operand, method); break;
            case "Starg":
                ins = ArgAccess(OpCodes.Starg, d.Operand, method); break;
            case "Ldloc":
                ins = LocalAccess(OpCodes.Ldloc, d.Operand, body); break;
            case "Ldloca":
                ins = LocalAccess(OpCodes.Ldloca, d.Operand, body); break;
            case "Stloc":
                ins = LocalAccess(OpCodes.Stloc, d.Operand, body); break;

            // ---- 常量 ----
            case "Ldc_I4":
                // 委托 ctor 的 IntPtr 槽装的是补丁内部 methodId，还原成 Ldftn 才看得出目标
                ins = _ldftnRewrite.TryGetValue(d.Index, out var dlgTarget)
                    ? Instruction.Create(OpCodes.Ldftn, dlgTarget)
                    : Instruction.Create(OpCodes.Ldc_I4, d.Operand);
                break;
            case "Ldc_I8": ins = Instruction.Create(OpCodes.Ldc_I8, d.InlineI8); break;
            case "Ldc_R4": ins = Instruction.Create(OpCodes.Ldc_R4, BitConverter.Int32BitsToSingle(d.Operand)); break;
            case "Ldc_R8": ins = Instruction.Create(OpCodes.Ldc_R8, d.InlineR8); break;
            case "Ldstr":
                ins = Instruction.Create(OpCodes.Ldstr,
                    d.Operand >= 0 && d.Operand < _p.InternStrings.Count ? _p.InternStrings[d.Operand] : "");
                break;

            // ---- 调用族：operand = (argc << 16) | id ----
            case "CallExtern":
            case "Newobj":
                {
                    var mr = MethodOf(d.Operand & 0xFFFF);
                    if (mr == null)
                    {
                        problems.Add($"[{d.Index}] {mn} 解析不到 extern#{d.Operand & 0xFFFF}");
                        return EmitPlaceholder(il);
                    }
                    var op = mn == "Newobj" ? OpCodes.Newobj : OpCodes.Call;
                    // 虚调用在非虚方法上会失败，退化为 Call。Resolve 可能因缺程序集抛，失败就按原样发射。
                    if (op == OpCodes.Callvirt)
                    {
                        try { if (mr.Resolve() is { IsVirtual: false }) op = OpCodes.Call; }
                        catch { /* 解析不到就保留 Callvirt */ }
                    }
                    ins = Instruction.Create(op, mr);
                    break;
                }
            // Callvirt 在 IFix 里是「内部虚调用」，operand 低位是补丁内部 methodId，
            // 不是 extern 索引（VM 的 Code.Callvirt 分支完全不碰 externMethods）。
            case "Call":
            case "Callvirt":
            case "Callvirtvirt":
                {
                    int targetId = d.Operand & 0xFFFF;
                    if (!InternalStubs.TryGetValue(targetId, out var stub))
                    {
                        problems.Add($"[{d.Index}] {mn} 找不到内部方法 method#{targetId} 的桩");
                        return EmitPlaceholder(il);
                    }
                    ins = Instruction.Create(OpCodes.Call, stub);
                    break;
                }
            case "Newanon":
                {
                    if (!Storeys.TryGetValue(d.Operand, out var sd))
                    {
                        problems.Add($"[{d.Index}] Newanon 找不到 anonStorey#{d.Operand} 的合成类型");
                        return EmitPlaceholder(il);
                    }
                    ins = Instruction.Create(OpCodes.Newobj, sd.Ctor);
                    break;
                }
            case "Ldvirtftn2":
                problems.Add($"[{d.Index}] Ldvirtftn2 走 vtable 槽位 [{d.Operand}]，无法静态确定目标");
                return EmitPlaceholder(il);

            // ---- 函数指针：operand 直接是 extern 方法索引，不带 argc 打包 ----
            case "Ldftn":
            case "Ldvirtftn":
                {
                    var mr = MethodOf(d.Operand);
                    if (mr == null)
                    {
                        problems.Add($"[{d.Index}] {mn} 解析不到 extern#{d.Operand}");
                        return EmitPlaceholder(il);
                    }
                    OpCodeTable.TryGet(mn, out var lop);
                    ins = Instruction.Create(lop, mr);
                    break;
                }

            // ---- 字段 ----
            case "Ldfld":
            case "Ldflda":
            case "Stfld":
            case "Ldsfld":
            case "Ldsflda":
            case "Stsfld":
                {
                    if (d.Operand < 0)
                    {
                        int negIdx = -(d.Operand + 1);
                        bool instanceForm = mn is "Ldfld" or "Stfld" or "Ldflda";

                        // 实例形式 = 闭包实例字段。接收者所属闭包由前面的栈模拟定位，
                        // 定位到就发真正的 Ldfld/Stfld，闭包之间互不串味。
                        if (instanceForm)
                        {
                            if (_siteStorey.TryGetValue(d.Index, out int si) &&
                                Storeys.TryGetValue(si, out var sd) &&
                                negIdx < sd.Fields.Count)
                            {
                                OpCodeTable.TryGet(mn, out var sop);
                                ins = Instruction.Create(sop, sd.Fields[negIdx]);
                                break;
                            }

                            // 兜底：接收者推不出来。仍要保持栈形状，但绝不能落到 VM 静态字段上
                            // 避免将闭包实例字段与全局静态字段混淆。
                            problems.Add($"[{d.Index}] {mn} 的接收者推不出所属闭包，闭包字段 [{negIdx}] 退化为未定位 holder");
                            var uf = UnresolvedField(negIdx);
                            if (uf == null) return EmitPlaceholder(il);
                            if (mn == "Stfld")
                            {
                                ins = Instruction.Create(OpCodes.Stsfld, uf);
                                il.Append(ins);
                                il.Append(Instruction.Create(OpCodes.Pop));   // 弹掉接收者
                                return ins;
                            }
                            il.Append(Instruction.Create(OpCodes.Pop));       // 弹掉接收者
                            ins = Instruction.Create(mn == "Ldflda" ? OpCodes.Ldsflda : OpCodes.Ldsfld, uf);
                            il.Append(ins);
                            return ins;
                        }

                        // 静态形式 = VM 静态字段表，本来就是全局唯一的，直译即可。
                        if (!VmStatics.TryGetValue(negIdx, out var vf))
                        {
                            problems.Add($"[{d.Index}] {mn} 找不到 VM 静态字段 [{negIdx}] 的合成字段");
                            return EmitPlaceholder(il);
                        }
                        OpCodeTable.TryGet(mn, out var vop);
                        ins = Instruction.Create(vop, vf);
                        break;
                    }
                    var fr = FieldOf(d.Operand);
                    if (fr == null)
                    {
                        // 补丁新增字段：原程序集里没有，走合成访问器（栈序与参数序一致）
                        if (NewFields.TryGetValue(d.Operand, out var acc))
                        {
                            var target = mn switch
                            {
                                "Ldfld" => acc.GetInstance,
                                "Stfld" => acc.SetInstance,
                                "Ldsfld" => acc.GetStatic,
                                "Stsfld" => acc.SetStatic,
                                _ => null,
                            };
                            if (target == null)
                            {
                                problems.Add($"[{d.Index}] {mn} 新增字段不支持取地址形式");
                                return EmitPlaceholder(il);
                            }
                            ins = Instruction.Create(OpCodes.Call, target);
                            break;
                        }
                        problems.Add($"[{d.Index}] {mn} 解析不到 field#{d.Operand}");
                        return EmitPlaceholder(il);
                    }
                    OpCodeTable.TryGet(mn, out var fop);
                    ins = Instruction.Create(fop, fr);
                    break;
                }

            // ---- 类型操作数 ----
            case "Ldtype":
                ins = Instruction.Create(OpCodes.Ldtoken, TypeOf(d.Operand)); break;
            case "Castclass":
            case "Isinst":
            case "Box":
            case "Unbox":
            case "Unbox_Any":
            case "Newarr":
            case "Initobj":
            case "Ldobj":
            case "Stobj":
            case "Cpobj":
            case "Constrained":
            case "Sizeof":
            case "Mkrefany":
            case "Refanyval":
            case "Ldelem_Any":
            case "Stelem_Any":
            case "Ldelema":
            case "Ldtoken":
                {
                    OpCodeTable.TryGet(mn, out var top);
                    ins = Instruction.Create(top, TypeOf(d.Operand));
                    break;
                }

            // ---- 分支 ----
            case "Br":
            case "Brfalse":
            case "Brtrue":
            case "Beq":
            case "Bge":
            case "Bgt":
            case "Ble":
            case "Blt":
            case "Bne_Un":
            case "Bge_Un":
            case "Bgt_Un":
            case "Ble_Un":
            case "Blt_Un":
                {
                    OpCodeTable.TryGet(mn, out var bop);
                    ins = Instruction.Create(bop, Instruction.Create(OpCodes.Nop)); // 占位，稍后回填
                    il.Append(ins);
                    pending.Add((ins, d.Index + d.Operand));
                    return ins;
                }
            case "Leave":
                {
                    ins = Instruction.Create(OpCodes.Leave, Instruction.Create(OpCodes.Nop));
                    il.Append(ins);
                    pending.Add((ins, d.Operand)); // Leave 的 operand 是绝对目标
                    return ins;
                }
            case "Switch":
                {
                    ins = Instruction.Create(OpCodes.Switch, []);
                    il.Append(ins);
                    pendingSwitch.Add((ins, d.SwitchTargets));
                    return ins;
                }

            // ---- 其余无操作数指令 ----
            default:
                {
                    if (!OpCodeTable.TryGet(mn, out var op))
                    {
                        problems.Add($"[{d.Index}] 无法映射到 CIL 的助记符 {mn}");
                        return EmitPlaceholder(il);
                    }
                    if (op.OperandType != OperandType.InlineNone)
                    {
                        problems.Add($"[{d.Index}] {mn} 需要操作数但未处理（{op.OperandType}）");
                        return EmitPlaceholder(il);
                    }
                    ins = Instruction.Create(op);
                    break;
                }
        }

        il.Append(ins);
        return ins;
    }

    /// <summary>无法忠实发射时的占位：保留一条指令以维持分支目标可达。</summary>
    private static Instruction EmitPlaceholder(ILProcessor il)
    {
        var nop = Instruction.Create(OpCodes.Nop);
        il.Append(nop);
        return nop;
    }

    /// <summary>
    /// IFix 的 Ldarg 下标里 0 恒为 this。闭包方法发射成闭包类型上的实例方法后，Cecil 的
    /// Parameters 不含 this，故下标要整体右移一位，0 号取 ThisParameter。
    /// </summary>
    private static Instruction ArgAccess(OpCode op, int index, MethodDefinition method)
    {
        if (index < 0) return Instruction.Create(OpCodes.Ldnull);
        if (method.HasThis)
        {
            if (index == 0) return Instruction.Create(op, method.Body.ThisParameter);
            index--;
        }
        if (index < method.Parameters.Count)
            return Instruction.Create(op, method.Parameters[index]);
        return Instruction.Create(OpCodes.Ldnull);
    }

    /// <summary>Ldarg 下标 → 类型，含 this 槽；与 <see cref="ArgAccess"/> 的下标口径一致。</summary>
    private static TypeReference?[] ArgTypes(MethodDefinition m)
    {
        var list = new List<TypeReference?>();
        if (m.HasThis) list.Add(m.DeclaringType);
        foreach (var p in m.Parameters) list.Add(p.ParameterType);
        return [.. list];
    }

    private static Instruction LocalAccess(OpCode op, int index, MethodBody body)
    {
        if (index >= 0 && index < body.Variables.Count)
            return Instruction.Create(op, body.Variables[index]);
        return Instruction.Create(OpCodes.Nop);
    }

    // ---------- 局部变量类型推断 ----------

    /// <summary>
    /// 带类型的栈模拟。只为在 stloc 处得到局部变量类型；遇到不确定就放 null（最终退化为 object）。
    /// 不追求完备，目标是让常见情形（局部保存调用返回值/字段/常量）拿到正确类型。
    /// </summary>
    /// <summary>模拟一遍得到的观测：局部类型、内部调用点的实参类型、各 Ret 处的栈顶类型。</summary>
    public sealed class SimObservations
    {
        public List<(int TargetId, List<TypeReference?> Args)> InternalCalls = [];
        public List<TypeReference?> RetTypes = [];
        /// <summary>实例形式的负数字段操作数：指令下标 → 接收者所属的闭包下标。</summary>
        public Dictionary<int, int> FieldSiteStorey = [];
        /// <summary>观测到的 <c>storey.f[i] = 值</c>。</summary>
        public List<(int Storey, int Field, TypeReference? Value)> StoreyStores = [];
        /// <summary>观测到的 <c>vmStatic[k] = 某闭包实例</c>。</summary>
        public List<(int VmIndex, int Storey)> VmStaticStoreyStores = [];
        /// <summary>委托创建点上观测到的目标对象类型，对应目标方法的 arg0。</summary>
        public List<(int MethodId, TypeReference? Target)> DelegateTargets = [];
    }

    private void SimulateForLocals(List<Decoded> decoded, MethodDefinition method,
        TypeReference?[] localTypes, SimObservations obs)
        => Simulate(decoded, ArgTypes(method),
            method.ReturnType.FullName != "System.Void",
            localTypes, obs,
            // 填体阶段直接使用已推断的内部桩返回类型。
            // 将调用返回类型传播到接收该值的局部变量。
            id => InternalStubs.TryGetValue(id, out var m) && m.ReturnType.FullName != "System.Void"
                ? m.ReturnType
                : null);

    /// <summary>
    /// 带类型的求值栈模拟。用途有两个：推断局部变量类型，以及采集内部调用点的实参类型
    /// （补丁不存内部方法签名，只能从调用现场反推）。遇到不确定就放 null。
    /// </summary>
    private void Simulate(List<Decoded> decoded, TypeReference?[] paramTypes, bool returnsValue,
        TypeReference?[] localTypes, SimObservations? obs, Func<int, TypeReference?> internalReturnType)
    {
        var stack = new List<TypeReference?>();

        // 与 stack 等长的来源标记：>=0 表示该槽是「局部变量 N 的地址」（由 Ldloca 压入）。
        // 有了它，Initobj/Ldobj/Stobj 的类型操作数和被调方形参的 byref 元素类型就能落到具体的
        // 局部变量上——这两处是补丁里仅有的、局部变量**声明类型**的权威来源。
        var prov = new List<int>();
        int lastProv = -1;
        void Push(TypeReference? t, int p = -1) { stack.Add(t); prov.Add(p); }
        TypeReference? Pop()
        {
            if (stack.Count == 0) { lastProv = -1; return null; }
            var t = stack[^1];
            lastProv = prov[^1];
            stack.RemoveAt(stack.Count - 1);
            prov.RemoveAt(prov.Count - 1);
            return t;
        }
        void PopN(int n) { for (int i = 0; i < n; i++) Pop(); }

        // 声明类型的观测。Stloc 的「首次赋值为准、冲突退化 object」合并会被这些覆盖，
        // 故统一记下来，等这一遍走完再落盘，免受指令顺序影响。
        var declared = new Dictionary<int, TypeReference>();
        void Declare(int slot, TypeReference? t)
        {
            if (slot < 0 || slot >= localTypes.Length || t == null) return;
            if (t.FullName == _object.FullName) return;
            if (!declared.ContainsKey(slot)) declared[slot] = t;   // 先到先得
        }

        // 从一次调用的实参来源里提取声明类型：接收者对应声明类型，byref 形参对应其元素类型。
        // 实参按从左到右压栈（ECMA-335），故弹出后反转即得压栈顺序。
        void HarvestCall(MethodReference? mr, List<(TypeReference? T, int P)> pushed, bool isNewobj)
        {
            if (mr == null) return;
            bool hasThis = !isNewobj && mr.HasThis;
            for (int i = 0; i < pushed.Count; i++)
            {
                int slot = pushed[i].P;
                if (slot < 0) continue;
                if (hasThis && i == 0) { Declare(slot, ImportOrNull(mr.DeclaringType)); continue; }
                int pi = hasThis ? i - 1 : i;
                if (pi < 0 || pi >= mr.Parameters.Count) continue;
                if (mr.Parameters[pi].ParameterType is ByReferenceType brt)
                    Declare(slot, ImportOrNull(Concretize(brt.ElementType, mr)));
            }
        }

        List<(TypeReference? T, int P)> PopArgs(int n)
        {
            var got = new List<(TypeReference? T, int P)>();
            for (int i = 0; i < n; i++) { var t = Pop(); got.Add((t, lastProv)); }
            got.Reverse();
            return got;
        }

        // 分支汇合点的栈快照。线性扫描会把 if/else 菱形的两条边接连「执行」一遍，栈就多压了一份：
        // `Ldloc storey; <cond>; brtrue L; <A>; br M; L: <B>; M: stfld` 走到 stfld 时栈上多出一个 A，
        // 接收者会被认成 A 而不是 storey。这里在跳转处记下目标的栈形状，落到目标时按快照恢复。
        var snapshots = new Dictionary<int, (List<TypeReference?> Stack, List<int> Prov)>();
        void MarkTarget(int target)
        {
            if (target >= 0 && !snapshots.ContainsKey(target))
                snapshots[target] = ([.. stack], [.. prov]);
        }
        bool afterUnconditional = false;
        Decoded? prevDecoded = null;

        foreach (var d in decoded)
        {
            // 只有「上一条是无条件转移」时才恢复：否则是正常落入，当前栈就是真实状态。
            if (afterUnconditional && snapshots.TryGetValue(d.Index, out var snap))
            {
                stack.Clear();
                stack.AddRange(snap.Stack);
                prov.Clear();
                prov.AddRange(snap.Prov);
            }
            afterUnconditional = false;

            switch (d.Mnemonic)
            {
                case "StackSpace": break;
                case "Ldarg":
                    Push(d.Operand >= 0 && d.Operand < paramTypes.Length
                        ? paramTypes[d.Operand] : null);
                    break;
                case "Ldarga": Push(_intptr); break;
                case "Starg": Pop(); break;
                case "Ldloc":
                    Push(d.Operand >= 0 && d.Operand < localTypes.Length ? localTypes[d.Operand] : null);
                    break;
                case "Ldloca": Push(_intptr, d.Operand); break;
                case "Stloc":
                    {
                        var t = Pop();
                        if (d.Operand >= 0 && d.Operand < localTypes.Length && !declared.ContainsKey(d.Operand))
                        {
                            var prev = localTypes[d.Operand];
                            if (prev == null) localTypes[d.Operand] = t;
                            else if (t != null && prev.FullName != t.FullName)
                                localTypes[d.Operand] = UnifySameKind(prev, t) ?? _object;
                        }
                        break;
                    }
                case "Ldc_I4": Push(_int32); break;
                case "Ldc_I8": Push(_int64); break;
                case "Ldc_R4": Push(_single); break;
                case "Ldc_R8": Push(_double); break;
                case "Ldstr": Push(_string); break;
                case "Ldnull": Push(_object); break;
                case "Ldtype": Push(_type); break;

                case "CallExtern":
                    {
                        var mr = MethodOf(d.Operand & 0xFFFF);
                        HarvestCall(mr, PopArgs(d.Operand >> 16), isNewobj: false);
                        if (mr != null && mr.ReturnType.FullName != "System.Void")
                            Push(ImportOrNull(Concretize(mr.ReturnType, mr)));
                        break;
                    }
                case "Newobj":
                    {
                        var mr = MethodOf(d.Operand & 0xFFFF);
                        var pushed = PopArgs(d.Operand >> 16);
                        HarvestCall(mr, pushed, isNewobj: true);
                        // 委托 ctor 的第一个实参就是 target 对象，也就是被包装方法的 arg0。
                        // 委托的 Invoke 签名只说得出 arg1.. 的类型，arg0 只能从这里观测。
                        if (obs != null && prevDecoded?.Mnemonic == "Ldc_I4" && pushed.Count == 2 &&
                            IsDelegateCtor(d.Operand & 0xFFFF))
                            obs.DelegateTargets.Add((prevDecoded.Operand, pushed[0].T));
                        Push(mr != null ? ImportOrNull(mr.DeclaringType) : null);
                        break;
                    }
                case "Call":
                case "Callvirt":
                case "Callvirtvirt":
                    {
                        int argc = d.Operand >> 16, target = d.Operand & 0xFFFF;
                        var args = new List<TypeReference?>();
                        for (int k = 0; k < argc; k++) args.Add(Pop());
                        args.Reverse();                       // 栈是逆序弹出的
                        obs?.InternalCalls.Add((target, args));
                        var rt2 = internalReturnType(target);
                        if (rt2 != null) Push(rt2);
                        else if (InternalReturnsValue(target)) Push(null);
                        break;
                    }
                case "Newanon":
                    {
                        // Newanon 会消耗 ctor 实参（迭代器闭包就带 1 个初始状态），旧实现只 Push
                        // 不 Pop，栈会整体错位；这里按 CtorParamNum 对齐，并把这次分配当作对 ctor
                        // 的一次调用记下来，好给 ctor 形参定型。
                        var a = d.Operand >= 0 && d.Operand < _p.AnonStoreys.Count
                            ? _p.AnonStoreys[d.Operand] : null;
                        var cargs = new List<TypeReference?>();
                        for (int k = 0; k < (a?.CtorParamNum ?? 0); k++) cargs.Add(Pop());
                        cargs.Reverse();
                        var st = StoreyType(d.Operand);
                        Push(st);
                        if (a != null && obs != null && a.CtorId >= 0)
                        {
                            var full = new List<TypeReference?> { st };
                            full.AddRange(cargs);
                            obs.InternalCalls.Add((a.CtorId, full));
                        }
                        break;
                    }
                case "Ldvirtftn2": Push(_intptr); break;

                // 负数字段操作数在 IFix 里有两种语义，必须按「实例形式 / 静态形式」分流：
                //   Ldfld/Ldflda/Stfld  → 栈上那个闭包实例的第 k 个字段（下标各闭包自己从 0 起算）
                //   Ldsfld/Ldsflda/Stsfld → VM 静态字段表第 k 项（全局唯一）
                // 两者下标空间不同，混为一谈会让互不相干的闭包看起来共享同一批全局变量。
                case "Ldfld":
                    {
                        var recv = Pop();
                        if (d.Operand >= 0) { Push(FieldTypeOf(d.Operand)); break; }
                        int si = StoreyOf(recv);
                        if (si >= 0)
                        {
                            obs?.FieldSiteStorey.TryAdd(d.Index, si);
                            TouchedStoreyFields.Add((si, -(d.Operand + 1)));
                            Push(StoreyFieldType(si, -(d.Operand + 1)));
                        }
                        else Push(null);
                        break;
                    }
                case "Ldsfld":
                    Push(d.Operand >= 0 ? FieldTypeOf(d.Operand)
                        : VmStaticType(-(d.Operand + 1)));
                    break;
                case "Ldflda":
                    {
                        var recvA = Pop();
                        if (d.Operand < 0)
                        {
                            int si = StoreyOf(recvA);
                            if (si >= 0)
                            {
                                obs?.FieldSiteStorey.TryAdd(d.Index, si);
                                TouchedStoreyFields.Add((si, -(d.Operand + 1)));
                            }
                        }
                        Push(_intptr);
                        break;
                    }
                case "Ldsflda": Push(_intptr); break;
                case "Stfld":
                    {
                        var val = Pop();
                        var recvS = Pop();
                        if (d.Operand < 0)
                        {
                            int si = StoreyOf(recvS);
                            if (si >= 0 && obs != null)
                            {
                                obs.FieldSiteStorey.TryAdd(d.Index, si);
                                obs.StoreyStores.Add((si, -(d.Operand + 1), val));
                                TouchedStoreyFields.Add((si, -(d.Operand + 1)));
                            }
                        }
                        break;
                    }
                case "Stsfld":
                    {
                        var sval = Pop();
                        if (d.Operand < 0 && obs != null)
                        {
                            int si = StoreyOf(sval);
                            if (si >= 0) obs.VmStaticStoreyStores.Add((-(d.Operand + 1), si));
                        }
                        break;
                    }

                case "Box": Pop(); Push(_object); break;
                case "Castclass":
                case "Isinst":
                case "Unbox_Any": Pop(); Push(TypeOf(d.Operand)); break;
                case "Unbox": Pop(); Push(_intptr); break;
                case "Newarr": Pop(); Push(new ArrayType(TypeOf(d.Operand))); break;
                case "Ldlen": Pop(); Push(_int32); break;
                // 这三条都带类型操作数、且作用于一个地址，是局部变量声明类型最直接的来源。
                // Stobj 的地址在值下面（先压地址再压值），故弹出顺序与另两条相反。
                case "Ldobj": Pop(); Declare(lastProv, TypeOf(d.Operand)); Push(TypeOf(d.Operand)); break;
                case "Stobj": Pop(); Pop(); Declare(lastProv, TypeOf(d.Operand)); break;
                case "Initobj": Pop(); Declare(lastProv, TypeOf(d.Operand)); break;

                case "Dup":
                    if (stack.Count > 0) Push(stack[^1], prov[^1]); else Push(null);
                    break;
                case "Pop": Pop(); break;
                case "Ret":
                    if (returnsValue) { var rt = Pop(); obs?.RetTypes.Add(rt); }
                    stack.Clear(); afterUnconditional = true; break;
                case "Throw": Pop(); stack.Clear(); afterUnconditional = true; break;

                case "Ceq":
                case "Cgt":
                case "Cgt_Un":
                case "Clt":
                case "Clt_Un":
                    PopN(2); Push(_int32); break;
                case "Add":
                case "Sub":
                case "Mul":
                case "Div":
                case "Div_Un":
                case "Rem":
                case "Rem_Un":
                case "And":
                case "Or":
                case "Xor":
                case "Shl":
                case "Shr":
                case "Shr_Un":
                case "Add_Ovf":
                case "Add_Ovf_Un":
                case "Mul_Ovf":
                case "Mul_Ovf_Un":
                case "Sub_Ovf":
                case "Sub_Ovf_Un":
                    {
                        // IL 算术不作用于 object，某一侧是 object 只说明那侧还没推出来；
                        // 取具体的那一侧，免得未定型的字段把结果一路污染成 object。
                        var b = Pop(); var a = Pop();
                        bool aKnown = a != null && a.FullName != _object.FullName;
                        Push(aKnown ? a : (b != null && b.FullName != _object.FullName ? b : a ?? b));
                        break;
                    }
                case "Neg": case "Not": break;
                case "Conv_I1":
                case "Conv_U1":
                case "Conv_I2":
                case "Conv_U2":
                case "Conv_I4":
                case "Conv_U4":
                case "Conv_Ovf_I4":
                case "Conv_Ovf_U4":
                    Pop(); Push(_int32); break;
                case "Conv_I8": case "Conv_U8": Pop(); Push(_int64); break;
                case "Conv_R4": Pop(); Push(_single); break;
                case "Conv_R8": case "Conv_R_Un": Pop(); Push(_double); break;
                case "Conv_I": case "Conv_U": Pop(); Push(_intptr); break;

                case "Brfalse":
                case "Brtrue":
                    Pop(); MarkTarget(d.Index + d.Operand); break;
                case "Beq":
                case "Bge":
                case "Bgt":
                case "Ble":
                case "Blt":
                case "Bne_Un":
                case "Bge_Un":
                case "Bgt_Un":
                case "Ble_Un":
                case "Blt_Un":
                    PopN(2); MarkTarget(d.Index + d.Operand); break;
                case "Switch":
                    Pop(); foreach (var t in d.SwitchTargets) MarkTarget(t); break;
                case "Br":
                    MarkTarget(d.Index + d.Operand); stack.Clear(); afterUnconditional = true; break;
                case "Leave":
                    MarkTarget(d.Operand); stack.Clear(); afterUnconditional = true; break;
                case "Endfinally":
                case "Endfilter":
                    stack.Clear(); afterUnconditional = true; break;

                default:
                    if (d.Mnemonic.StartsWith("Ldelem")) { PopN(2); Push(null); }
                    else if (d.Mnemonic.StartsWith("Stelem")) PopN(3);
                    else if (d.Mnemonic.StartsWith("Ldind")) { Pop(); Push(null); }
                    else if (d.Mnemonic.StartsWith("Stind")) PopN(2);
                    break;
            }
            prevDecoded = d;
        }

        // 声明类型是权威的，最后统一覆盖掉 Stloc 合并出来的结果。
        foreach (var (slot, t) in declared) localTypes[slot] = t;
    }
}
