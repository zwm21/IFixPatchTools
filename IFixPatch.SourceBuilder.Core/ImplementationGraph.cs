using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IFixPatch.SourceBuilder.Core;

public sealed partial class BodyGrafter
{
    private static bool NeedsGraph(IEnumerable<MethodDefinition> roots, ModuleDefinition module)
    {
        var seen = new HashSet<MethodDefinition>(); var queue = new Queue<MethodDefinition>(roots);
        bool Owned(TypeReference type) => AssemblyNameOf(type.GetElementType().Scope) == module.Assembly.Name.Name;
        while (queue.TryDequeue(out var method))
        {
            if (!seen.Add(method) || !method.HasBody) continue;
            if (Owned(method.ReturnType) || method.Parameters.Any(parameter => Owned(parameter.ParameterType)) || method.Body.Variables.Any(variable => Owned(variable.VariableType))) return true;
            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.Operand is FieldReference field && Owned(field.DeclaringType) || instruction.Operand is TypeReference type && Owned(type)) return true;
                if (instruction.Operand is MethodReference reference && Owned(reference.DeclaringType) && reference.Resolve() is { } callee)
                { if (!callee.IsStatic || callee.IsConstructor) return true; queue.Enqueue(callee); }
            }
        }
        return false;
    }

    private sealed class ImplementationGraph(
        BodyGrafter grafter, ReferenceMapper mapper, PreparedProfile prepared, PatchManifest manifest,
        ModuleDefinition source, ModuleDefinition target,
        List<(MethodDefinition Target, MethodDefinition Implementation)> entries)
    {
        public Dictionary<MethodDefinition, MethodDefinition> Methods { get; } = [];
        public Dictionary<MethodDefinition, List<MethodDefinition>> Calls { get; } = [];
        public List<TypeDefinition> NewTypes { get; } = [];
        public List<FieldDefinition> NewFields { get; } = [];
        public List<PropertyDefinition> NewProperties { get; } = [];
        private readonly HashSet<TypeDefinition> _types = [];
        private readonly HashSet<MethodDefinition> _methods = [];
        private readonly HashSet<FieldDefinition> _fields = [];
        private readonly Dictionary<string, TypeDefinition> _aliases = manifest.TypeBindings.ToDictionary(
            pair => pair.Key, pair => target.FindType(pair.Value), StringComparer.Ordinal);

        private bool Owned(TypeReference type) => AssemblyNameOf(type.GetElementType().Scope) == source.Assembly.Name.Name;
        private void Type(TypeReference? reference)
        {
            if (reference is null) return;
            if (reference is GenericInstanceType generic)
            {
                foreach (var argument in generic.GenericArguments) Type(argument);
                Type(generic.ElementType); return;
            }
            if (reference is TypeSpecification specification) { Type(specification.ElementType); return; }
            if (!Owned(reference) || reference.Resolve() is not { } definition || !_types.Add(definition)) return;
            if (definition.HasGenericParameters || definition.IsValueType || definition.IsInterface)
                throw new NotSupportedException($"New types must be non-generic reference classes: {definition.FullName}. Struct state machines and new interfaces are not supported.");
            if (!_aliases.ContainsKey(definition.FullName) && definition.BaseType is { FullName: not "System.Object" } baseType && !Owned(baseType))
                throw new NotSupportedException("New classes cannot inherit native classes: " + definition.FullName);
            Type(definition.BaseType);
            foreach (var item in definition.Interfaces) Type(item.InterfaceType);
            foreach (var field in definition.Fields) { _fields.Add(field); Type(field.FieldType); }
            // Instance members form the type contract. Static helper bodies are traversed only when reachable.
            foreach (var method in definition.Methods.Where(method => !method.IsStatic || method.IsConstructor || method.IsSpecialName)) Method(method);
        }

        private void Method(MethodDefinition method)
        {
            if (!_methods.Add(method)) return;
            if (method.HasGenericParameters || method.DeclaringType.HasGenericParameters)
                throw new NotSupportedException("Generic implementation methods require monomorphization: " + method.FullName);
            Type(method.DeclaringType); Type(method.ReturnType);
            foreach (var parameter in method.Parameters) Type(parameter.ParameterType);
            if (!method.HasBody) return;
            foreach (var variable in method.Body.Variables) Type(variable.VariableType);
            foreach (var handler in method.Body.ExceptionHandlers) Type(handler.CatchType);
            foreach (var instruction in method.Body.Instructions)
                switch (instruction.Operand)
                {
                    case MethodReference call:
                        Type(call.DeclaringType); Type(call.ReturnType);
                        foreach (var parameter in call.Parameters) Type(parameter.ParameterType);
                        if (call is GenericInstanceMethod generic) foreach (var argument in generic.GenericArguments) Type(argument);
                        if (Owned(call.DeclaringType) && call.Resolve() is { } callee)
                        {
                            if (entries.Any(entry => entry.Implementation == callee))
                                throw new NotSupportedException("Call the target method instead of another patch implementation: " + callee.FullName);
                            var edges = Calls.TryGetValue(method, out var list) ? list : Calls[method] = [];
                            if (!edges.Contains(callee)) edges.Add(callee);
                            Method(callee);
                        }
                        break;
                    case FieldReference field: Type(field.DeclaringType); Type(field.FieldType); break;
                    case TypeReference type: Type(type); break;
                }
        }

        public void Build()
        {
            foreach (var entry in entries) Method(entry.Implementation);
            foreach (var alias in _aliases)
            {
                if (!alias.Value.IsClass || alias.Value.IsValueType || alias.Value.HasGenericParameters)
                    throw new NotSupportedException("Type bindings require an existing non-generic reference class: " + alias.Value.FullName);
                var definition = source.FindType(alias.Key); Type(definition);
                foreach (var method in definition.Methods) Method(method);
            }
            foreach (var type in _types.OrderBy(type => type.FullName, StringComparer.Ordinal))
            {
                if (_aliases.TryGetValue(type.FullName, out var existing)) { mapper.OwnedTypes.Add(type, existing); continue; }
                var name = Sanitize(manifest.AssemblyName) + "_" + Sanitize(type.FullName);
                if (target.Types.Any(candidate => candidate.Namespace == "IFixPatch.Generated" && candidate.Name == name)) throw new InvalidDataException("New type name collision: " + name);
                var clone = new TypeDefinition("IFixPatch.Generated", name,
                    (type.Attributes & ~TypeAttributes.VisibilityMask) | TypeAttributes.Public, target.TypeSystem.Object);
                target.Types.Add(clone); mapper.OwnedTypes.Add(type, clone); NewTypes.Add(clone);
                Marker(type.CustomAttributes, clone.CustomAttributes);
            }
            foreach (var type in _types)
            {
                var clone = mapper.OwnedTypes[type];
                if (!_aliases.ContainsKey(type.FullName))
                {
                    clone.BaseType = mapper.MapType(type.BaseType);
                    foreach (var item in type.Interfaces) clone.Interfaces.Add(new InterfaceImplementation(mapper.MapType(item.InterfaceType)));
                }
            }
            foreach (var field in _fields)
            {
                var owner = mapper.OwnedTypes[field.DeclaringType];
                if (owner.Fields.Any(candidate => candidate.Name == field.Name)) throw new InvalidDataException("Field already exists: " + owner.FullName + "::" + field.Name);
                if (field.HasFieldRVA) throw new NotSupportedException("RVA data fields require an IL lowering pass: " + field.FullName);
                var attributes = (field.Attributes & ~FieldAttributes.FieldAccessMask) | FieldAttributes.Public;
                var clone = new FieldDefinition(field.Name, attributes, mapper.MapType(field.FieldType));
                if (field.HasConstant) clone.Constant = field.Constant;
                if (!field.IsStatic) Marker(field.CustomAttributes, clone.CustomAttributes);
                owner.Fields.Add(clone); mapper.OwnedFields.Add(field, clone);
                if (_aliases.ContainsKey(field.DeclaringType.FullName)) NewFields.Add(clone);
            }
            foreach (var entry in entries) { Methods.Add(entry.Implementation, entry.Target); mapper.RegisterHelper(entry.Implementation, entry.Target); }
            foreach (var method in _methods.Where(method => !Methods.ContainsKey(method)))
            {
                if (_aliases.ContainsKey(method.DeclaringType.FullName) && method.IsConstructor && method.Parameters.Count != 0)
                    throw new NotSupportedException("Extension initializers must use parameterless constructors: " + method.FullName);
                var owner = mapper.OwnedTypes[method.DeclaringType];
                MethodDefinition clone;
                var existing = owner.Methods.FirstOrDefault(candidate => candidate.Name == method.Name && candidate.Parameters.Count == method.Parameters.Count);
                if (_aliases.ContainsKey(method.DeclaringType.FullName) && existing is not null)
                {
                    if (!method.IsConstructor) throw new InvalidDataException("Method already exists: " + existing.FullName);
                    clone = existing;
                }
                else
                {
                    // Patch entries move to target types and still need access to their helpers.
                    var attributes = (method.Attributes & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Public;
                    clone = new MethodDefinition(method.Name, attributes, mapper.MapType(method.ReturnType));
                    foreach (var parameter in method.Parameters) clone.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, mapper.MapType(parameter.ParameterType)));
                    owner.Methods.Add(clone);
                }
                Marker(method.CustomAttributes, clone.CustomAttributes);
                Methods.Add(method, clone); mapper.RegisterHelper(method, clone);
            }
            foreach (var (original, clone) in Methods)
            {
                foreach (var implementation in original.Overrides) clone.Overrides.Add(mapper.MapMethod(implementation));
                if (original.HasBody) grafter.CloneBody(mapper, original, clone);
            }
            foreach (var type in _types)
            {
                var owner = mapper.OwnedTypes[type];
                foreach (var property in type.Properties.Where(property => property.GetMethod is not null && Methods.ContainsKey(property.GetMethod) || property.SetMethod is not null && Methods.ContainsKey(property.SetMethod)))
                {
                    if (owner.Properties.Any(candidate => candidate.Name == property.Name)) throw new InvalidDataException("Property already exists: " + owner.FullName + "::" + property.Name);
                    var clone = new PropertyDefinition(property.Name, property.Attributes, mapper.MapType(property.PropertyType))
                    { GetMethod = property.GetMethod is null ? null : Methods[property.GetMethod], SetMethod = property.SetMethod is null ? null : Methods[property.SetMethod] };
                    foreach (var parameter in property.Parameters) clone.Parameters.Add(new ParameterDefinition(mapper.MapType(parameter.ParameterType)));
                    owner.Properties.Add(clone);
                    if (_aliases.ContainsKey(type.FullName)) NewProperties.Add(clone);
                }
            }
            ValidateBridges();
        }

        private void Marker(Mono.Collections.Generic.Collection<CustomAttribute> sourceAttributes, Mono.Collections.Generic.Collection<CustomAttribute> destination)
        {
            foreach (var attribute in sourceAttributes.Where(attribute => attribute.AttributeType.FullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute"))
                destination.Add(new CustomAttribute(target.ImportReference(attribute.Constructor)));
        }

        private void ValidateBridges()
        {
            using var original = CecilModel.ReadAssembly(prepared.SourceAssemblyPath, prepared.ReferenceDirectories);
            var layout = InjectionLayout.Load(prepared);
            var bridge = original.MainModule.FindType(layout.InterfaceBridgeType);
            var provided = bridge.Interfaces.Select(item => item.InterfaceType.FullName).ToHashSet(StringComparer.Ordinal);
            var wrappers = original.MainModule.FindType(layout.DynamicWrapperType);
            foreach (var constructor in _methods.Where(method => method.HasBody).SelectMany(method => method.Body.Instructions)
                         .Where(instruction => instruction.OpCode.Code == Code.Newobj).Select(instruction => instruction.Operand).OfType<MethodReference>())
            {
                var delegateType = constructor.DeclaringType;
                var definition = delegateType.Resolve()
                    ?? throw new InvalidDataException("Unable to resolve constructed type: " + delegateType.FullName);
                if (definition.BaseType?.FullName != "System.MulticastDelegate") continue;
                var invoke = definition.Methods.Single(method => method.Name == "Invoke");
                TypeReference Substitute(TypeReference type)
                {
                    if (type is GenericParameter parameter && delegateType is GenericInstanceType instance) return instance.GenericArguments[parameter.Position];
                    if (type is ByReferenceType byRef) return new ByReferenceType(Substitute(byRef.ElementType));
                    if (type is ArrayType array) return new ArrayType(Substitute(array.ElementType), array.Rank);
                    if (type is GenericInstanceType generic)
                    {
                        var result = new GenericInstanceType(generic.ElementType);
                        foreach (var argument in generic.GenericArguments) result.GenericArguments.Add(Substitute(argument));
                        return result;
                    }
                    return type;
                }
                if (wrappers is null || !wrappers.Methods.Any(method => method.Name.StartsWith("__Gen_Wrap_", StringComparison.Ordinal) &&
                    method.ReturnType.FullName == Substitute(invoke.ReturnType).FullName && method.Parameters.Count == invoke.Parameters.Count &&
                    method.Parameters.Zip(invoke.Parameters).All(pair => pair.First.ParameterType.FullName == Substitute(pair.Second.ParameterType).FullName &&
                        pair.First.IsOut == pair.Second.IsOut && pair.First.IsIn == pair.Second.IsIn)))
                    throw new NotSupportedException("Target IFix delegate wrapper does not cover " + delegateType.FullName);
            }
            foreach (var type in NewTypes)
                foreach (var contract in type.Interfaces)
                    if (!provided.Contains(contract.InterfaceType.FullName))
                        throw new NotSupportedException($"Target IFix bridge does not implement {contract.InterfaceType.FullName}; this tool does not modify runtime bridges.");
        }
    }
}
