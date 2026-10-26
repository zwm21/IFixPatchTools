using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IFixPatch.SourceBuilder.Core;

public sealed class BodyGrafter
{
    /// <summary>本次 graft 搬运的 helper。Graft 之后才有值。</summary>
    public IReadOnlyList<GraftHelperReport> Helpers { get; private set; } = [];

    /// <summary>本次 graft 用到的闭合泛型实例及其存在性证据。Graft 之后才有值。</summary>
    public IReadOnlyList<GenericInstanceReport> GenericInstances { get; private set; } = [];

    /// <summary>规模度量。Graft 之后才有值。</summary>
    public PatchSizeReport Size { get; private set; } = new();

    /// <summary>规模检查结果。Graft 之后才有值。</summary>
    public IReadOnlyList<BuildDiagnostic> Diagnostics { get; private set; } = [];

    /// <summary>当前正在处理的实现方法/指令，用于给报错补上源码位置。</summary>
    private readonly SourceLocator _locator = new();

    public List<GraftBindingReport> Graft(
        PreparedProfile prepared,
        PatchManifest manifest,
        string implementationAssemblyPath,
        string outputAssemblyPath,
        string processConfigPath)
    {
        if (manifest.Patches.Count == 0)
            throw new InvalidDataException("Patch manifest has no patch bindings.");

        outputAssemblyPath = Path.GetFullPath(outputAssemblyPath);
        processConfigPath = Path.GetFullPath(processConfigPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputAssemblyPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(processConfigPath)!);

        using var targetAssembly = CecilModel.ReadAssembly(prepared.CleanAssemblyPath, prepared.ReferenceDirectories);
        // 带 portable pdb 读，只为让 graft 失败能报出 PatchImpl.cs:123 这样的源码位置；
        // 符号缺失会自动降级，不影响 graft 结果。
        using var implementationAssembly = CecilModel.ReadAssembly(
            implementationAssemblyPath,
            prepared.ReferenceDirectories.Append(Path.GetDirectoryName(implementationAssemblyPath)!),
            readSymbols: true);
        var catalog = SourceBuilderJson.Load<AssemblyCatalog>(prepared.CatalogPath);
        var idMap = catalog.IdMap.ToDictionary(entry => entry.Field, entry => entry.Slot, StringComparer.Ordinal);

        var targetModule = targetAssembly.MainModule;
        var implementationModule = implementationAssembly.MainModule;
        var mapper = new ReferenceMapper(targetModule, implementationModule, _locator);

        // 先解析出全部入口对，再发现 helper：MapMethod 重绑时需要 helper 映射已经建好。
        var entries = new List<(MethodDefinition Target, MethodDefinition Implementation)>();
        foreach (var binding in manifest.Patches)
        {
            var target = targetModule.FindMethod(binding.Target);
            var implementation = implementationModule.FindMethod(binding.Implementation);
            ValidateAbi(target, implementation);
            entries.Add((target, implementation));
        }

        var entrySet = entries.Select(entry => entry.Implementation).ToHashSet();
        var helperOrder = DiscoverHelpers(
            entries.Select(entry => entry.Implementation), entrySet, implementationModule, out var calls);
        var helperMethods = new List<MethodDefinition>();
        var helperReports = new List<GraftHelperReport>();
        if (helperOrder.Count != 0)
        {
            var host = CreateHelperHost(targetModule, manifest.AssemblyName);
            var usedNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var helper in helperOrder)
            {
                _locator.Enter(helper);
                var name = UniqueHelperName(helper, usedNames);
                var grafted = new MethodDefinition(
                    name,
                    MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
                    mapper.MapType(helper.ReturnType));
                foreach (var parameter in helper.Parameters)
                {
                    grafted.Parameters.Add(new ParameterDefinition(
                        parameter.Name,
                        parameter.Attributes,
                        mapper.MapType(parameter.ParameterType)));
                }
                host.Methods.Add(grafted);
                mapper.RegisterHelper(helper, grafted);
                helperMethods.Add(grafted);
                helperReports.Add(new GraftHelperReport
                {
                    Implementation = helper.FullName,
                    Grafted = grafted.FullName,
                    SourceInstructionCount = helper.Body.Instructions.Count,
                });
            }

            for (var i = 0; i < helperOrder.Count; i++)
            {
                CloneBody(mapper, helperOrder[i], helperMethods[i]);
                helperReports[i].ResultInstructionCount = helperMethods[i].Body.Instructions.Count;
                helperReports[i].LocalCount = helperMethods[i].Body.Variables.Count;
                helperReports[i].MaxStackSize = helperMethods[i].Body.MaxStackSize;
                helperReports[i].ExceptionHandlerCount = helperMethods[i].Body.ExceptionHandlers.Count;
            }
        }

