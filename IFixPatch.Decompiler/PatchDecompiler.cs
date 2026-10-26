using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using IFixPatch.Core;
using Mono.Cecil;

namespace IFixPatchDecompiler;

public sealed class DecompileReport
{
    public int FixMethods, FixTranspiled;
    public int InternalMethods, InternalTranspiled;
    public int MethodsWithProblems;
    public List<string> Problems = [];
    public string CSharp = "";
    public List<BalanceIssue> Balance = [];
    public int TypedParams, TotalParams, TypedRets, TotalRets;
    public int Storeys, BoundStoreyMethods, TypedStoreyFields, TotalStoreyFields, UntouchedStoreyFields;
    public int ExternResolved, ExternTotal;
    public List<int> OrphanMethods = [];
    public List<string> ExternFailures = [];
}

/// <summary>
/// 把补丁转成一个合成程序集再交给 ICSharpCode.Decompiler。
///
/// 分两遍：先声明所有方法（修复列表的 47 个 + 其余内部方法的桩）、VM 静态字段、
/// 匿名类分配器；再填充方法体。这样 Call 指向后面才声明的内部方法也能前向引用。
/// </summary>
public static class PatchDecompiler
{
    public static DecompileReport Run(PatchFile p, OpCodeMap map, DummyDllSource src)
    {
        var report = new DecompileReport();

        // 合成程序集必须共用 DummyDll 的解析器，否则 ImportReference / Resolve 解析不到那些程序集
        var asm = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("IFixPatch", new Version(1, 0)), "IFixPatch",
            new ModuleParameters { Kind = ModuleKind.Dll, AssemblyResolver = src.Resolver });
        var module = asm.MainModule;
        var obj = module.TypeSystem.Object;

        // 外部方法是否有返回值：查 DummyDll。必须构造时传入，推断过程要用。
        var sigs = new InternalSignatures(p, map, id =>
        {
            if (id < 0 || id >= p.ExternMethods.Count) return false;
            var em = p.ExternMethods[id];
            var aqn = em.DeclaringTypeId >= 0 && em.DeclaringTypeId < p.ExternTypes.Count
                ? p.ExternTypes[em.DeclaringTypeId] : null;
            if (aqn == null) return false;
            var md = src.FindMethod(aqn, em.Name, em.Params.Count);
            return md != null && md.ReturnType.FullName != "System.Void";
        });

        var transpiler = new ILTranspiler(p, map, src, module);

        // ---- 第一遍：声明容器与成员 ----

        // VM 内静态字段（类型来自补丁第 8 节）
        var vmType = new TypeDefinition("IFixPatch", "__VMStatics",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, obj);
        module.Types.Add(vmType);
        for (int i = 0; i < p.StaticFields.Count; i++)
        {
            var ft = TypeRefOf(p, module, src, p.StaticFields[i].FieldTypeId) ?? obj;
            var fd = new FieldDefinition($"vmStatic{i}",
                FieldAttributes.Public | FieldAttributes.Static, ft);
            vmType.Fields.Add(fd);
            transpiler.VmStatics[i] = fd;
        }

        // 匿名类/闭包：合成真实类型。IFix 的负数字段操作数在实例形式下寻址的就是这些对象的字段，
        // 只有把它们建成各自独立的类型，闭包之间才不会看起来共享同一批全局变量。
        // 先扫出「哪个内部方法属于哪个闭包」，其 arg0 才能定型，字段寻址才有依据。
        transpiler.BuildStoreyBindings();
        var objCtor = module.ImportReference(
            src.FindMethod("System.Object, mscorlib", ".ctor", 0) ?? throw new InvalidOperationException("找不到 object..ctor"));
        for (int i = 0; i < p.AnonStoreys.Count; i++)
        {
            var a = p.AnonStoreys[i];
            var st = new TypeDefinition("IFixPatch", $"__Storey{i}",
                TypeAttributes.Public | TypeAttributes.Sealed, obj);
            module.Types.Add(st);

            var sd = new ILTranspiler.StoreyDef
            {
                Index = i,
                Type = st,
                Locked = new bool[a.FieldTypes.Count],
                Observed = new TypeReference?[a.FieldTypes.Count],
            };

            // 字段类型编码：0 = 基元，-2 = 引用类型，其余 = 外部类型 id + 1（值类型）。
            // 前两档都不带确切身份，只能先放 object，再由 Stfld 现场反推（见 RefineStoreyField）。
            for (int k = 0; k < a.FieldTypes.Count; k++)
            {
                int enc = a.FieldTypes[k];
                TypeReference ft = obj;
                if (enc > 0)
                {
                    ft = TypeRefOf(p, module, src, enc - 1) ?? obj;
                    sd.Locked[k] = !ReferenceEquals(ft, obj) && ft.FullName != obj.FullName;
                }
                var fd = new FieldDefinition($"f{k}", FieldAttributes.Public, ft);
                st.Fields.Add(fd);
                sd.Fields.Add(fd);
            }

            var ctor = new MethodDefinition(".ctor",
                MethodAttributes.Public | MethodAttributes.HideBySig |
                MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                module.TypeSystem.Void);
            for (int k = 0; k < a.CtorParamNum; k++)
                ctor.Parameters.Add(new ParameterDefinition($"a{k}", ParameterAttributes.None, obj));
            var cb = ctor.Body.GetILProcessor();
            cb.Append(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ldarg_0));
            cb.Append(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Call, objCtor));
            cb.Append(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ret));
            st.Methods.Add(ctor);
            sd.Ctor = ctor;

            // 补丁里 CtorId 那个方法就是这个闭包的构造函数（体内首条即 base..ctor()）。
            // 直接让它充当 .ctor，省得同一个闭包既有合成 ctor 又有个 `m14(object a0){ a0..ctor(); }`
            // 的静态桩——后者除了噪声没有任何信息量。上面那具空体会被填体阶段整体覆盖。
            if (a.CtorId >= 0 && a.CtorId < p.Methods.Count && !p.Methods[a.CtorId].IsEmpty)
                transpiler.InternalStubs[a.CtorId] = ctor;

            transpiler.RegisterStorey(sd);
        }

        // 接收者推不出闭包时的兜底容器（与 __VMStatics 分开，避免两类语义再次混淆）
        var unresolvedType = new TypeDefinition("IFixPatch", "__UnresolvedStoreyFields",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, obj);
        module.Types.Add(unresolvedType);
        transpiler.UnresolvedStoreyHolder = unresolvedType;

        // 补丁新增字段（原程序集里不存在）→ 合成访问器
        var newFieldType = new TypeDefinition("IFixPatch", "__NewFields",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, obj);
        module.Types.Add(newFieldType);
        var aqnRes = new AqnResolver(src, module);
        for (int i = 0; i < p.Fields.Count; i++)
        {
            var f = p.Fields[i];
            if (!f.IsNew) continue;
            var declAqn0 = f.DeclaringTypeId >= 0 && f.DeclaringTypeId < p.ExternTypes.Count
                ? p.ExternTypes[f.DeclaringTypeId] : null;
            if (declAqn0 == null) continue;
            if (src.FindField(declAqn0, f.Name) != null) continue;   // 真实字段已存在，无需合成

            var ft = (f.FieldTypeId >= 0 && f.FieldTypeId < p.ExternTypes.Count
                ? aqnRes.Resolve(p.ExternTypes[f.FieldTypeId]) : null) ?? obj;
            var (dn, _) = DummyDllSource.SplitAqn(declAqn0);
            var tag = Sanitize(dn[(dn.LastIndexOf('.') + 1)..]) + "_" + Sanitize(f.Name);

            MethodDefinition Make(string name, TypeReference ret, params (string, TypeReference)[] ps)
            {
                var md = new MethodDefinition(name, MethodAttributes.Public | MethodAttributes.Static, ret);
                foreach (var (pn, pt) in ps)
                    md.Parameters.Add(new ParameterDefinition(pn, ParameterAttributes.None, pt));
                var b = md.Body.GetILProcessor();
                if (ret.FullName != "System.Void")
                {
                    if (ret.IsValueType)
                    {
                        var v = new Mono.Cecil.Cil.VariableDefinition(ret); md.Body.Variables.Add(v);
                        md.Body.InitLocals = true;
                        b.Append(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ldloca, v));
                        b.Append(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Initobj, ret));
                        b.Append(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ldloc, v));
                    }
                    else b.Append(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ldnull));
                }
                b.Append(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ret));
                newFieldType.Methods.Add(md);
                return md;
            }

            transpiler.NewFields[i] = new ILTranspiler.NewFieldAccessors
            {
                GetInstance = Make($"Get_{tag}", ft, ("self", obj)),
                SetInstance = Make($"Set_{tag}", module.TypeSystem.Void, ("self", obj), ("value", ft)),
                GetStatic = Make($"GetStatic_{tag}", ft),
                SetStatic = Make($"SetStatic_{tag}", module.TypeSystem.Void, ("value", ft)),
            };
        }

        // 修复列表方法：按原声明类型分组
        var containers = new Dictionary<string, TypeDefinition>();
        var fixByMethodId = new Dictionary<int, FixEntry>();
        foreach (var fx in p.FixList)
            if (fx.MethodId >= 0 && fx.MethodId < p.Methods.Count)
                fixByMethodId[fx.MethodId] = fx;

        // 修复列表方法的签名是已知的（来自 DummyDll），作为类型流分析的锚点
        transpiler.InternalReturnsValue = id => id >= 0 && id < sigs.ReturnsValue.Length && sigs.ReturnsValue[id];
        TypeReference?[]? KnownParams(int id)
        {
            if (!fixByMethodId.TryGetValue(id, out var fx)) return null;
            var declAqn0 = fx.Method.DeclaringTypeId >= 0 && fx.Method.DeclaringTypeId < p.ExternTypes.Count
                ? p.ExternTypes[fx.Method.DeclaringTypeId] : null;
            if (declAqn0 == null) return null;
            var real = src.FindMethod(declAqn0, fx.Method.Name, fx.Method.Params.Count);
            var list = new List<TypeReference?>();
            if (real?.HasThis ?? true) list.Add(aqnRes.Resolve(declAqn0));
            for (int i = 0; i < fx.Method.Params.Count; i++)
            {
                var pr = fx.Method.Params[i];
                list.Add(pr.IsGeneric ? obj
                    : (pr.TypeId >= 0 && pr.TypeId < p.ExternTypes.Count
                        ? aqnRes.Resolve(p.ExternTypes[pr.TypeId]) : null) ?? obj);
            }
            return [.. list];
        }

        // 内部方法签名：参数类型从调用点反推，返回类型从被调方 Ret 反推（不动点迭代）
        var (inferredRet, inferredArgs) = transpiler.AnalyzeInternalTypes(sigs, KnownParams);

        // 内部方法桩（不在修复列表里的）
        var internalType = new TypeDefinition("IFixPatch", "__Internal",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, obj);
        module.Types.Add(internalType);
        int typedParams = 0, totalParams = 0, typedRets = 0, totalRets = 0;
        for (int id = 0; id < p.Methods.Count; id++)
        {
            if (p.Methods[id].IsEmpty || fixByMethodId.ContainsKey(id)) continue;
            if (transpiler.InternalStubs.ContainsKey(id)) continue;   // 闭包 .ctor 已在上面声明
            TypeReference retType = module.TypeSystem.Void;
            if (sigs.ReturnsValue[id])
            {
                totalRets++;
                retType = inferredRet[id] ?? obj;
                if (inferredRet[id] != null && retType.FullName != obj.FullName) typedRets++;
            }
            // 属于某个闭包的方法就发射成该闭包类型上的实例方法——它们本来就是那个类的成员。
            // 除了读起来自然（arg0 变成 this、字段访问不带前缀），委托创建点也才能还原成
            // `new Action<int>(storey.m28)` 这种方法组形态，而不是函数指针强转。
            // ArgCount 由 Ldarg 最大下标推出，方法体未读取 this 时可能为 0；
            // 但它仍是闭包的实例方法（<>c 上那些无捕获 lambda 就是这样），照样按实例发射。
            bool asInstance = transpiler.MethodStorey.TryGetValue(id, out int storeyIdx)
                && transpiler.Storeys.ContainsKey(storeyIdx);
            var owner = asInstance ? transpiler.Storeys[storeyIdx].Type : internalType;

            var stub = new MethodDefinition($"m{id}",
                asInstance
                    ? MethodAttributes.Public | MethodAttributes.HideBySig
                    : MethodAttributes.Public | MethodAttributes.Static,
                retType);
            if (asInstance) stub.HasThis = true;

            // 实例形式下 arg0 是 this，不再作为形参；参数定型统计也相应从 1 号槽起算
            for (int k = asInstance ? 1 : 0; k < sigs.ArgCount[id]; k++)
            {
                totalParams++;
                var pt = (k < inferredArgs[id].Length ? inferredArgs[id][k] : null) ?? obj;
                if (pt.FullName != obj.FullName) typedParams++;
                stub.Parameters.Add(new ParameterDefinition($"a{k}", ParameterAttributes.None, pt));
            }
            owner.Methods.Add(stub);
            transpiler.InternalStubs[id] = stub;
        }
        report.TypedParams = typedParams; report.TotalParams = totalParams;
        report.TypedRets = typedRets; report.TotalRets = totalRets;
        (report.ExternResolved, report.ExternTotal, report.ExternFailures) = transpiler.ProbeExternResolution();
        report.OrphanMethods = transpiler.FindOrphanMethods();
        report.Storeys = transpiler.Storeys.Count;
        report.BoundStoreyMethods = transpiler.MethodStorey.Count;
        foreach (var sd in transpiler.Storeys.Values)
            for (int k = 0; k < sd.Fields.Count; k++)
            {
                // 没有任何读写现场的字段不计入分母：那是补丁里没有这份信息，不是推断失败。
                // <>c 这类无捕获闭包的字段全属此列。
                if (!transpiler.TouchedStoreyFields.Contains((sd.Index, k))) { report.UntouchedStoreyFields++; continue; }
                report.TotalStoreyFields++;
                if (sd.Fields[k].FieldType.FullName != obj.FullName) report.TypedStoreyFields++;
            }

        // ---- 第二遍：填充方法体 ----

        var declaredFix = new Dictionary<int, MethodDefinition>();
        foreach (var kv in fixByMethodId.OrderBy(k => k.Key))
        {
            var fx = kv.Value;
            report.FixMethods++;

            var declAqn = fx.Method.DeclaringTypeId >= 0 && fx.Method.DeclaringTypeId < p.ExternTypes.Count
                ? p.ExternTypes[fx.Method.DeclaringTypeId] : "Unknown";
            var (typeName, _) = DummyDllSource.SplitAqn(declAqn);

            if (!containers.TryGetValue(typeName, out var container))
            {
                int lastDot = typeName.LastIndexOf('.');
                var ns = lastDot > 0 ? typeName[..lastDot] : "";
                var simple = (lastDot > 0 ? typeName[(lastDot + 1)..] : typeName).Replace('+', '_');
                container = new TypeDefinition(ns, simple + "__Patch",
                    TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, obj);
                module.Types.Add(container);
                containers[typeName] = container;
            }

            var problems = new List<string>();
            var declared = transpiler.DeclareFix(fx, container, problems);
            Record(report, $"method#{fx.MethodId} {fx.Method.Name}", problems);
            if (declared != null) declaredFix[fx.MethodId] = declared;
        }

        // ---- 第二遍：填充方法体（此时所有方法都已声明，互相调用可前向引用）----

        foreach (var kv in declaredFix.OrderBy(k => k.Key))
        {
            var r = transpiler.TranspileInternal(p.Methods[kv.Key], kv.Value);
            if (r.Ok) report.FixTranspiled++;
            Record(report, $"method#{kv.Key} {fixByMethodId[kv.Key].Method.Name}", r.Problems);
        }

        // 内部方法体
        for (int id = 0; id < p.Methods.Count; id++)
        {
            if (p.Methods[id].IsEmpty || fixByMethodId.ContainsKey(id)) continue;
            if (!transpiler.InternalStubs.TryGetValue(id, out var stub)) continue;
            report.InternalMethods++;
            var r = transpiler.TranspileInternal(p.Methods[id], stub);
            if (r.Ok) report.InternalTranspiled++;
            Record(report, $"internal m{id}", r.Problems);
        }

        // ---- 栈平衡自查（诊断用，独立于本项目的 pop/push 估计）----
        foreach (var t in module.Types)
            foreach (var mm in t.Methods)
                report.Balance.AddRange(StackBalanceCheck.Check(mm));

        // ---- 反编译 ----
        using var ms = new MemoryStream();
        asm.Write(ms);
        ms.Position = 0;

        try
        {
            var pe = new PEFile("IFixPatch", ms);
            // 反编译器的解析器必须也能搜到 DummyDll：否则 Assembly-CSharp / UnityEngine 的类型
            // 解析不到，ILSpy 会把它们的栈类型标成 Unknown（"missing references"），
            // 产物里充满 "Unknown result type" 注释。
            var resolver = new UniversalAssemblyResolver(null, false, pe.DetectTargetFrameworkId());
            resolver.AddSearchDirectory(src.DllDirectory);
            var settings = new DecompilerSettings
            {
                ThrowOnAssemblyResolveErrors = false,
                ShowXmlDocumentation = false,
            };
            var dec = new CSharpDecompiler(pe, resolver, settings);
            report.CSharp = dec.DecompileWholeModuleAsString();
        }
        catch (Exception e)
        {
            report.Problems.Add($"反编译失败: {e.Message}");
        }

        return report;
    }

    private static string Sanitize(string n)
        => new([.. n.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_')]);

    private static void Record(DecompileReport report, string who, List<string> problems)
    {
        if (problems.Count == 0) return;
        report.MethodsWithProblems++;
        foreach (var pb in problems) report.Problems.Add($"{who}: {pb}");
    }

    /// <summary>解析补丁 externTypes 里的类型（供第一遍声明字段时用）。</summary>
    private static TypeReference? TypeRefOf(PatchFile p,
        ModuleDefinition module, DummyDllSource src, int externTypeId)
    {
        if (externTypeId < 0 || externTypeId >= p.ExternTypes.Count) return null;
        return new AqnResolver(src, module).Resolve(p.ExternTypes[externTypeId]);
    }
}