        var reports = new List<GraftBindingReport>();
        var patchedMethods = new List<MethodDefinition>();
        foreach (var (target, implementation) in entries)
        {
            CloneBody(mapper, implementation, target);
            var idTag = CecilModel.ReadIdTag(target) ?? 0;
            var idMapField = target.DeclaringType.FullName.Replace('.', '-').Replace('/', '-') + "-" + target.Name + idTag;
            if (!idMap.TryGetValue(idMapField, out var nativeSlot))
                throw new InvalidDataException(
                    $"Unable to resolve native IFix IDMAP slot for {target.FullName}: expected field {idMapField}.");
            patchedMethods.Add(target);
            reports.Add(new GraftBindingReport
            {
                Target = target.FullName,
                Implementation = implementation.FullName,
                IdTag = idTag,
                NativeIdMapField = idMapField,
                NativeSlot = nativeSlot,
                SourceInstructionCount = implementation.Body.Instructions.Count,
                ResultInstructionCount = target.Body.Instructions.Count,
                LocalCount = target.Body.Variables.Count,
                MaxStackSize = target.Body.MaxStackSize,
                ExceptionHandlerCount = target.Body.ExceptionHandlers.Count,
            });
        }

        Helpers = helperReports;
        GenericInstances = ProbeGenericInstances(targetModule, mapper.GenericInstances);

        var diagnostics = new List<BuildDiagnostic>();
        // 未找到元数据引用时只警告：DummyDll 没有原始方法体，无法据此判断实例不存在。
        foreach (var instance in GenericInstances.Where(entry => !entry.Proven))
            diagnostics.Add(new BuildDiagnostic
            {
                Severity = "warn",
                Rule = "generic-instance",
                Method = instance.Instance,
                Message = "Not found in the target assembly's metadata signatures; separately confirm " +
                          "the required AOT implementation in the target runtime.",
            });

        Size = PatchSizeAnalyzer.Analyze(
            [.. entries.Select(entry => new GraftedMethod(entry.Target.FullName, entry.Implementation, entry.Target))],
            helperOrder.Zip(helperMethods).ToDictionary(pair => pair.First, pair => pair.Second),
            calls,
            manifest.Limits,
            diagnostics);
        Diagnostics = diagnostics;
        targetAssembly.Write(outputAssemblyPath);
        ProcessConfigWriter.Write(processConfigPath, patchedMethods, helperMethods);
        return reports;
    }

    private static List<GenericInstanceReport> ProbeGenericInstances(
        ModuleDefinition targetModule, IReadOnlyCollection<string> instances)
    {
        if (instances.Count == 0)
            return [];
        var evidence = GenericInstanceProbe.ScanMetadata(targetModule);
        return
        [
            .. instances.Order(StringComparer.Ordinal).Select(instance => new GenericInstanceReport
            {
                Instance = instance,
                Proven = evidence.ContainsKey(instance),
                ProvenBy = evidence.GetValueOrDefault(instance),
            })
        ];
    }

    /// <summary>
    /// 从入口实现出发，传递闭包地收集实现程序集内被调用、且自身不是入口的方法。
    /// 返回顺序保证被调用者排在调用者之前，方便逐个 graft（顺序本身对结果无影响，只是可读）。
    /// </summary>
    private List<MethodDefinition> DiscoverHelpers(
        IEnumerable<MethodDefinition> entries,
        IReadOnlySet<MethodDefinition> entrySet,
        ModuleDefinition implementationModule,
        out Dictionary<MethodDefinition, List<MethodDefinition>> calls)
    {
        var implementationAssemblyName = implementationModule.Assembly.Name.Name;
        var discovered = new List<MethodDefinition>();
        var seen = new HashSet<MethodDefinition>();
        // 调用边要记全，不能只记首次发现的那条：规模检查要沿调用图求最深链，
        // 少一条边就会低估共用求值栈的占用。
        calls = [];
        var queue = new Queue<MethodDefinition>(entries);
        while (queue.Count != 0)
        {
            var current = queue.Dequeue();
            if (!current.HasBody)
                continue;
            _locator.Enter(current);
            foreach (var instruction in current.Body.Instructions)
            {
                _locator.At(instruction);
                if (instruction.Operand is not MethodReference reference)
                    continue;
                if (AssemblyNameOf(reference.DeclaringType.Scope) != implementationAssemblyName)
                    continue;

                if (reference.Resolve() is not { } callee)
                    throw new InvalidDataException(_locator.Decorate(
                        $"Unable to resolve implementation-owned call {reference.FullName} in {current.FullName}."));
                if (entrySet.Contains(callee))
                    throw new NotSupportedException(_locator.Decorate(
                        $"{current.FullName} calls another patch entry point {callee.FullName}; " +
                        "call the game's own method instead."));

                var edges = calls.TryGetValue(current, out var existing) ? existing : calls[current] = [];
                if (!edges.Contains(callee))
                    edges.Add(callee);

                if (!seen.Add(callee))
                    continue;

                if (!callee.IsStatic)
                    throw new NotSupportedException(_locator.Decorate(
                        $"Implementation helper must be static: {callee.FullName}"));
                if (callee.IsConstructor)
                    throw new NotSupportedException(_locator.Decorate(
                        $"Implementation-owned constructors are not supported: {callee.FullName}"));
                if (callee.HasGenericParameters || callee.DeclaringType.HasGenericParameters)
                    throw new NotSupportedException(_locator.Decorate(
                        $"Generic implementation helpers are not supported: {callee.FullName}"));
                if (!callee.HasBody)
                    throw new NotSupportedException(_locator.Decorate(
                        $"Implementation helper has no body: {callee.FullName}"));

                queue.Enqueue(callee);
                discovered.Add(callee);
            }
        }
        discovered.Reverse();
        return discovered;
    }

    /// <summary>
    /// helper 必须作为 MethodDefinition 真实存在于 grafted assembly 里——上游
    /// <c>PatchGenerateConfigure</c> 是遍历该程序集去匹配 new methods 段的。
    /// 该类型只是编译输入，不会随包下发。命名空间避开 "IFix"，否则会被 CodeTranslator 的类型枚举过滤掉。
    /// </summary>
    private static TypeDefinition CreateHelperHost(ModuleDefinition targetModule, string assemblyName)
    {
        var name = Sanitize(assemblyName) + "Helpers";
        var host = new TypeDefinition(
            "IFixPatch.Generated",
            name,
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class,
            targetModule.TypeSystem.Object);
        if (targetModule.Types.Any(type => type.FullName == host.FullName))
            throw new InvalidDataException($"Helper host type already exists in the target assembly: {host.FullName}");
        targetModule.Types.Add(host);
        return host;
    }

    private static string UniqueHelperName(MethodDefinition helper, HashSet<string> used)
    {
        var baseName = Sanitize(helper.DeclaringType.FullName) + "__" + Sanitize(helper.Name);
        var name = baseName;
        var index = 1;
        while (!used.Add(name))
            name = baseName + "_" + index++.ToString();
        return name;
    }

    private static string Sanitize(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
            builder.Append(char.IsLetterOrDigit(character) || character == '_' ? character : '_');
        return builder.ToString();
    }

    private static string? AssemblyNameOf(IMetadataScope scope) => scope switch
    {
        AssemblyNameReference assembly => assembly.Name,
        ModuleDefinition module => module.Assembly.Name.Name,
        ModuleReference module => Path.GetFileNameWithoutExtension(module.Name),
        _ => null,
    };

    private void ValidateAbi(MethodDefinition target, MethodDefinition implementation)
    {
        _locator.Enter(implementation);
        if (!implementation.IsStatic)
            throw new InvalidDataException(_locator.Decorate($"Implementation must be static: {implementation.FullName}"));
        if (!target.HasBody || !implementation.HasBody)
            throw new InvalidDataException(_locator.Decorate("Target and implementation must both have method bodies."));
        if (target.HasGenericParameters || implementation.HasGenericParameters ||
            target.DeclaringType.HasGenericParameters || implementation.DeclaringType.HasGenericParameters)
            throw new NotSupportedException(_locator.Decorate(
                "Generic targets and implementations are not supported."));
        if (!SameType(target.ReturnType, implementation.ReturnType))
            throw new InvalidDataException(_locator.Decorate(
                $"Return type mismatch: target {target.ReturnType.FullName}, implementation {implementation.ReturnType.FullName}"));

        var expectedCount = target.Parameters.Count + (target.HasThis ? 1 : 0);
        if (implementation.Parameters.Count != expectedCount)
            throw new InvalidDataException(_locator.Decorate(
                $"Parameter count mismatch for {target.FullName}: expected {expectedCount}, got {implementation.Parameters.Count}."));

        var offset = 0;
        if (target.HasThis)
        {
            var self = implementation.Parameters[0].ParameterType;
            TypeReference expectedSelf = target.DeclaringType.IsValueType
                ? new ByReferenceType(target.DeclaringType)
                : target.DeclaringType;
            if (!SameType(expectedSelf, self))
                throw new InvalidDataException(_locator.Decorate(
                    $"Explicit self mismatch: expected {expectedSelf.FullName}, got {self.FullName}."));
            offset = 1;
        }

        for (var i = 0; i < target.Parameters.Count; i++)
        {
            var targetParameter = target.Parameters[i];
            var implementationParameter = implementation.Parameters[i + offset];
            if (!SameType(targetParameter.ParameterType, implementationParameter.ParameterType) ||
                targetParameter.IsOut != implementationParameter.IsOut ||
                targetParameter.IsIn != implementationParameter.IsIn)
            {
                throw new InvalidDataException(_locator.Decorate(
                    $"Parameter {i} mismatch: target {Describe(targetParameter)}, implementation {Describe(implementationParameter)}."));
            }
        }
    }

    private static string Describe(ParameterDefinition parameter) =>
        $"{(parameter.IsOut ? "out " : parameter.IsIn ? "in " : "")}{parameter.ParameterType.FullName}";

    private static bool SameType(TypeReference left, TypeReference right) =>
        left.FullName == right.FullName;

    private void CloneBody(
        ReferenceMapper mapper,
        MethodDefinition implementation,
        MethodDefinition target)
    {
        _locator.Enter(implementation);
        var sourceBody = implementation.Body;
        var targetBody = new MethodBody(target)
        {
            InitLocals = sourceBody.InitLocals,
            MaxStackSize = sourceBody.MaxStackSize,
        };
        target.Body = targetBody;

        var variableMap = new Dictionary<VariableDefinition, VariableDefinition>();
        foreach (var variable in sourceBody.Variables)
        {
            var clone = new VariableDefinition(mapper.MapType(variable.VariableType));
            targetBody.Variables.Add(clone);
            variableMap.Add(variable, clone);
        }

        var instructionMap = new Dictionary<Instruction, Instruction>();
        foreach (var source in sourceBody.Instructions)
        {
            var clone = Instruction.Create(OpCodes.Nop);
            targetBody.Instructions.Add(clone);
            instructionMap.Add(source, clone);
        }

        foreach (var source in sourceBody.Instructions)
        {
            var clone = instructionMap[source];
            clone.OpCode = source.OpCode;
            _locator.At(source);
            clone.Operand = MapOperand(source.Operand, target, mapper, variableMap, instructionMap);
        }

        _locator.At(null);
        foreach (var source in sourceBody.ExceptionHandlers)
        {
            targetBody.ExceptionHandlers.Add(new ExceptionHandler(source.HandlerType)
            {
                CatchType = source.CatchType is null ? null : mapper.MapType(source.CatchType),
                TryStart = MapInstruction(source.TryStart, instructionMap),
                TryEnd = MapInstruction(source.TryEnd, instructionMap),
                HandlerStart = MapInstruction(source.HandlerStart, instructionMap),
                HandlerEnd = MapInstruction(source.HandlerEnd, instructionMap),
                FilterStart = MapInstruction(source.FilterStart, instructionMap),
            });
        }
    }

    private object? MapOperand(
        object? operand,
        MethodDefinition target,
        ReferenceMapper mapper,
        IReadOnlyDictionary<VariableDefinition, VariableDefinition> variables,
        IReadOnlyDictionary<Instruction, Instruction> instructions) => operand switch
    {
        null => null,
        Instruction instruction => instructions[instruction],
        Instruction[] targets => targets.Select(i => instructions[i]).ToArray(),
        VariableDefinition variable => variables[variable],
        ParameterDefinition parameter => MapParameter(target, parameter),
        TypeReference type => mapper.MapType(type),
        MethodReference method => mapper.MapMethod(method),
        FieldReference field => mapper.MapField(field),
        CallSite => throw new NotSupportedException(_locator.Decorate("calli/CallSite operands are not supported.")),
        string or sbyte or byte or short or ushort or int or uint or long or ulong or float or double => operand,
        _ => throw new NotSupportedException(_locator.Decorate($"Unsupported IL operand {operand.GetType().FullName}: {operand}")),
    };

    private static ParameterDefinition MapParameter(MethodDefinition target, ParameterDefinition source)
    {
        var logicalIndex = source.Index;
        if (!target.HasThis)
            return target.Parameters[logicalIndex];
        if (logicalIndex == 0)
            return target.Body.ThisParameter;
        return target.Parameters[logicalIndex - 1];
    }

    private static Instruction? MapInstruction(
        Instruction? source,
        IReadOnlyDictionary<Instruction, Instruction> instructions) =>
        source is null ? null : instructions[source];

    /// <summary>
    /// 把 graft 报错定位到补丁源码。实现程序集由 SourceCompiler 用 <c>-debug:portable</c> 编出，
    /// 每条 IL 都能回溯到 sequence point；取不到就退回原始消息，不让缺符号本身变成失败。
    ///
    /// 用「偏移不大于当前指令的最后一个 sequence point」而不是精确匹配：一条 C# 语句编成多条 IL，
    /// 只有首条挂点，精确匹配会让语句中段的报错丢掉行号。
    /// </summary>
    private sealed class SourceLocator
    {
        private MethodDefinition? _method;
        private Instruction? _instruction;

        public void Enter(MethodDefinition method)
        {
            _method = method;
            _instruction = null;
        }

        public void At(Instruction? instruction) => _instruction = instruction;

        public string Decorate(string message) =>
            Describe() is { } location ? location + ": " + message : message;

        private string? Describe()
        {
            var debug = _method?.DebugInformation;
            if (debug is null || !debug.HasSequencePoints)
                return null;
            var point = _instruction is null ? null : Nearest(debug, _instruction.Offset);
            point ??= debug.SequencePoints.FirstOrDefault(candidate => !candidate.IsHidden);
            return point is null ? null : $"{Path.GetFileName(point.Document.Url)}:{point.StartLine}";
        }

        private static SequencePoint? Nearest(MethodDebugInformation debug, int offset)
        {
            SequencePoint? best = null;
            foreach (var point in debug.SequencePoints)
            {
                if (point.IsHidden || point.Offset > offset)
                    continue;
                if (best is null || point.Offset > best.Offset)
                    best = point;
            }
            return best;
        }
    }

    private sealed class ReferenceMapper(
        ModuleDefinition targetModule,
        ModuleDefinition implementationModule,
        SourceLocator locator)
    {
        private readonly string _targetAssemblyName = targetModule.Assembly.Name.Name;
        private readonly string _implementationAssemblyName = implementationModule.Assembly.Name.Name;
        private readonly Dictionary<MethodDefinition, MethodDefinition> _helpers = new();
        private readonly Dictionary<string, GenericInstanceType> _genericInstances = new(StringComparer.Ordinal);

        /// <summary>本次 graft 里出现过的闭合泛型实例（已映射到目标程序集），按全名去重。</summary>
        public IReadOnlyCollection<string> GenericInstances => _genericInstances.Keys;

        /// <summary>登记一个实现程序集 helper 到它在目标程序集里的 grafted 副本。</summary>
        public void RegisterHelper(MethodDefinition implementation, MethodDefinition grafted) =>
            _helpers.Add(implementation, grafted);

        /// <summary>目标程序集里按全名取类型；找不到时补上源码位置再抛。</summary>
        private TypeDefinition FindTargetType(TypeReference type)
        {
            try
            {
                return targetModule.FindType(type.FullName);
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidDataException(locator.Decorate(exception.Message));
            }
        }

        public TypeReference MapType(TypeReference type)
        {
            if (type is ByReferenceType byReference)
                return new ByReferenceType(MapType(byReference.ElementType));
            if (type is PointerType pointer)
                return new PointerType(MapType(pointer.ElementType));
            if (type is ArrayType array)
                return new ArrayType(MapType(array.ElementType), array.Rank);
            if (type is OptionalModifierType optional)
                return new OptionalModifierType(MapType(optional.ModifierType), MapType(optional.ElementType));
            if (type is RequiredModifierType required)
                return new RequiredModifierType(MapType(required.ModifierType), MapType(required.ElementType));
            if (type is GenericInstanceType generic)
            {
                var mapped = new GenericInstanceType(MapType(generic.ElementType));
                foreach (var argument in generic.GenericArguments)
                    mapped.GenericArguments.Add(MapType(argument));
                // 闭合实例能否被调用，取决于 IL2CPP 有没有为它生成过代码；工具本身证明不了，
                // 只能登记下来交给 GenericInstanceProbe 去元数据里找证据。
                _genericInstances.TryAdd(mapped.FullName, mapped);
                return mapped;
            }
            if (type is GenericParameter)
                throw new NotSupportedException(locator.Decorate(
                    $"Open generic parameter is not supported: {type.FullName}. " +
                    "Only closed generic instances can be rebound."));

            var assemblyName = AssemblyName(type.Scope);
            if (assemblyName == _targetAssemblyName)
                return FindTargetType(type);
            if (assemblyName == _implementationAssemblyName)
                throw new NotSupportedException(locator.Decorate(
                    $"Implementation-owned type closure is not supported: {type.FullName}"));
            return targetModule.ImportReference(type);
        }

        public MethodReference MapMethod(MethodReference method)
        {
            // 泛型方法实例（Foo.Bar<Baz>(...)）：先映射元素方法，再逐个映射实参重建。
            // 上游 CodeTranslator.writeMethod 的 IsGenericInstance 分支保存方法泛型实参。
            if (method is GenericInstanceMethod genericInstanceMethod)
            {
                var mappedElement = MapMethod(genericInstanceMethod.ElementMethod);
                var mappedInstance = new GenericInstanceMethod(mappedElement);
                foreach (var argument in genericInstanceMethod.GenericArguments)
                    mappedInstance.GenericArguments.Add(MapType(argument));
                return mappedInstance;
            }

            var assemblyName = AssemblyName(method.DeclaringType.Scope);
            if (assemblyName == _implementationAssemblyName)
            {
                // 实现程序集自有方法改绑到 grafted helper；它会经由 process_cfg 的
                // new methods 段被 InjectFix 翻译成补丁内部方法（CallType.Internal）。
                if (method.Resolve() is { } definition && _helpers.TryGetValue(definition, out var helper))
                    return helper;
                throw new InvalidDataException(locator.Decorate(
                    $"Implementation-owned call was not grafted as a helper: {method.FullName}"));
            }
            if (assemblyName != _targetAssemblyName)
                return targetModule.ImportReference(method);
            if (method.DeclaringType is ArrayType)
                throw new NotSupportedException(locator.Decorate(
                    $"Members on array types are not supported: {method.FullName}"));

            // 声明类型可以是闭合泛型实例（Container<Item>）。定义永远在元素类型
            // 上，所以先剥到元素类型再匹配；匹配用开放签名（定义里是 !0，不是实参）。
            var declaringType = method.DeclaringType;
            var definitionType = FindTargetType(declaringType.GetElementType());
            var match = FindTargetMethod(definitionType, method);
            return declaringType is GenericInstanceType instance
                ? MakeGeneric(match, MapType(instance))
                : match;
        }

        private MethodDefinition FindTargetMethod(TypeDefinition type, MethodReference method)
        {
            var matches = type.Methods.Where(candidate =>
                candidate.Name == method.Name &&
                candidate.HasThis == method.HasThis &&
                candidate.GenericParameters.Count == method.GenericParameters.Count &&
                candidate.Parameters.Count == method.Parameters.Count &&
                SameSignatureType(candidate.ReturnType, method.ReturnType) &&
                candidate.Parameters.Zip(method.Parameters).All(pair =>
                    SameSignatureType(pair.First.ParameterType, pair.Second.ParameterType))).ToList();
            return matches.Count switch
            {
                1 => matches[0],
                0 => throw new InvalidDataException(locator.Decorate(
                    $"Unable to rebind Assembly-CSharp method: {method.FullName}")),
                _ => throw new InvalidDataException(locator.Decorate(
                    $"Ambiguous Assembly-CSharp method rebind: {method.FullName}")),
            };
        }

        /// <summary>
        /// 在闭合泛型实例上重建成员引用。签名照抄**定义**的开放形态（!0 / !!0）——MemberRef
        /// 的签名本来就写在定义的泛型形参上，实参只出现在声明类型里。
        /// </summary>
        private static MethodReference MakeGeneric(MethodDefinition definition, TypeReference declaringType)
        {
            var reference = new MethodReference(definition.Name, definition.ReturnType, declaringType)
            {
                HasThis = definition.HasThis,
                ExplicitThis = definition.ExplicitThis,
                CallingConvention = definition.CallingConvention,
            };
            foreach (var parameter in definition.Parameters)
                reference.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
            foreach (var generic in definition.GenericParameters)
                reference.GenericParameters.Add(new GenericParameter(generic.Name, reference));
            return reference;
        }

        public FieldReference MapField(FieldReference field)
        {
            var assemblyName = AssemblyName(field.DeclaringType.Scope);
            if (assemblyName == _implementationAssemblyName)
                throw new NotSupportedException(locator.Decorate(
                    $"Implementation-owned fields are not supported: {field.FullName}"));
            if (assemblyName != _targetAssemblyName)
                return targetModule.ImportReference(field);

            var declaringType = field.DeclaringType;
            var definitionType = FindTargetType(declaringType.GetElementType());
            var matches = definitionType.Fields.Where(candidate =>
                candidate.Name == field.Name &&
                SameSignatureType(candidate.FieldType, field.FieldType)).ToList();
            var match = matches.Count switch
            {
                1 => matches[0],
                0 => throw new InvalidDataException(locator.Decorate(
                    $"Unable to rebind Assembly-CSharp field: {field.FullName}")),
                _ => throw new InvalidDataException(locator.Decorate(
                    $"Ambiguous Assembly-CSharp field rebind: {field.FullName}")),
            };
            if (declaringType is not GenericInstanceType instance)
                return match;

            // 上游不支持字段类型中的嵌套泛型形参：CodeTranslator.Serialize 的
            // "add field type" 循环调 addExternType(fieldType) 时**不传 contextType**，
            // 只对 fieldType.IsGenericParameter（裸 T）单独做一次 ResolveGenericArgument。
            // 字段类型里嵌着形参（T[]、List<T> …）时它既不会被解开、也没有上下文可解，
            // GetAssemblyQualifiedName 会在 ResolveGenericArgument(gp, null) 上抛 NRE。
            // 方法签名没有这个问题——writeMethod/addExternMethod 那边是传了 DeclaringType 的。
            // 在 build 阶段带源码位置报错，避免上游在 generate 阶段抛出 NRE。
            if (match.FieldType.ContainsGenericParameter && match.FieldType is not GenericParameter)
                throw new NotSupportedException(locator.Decorate(
                    $"Field {field.FullName} has type {match.FieldType.FullName}, which nests a type " +
                    "parameter of its declaring generic type. InjectFix only resolves a bare type parameter " +
                    "in field types, so this would crash CodeTranslator.Serialize. Reach the value through " +
                    "a property or a non-generic accessor instead."));
            return new FieldReference(match.Name, match.FieldType, MapType(instance));
        }

        /// <summary>
        /// 比较签名位置上的两个类型。泛型形参按「归属种类 + 序号」比而不是按名字：MemberRef 的
        /// 签名里写的是 !0 / !!0，Cecil 给它们的 Name 未必等于定义里的 T，按 FullName 比会全不匹配。
        /// </summary>
        private static bool SameSignatureType(TypeReference left, TypeReference right)
        {
            if (left is GenericParameter leftParameter)
                return right is GenericParameter rightParameter
                    && leftParameter.Type == rightParameter.Type
                    && leftParameter.Position == rightParameter.Position;
            if (right is GenericParameter)
                return false;

            if (left is GenericInstanceType leftInstance)
                return right is GenericInstanceType rightInstance
                    && SameSignatureType(leftInstance.ElementType, rightInstance.ElementType)
                    && leftInstance.GenericArguments.Count == rightInstance.GenericArguments.Count
                    && leftInstance.GenericArguments.Zip(rightInstance.GenericArguments)
                        .All(pair => SameSignatureType(pair.First, pair.Second));
            if (right is GenericInstanceType)
                return false;

            if (left is ArrayType leftArray)
                return right is ArrayType rightArray
                    && leftArray.Rank == rightArray.Rank
                    && SameSignatureType(leftArray.ElementType, rightArray.ElementType);
            if (left is ByReferenceType leftByReference)
                return right is ByReferenceType rightByReference
                    && SameSignatureType(leftByReference.ElementType, rightByReference.ElementType);
            if (left is PointerType leftPointer)
                return right is PointerType rightPointer
                    && SameSignatureType(leftPointer.ElementType, rightPointer.ElementType);

            return left.FullName == right.FullName;
        }

        private static string? AssemblyName(IMetadataScope scope) => scope switch
        {
            AssemblyNameReference assembly => assembly.Name,
            ModuleDefinition module => module.Assembly.Name.Name,
            ModuleReference module => Path.GetFileNameWithoutExtension(module.Name),
            _ => null,
        };
    }
}
