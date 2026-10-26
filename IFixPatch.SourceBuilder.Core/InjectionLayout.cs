using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using IFixPatch.Core;
using Mono.Cecil;

namespace IFixPatch.SourceBuilder.Core;

/// <summary>Validated metadata inventory for the supported upstream injection layout.</summary>
public sealed class InjectionLayout
{
    public string Format { get; set; } = "injectfix-v1";
    public string TargetAssemblyIdentity { get; set; } = "";
    public string RuntimeAssemblyIdentity { get; set; } = "";
    public string DynamicWrapperType { get; set; } = "";
    public string InterfaceBridgeType { get; set; } = "";
    public string WrapperManagerType { get; set; } = "";
    public List<IdMapSegment> IdMapSegments { get; set; } = [];
    public List<string> RedirectTypes { get; set; } = [];
    public List<string> BaseProxyMethods { get; set; } = [];

    [JsonIgnore]
    public IEnumerable<string> RemovedTypes => new[] { DynamicWrapperType, InterfaceBridgeType, WrapperManagerType }
        .Concat(IdMapSegments.Select(segment => segment.Type)).Concat(RedirectTypes);

    public static InjectionLayout Load(PreparedProfile prepared)
    {
        var result = SourceBuilderJson.Load<InjectionLayout>(prepared.InjectionLayoutPath);
        if (result.Format != "injectfix-v1" || result.TargetAssemblyIdentity != prepared.TargetAssemblyIdentity)
            throw new InvalidDataException("Unsupported or mismatched injection layout; run prepare again.");
        return result;
    }

    public void ValidatePatchIdentity(PatchFile patch)
    {
        void Assembly(string value)
        {
            if (new System.Reflection.AssemblyName(value).FullName != TargetAssemblyIdentity)
                throw new InvalidDataException("Patch assembly identity differs from the prepared target: " + value);
        }
        void Role(string aqn, string expected)
        {
            var comma = aqn.IndexOf(',');
            if (comma < 0 || aqn[..comma].Trim() != expected)
                throw new InvalidDataException("Patch injection type differs from the prepared layout: " + aqn);
            Assembly(aqn[(comma + 1)..].Trim());
        }
        if (!patch.FullyParsed || patch.TrailingBytes != 0)
            throw new InvalidDataException("Injection identity checks require a fully parsed patch without trailing bytes.");
        Role(patch.InterfaceBridgeType, InterfaceBridgeType);
        Role(patch.WrapperManagerType, WrapperManagerType);
        if (!patch.IdMapSuffix.StartsWith(',')) throw new InvalidDataException("Invalid patch IDMAP assembly suffix.");
        Assembly(patch.IdMapSuffix[1..].Trim());
        foreach (var fix in patch.FixList)
        {
            if (fix.Method.DeclaringTypeId < 0 || fix.Method.DeclaringTypeId >= patch.ExternTypes.Count)
                throw new InvalidDataException("Invalid fix declaring type index.");
            var aqn = patch.ExternTypes[fix.Method.DeclaringTypeId];
            var depth = 0;
            var comma = -1;
            for (var index = 0; index < aqn.Length; index++)
            {
                if (aqn[index] == '[') depth++;
                else if (aqn[index] == ']') depth--;
                else if (aqn[index] == ',' && depth == 0) { comma = index; break; }
            }
            if (comma < 0) throw new InvalidDataException("Fix declaring type has no assembly identity: " + aqn);
            Assembly(aqn[(comma + 1)..].Trim());
        }
    }
}

public sealed class IdMapSegment
{
    public int Index { get; set; }
    public string Type { get; set; } = "";
    public List<IdMapCatalogEntry> Entries { get; set; } = [];
}

internal static class InjectionInspector
{
    public static InjectionLayout Discover(AssemblyDefinition assembly, string runtimeIdentity)
    {
        var module = assembly.MainModule;
        var types = module.AllTypes().ToArray();
        TypeDefinition Required(string name)
        {
            var candidates = types.Where(type => type.FullName == name).ToArray();
            return candidates.Length == 1 ? candidates[0]
                : throw new InvalidDataException($"Expected one injected type {name}, found {candidates.Length}. Unsupported injection layout.");
        }
        void Require(bool condition, string detail)
        {
            if (!condition) throw new InvalidDataException("Unsupported injection layout: " + detail);
        }
        bool RuntimeType(TypeReference? type, string name) => type?.FullName == "IFix.Core." + name &&
            type.Scope is AssemblyNameReference reference && reference.FullName == runtimeIdentity;
        bool TargetType(TypeReference type, string expected) => type.FullName == expected && type.Scope switch
        {
            ModuleDefinition owner => owner.Assembly.Name.FullName == assembly.Name.FullName,
            AssemblyNameReference reference => reference.FullName == assembly.Name.FullName,
            _ => false,
        };
        bool SignatureType(TypeReference type, string expected) => type.FullName == expected &&
            (expected.StartsWith("IFix.Core.", StringComparison.Ordinal) ? RuntimeType(type, expected[10..]) :
                expected == "IFix.ILFixDynamicMethodWrapper" ? TargetType(type, expected) : true);
        bool Method(TypeDefinition type, string name, bool isStatic, string result, params string[] parameters) =>
            type.Methods.Count(method => method.Name == name && method.IsStatic == isStatic && SignatureType(method.ReturnType, result) &&
                method.Parameters.Count == parameters.Length &&
                method.Parameters.Zip(parameters).All(pair => SignatureType(pair.First.ParameterType, pair.Second))) == 1;

        var wrapper = Required("IFix.ILFixDynamicMethodWrapper");
        var bridge = Required("IFix.ILFixInterfaceBridge");
        var manager = Required("IFix.WrappersManagerImpl");
        Require(wrapper.IsClass && !wrapper.HasGenericParameters && wrapper.BaseType?.FullName == "System.Object", wrapper.FullName);
        Require(wrapper.Fields.Count(field => field.Name == "virtualMachine" && !field.IsStatic && RuntimeType(field.FieldType, "VirtualMachine")) == 1 &&
            wrapper.Fields.Count(field => field.Name == "methodId" && !field.IsStatic && field.FieldType.FullName == "System.Int32") == 1 &&
            wrapper.Fields.Count(field => field.Name == "wrapperArray" && field.IsStatic && TargetType(field.FieldType, wrapper.FullName + "[]")) == 1 &&
            Method(wrapper, ".ctor", false, "System.Void", "IFix.Core.VirtualMachine", "System.Int32", "System.Object"), "dynamic wrapper contract");
        Require(bridge.IsClass && !bridge.HasGenericParameters && RuntimeType(bridge.BaseType, "AnonymousStorey"), "interface bridge base");
        Require(manager.IsClass && !manager.HasGenericParameters && manager.Interfaces.Count(item => RuntimeType(item.InterfaceType, "WrappersManager")) == 1 &&
            Method(manager, ".ctor", false, "System.Void", "IFix.Core.VirtualMachine") &&
            Method(manager, "GetPatch", true, wrapper.FullName, "System.Int32") &&
            Method(manager, "IsPatched", true, "System.Boolean", "System.Int32") &&
            Method(manager, "CreateWrapper", false, "System.Object", "System.Int32") &&
            Method(manager, "InitWrapperArray", false, "System.Object", "System.Int32") &&
            Method(manager, "CreateDelegate", false, "System.Delegate", "System.Type", "System.Int32", "System.Object") &&
            Method(manager, "CreateBridge", false, "IFix.Core.AnonymousStorey", "System.Int32", "System.Int32[]", "System.Int32", "System.Int32[]", "System.Int32[]", "IFix.Core.VirtualMachine"),
            "wrapper manager contract");

        var layout = new InjectionLayout
        {
            TargetAssemblyIdentity = assembly.Name.FullName, RuntimeAssemblyIdentity = runtimeIdentity,
            DynamicWrapperType = wrapper.FullName, InterfaceBridgeType = bridge.FullName, WrapperManagerType = manager.FullName,
        };
        var fields = new HashSet<string>(StringComparer.Ordinal);
        var slots = new HashSet<int>();
        foreach (var type in module.Types.Where(type => type.Namespace == "IFix"))
        {
            var match = Regex.Match(type.Name, @"^IDMAP(0|[1-9][0-9]*)$");
            Require(match.Success || !Regex.IsMatch(type.Name, @"^IDMAP[0-9]+$"), "noncanonical IDMAP segment name: " + type.FullName);
            if (!match.Success) continue;
            Require(int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < 100,
                "IDMAP index must be within the upstream loader range 0..99: " + type.FullName);
            Require(type.IsEnum && type.Fields.Count(field => field.Name == "value__" && field.FieldType.FullName == "System.Int32" && !field.IsStatic) == 1,
                "IDMAP must be an Int32 enum: " + type.FullName);
            var segment = new IdMapSegment { Index = index, Type = type.FullName };
            foreach (var field in type.Fields.Where(field => field.Name != "value__"))
            {
                Require(field.IsStatic && field.IsLiteral && field.HasConstant && field.Constant is int && field.FieldType.FullName == type.FullName,
                    "invalid IDMAP field: " + field.FullName);
                var slot = (int)field.Constant;
                Require(slot >= 0 && fields.Add(field.Name) && slots.Add(slot), "duplicate or negative IDMAP mapping: " + field.FullName);
                segment.Entries.Add(new IdMapCatalogEntry { Field = field.Name, Slot = slot, DeclaringType = type.FullName });
            }
            layout.IdMapSegments.Add(segment);
        }
        layout.IdMapSegments = layout.IdMapSegments.OrderBy(segment => segment.Index).ToList();
        Require(layout.IdMapSegments.Count > 0 && layout.IdMapSegments.Select(segment => segment.Index).SequenceEqual(Enumerable.Range(0, layout.IdMapSegments.Count)),
            "IDMAP segments must start at IDMAP0 and be contiguous");

        static bool Redirect(TypeDefinition type) => type.Namespace == "IFix.RedirectTo" ||
            type.Namespace.StartsWith("IFix.RedirectTo.", StringComparison.Ordinal) || type.DeclaringType is not null && Redirect(type.DeclaringType);
        foreach (var type in types.Where(Redirect))
        {
            Require(type.IsAbstract && type.IsSealed && type.BaseType?.FullName == "System.Object" &&
                type.Fields.All(field => field.IsStatic && TargetType(field.FieldType, wrapper.FullName)) && type.Methods.Count == 0,
                "redirect type contract: " + type.FullName);
            layout.RedirectTypes.Add(type.FullName);
        }
        foreach (var type in types)
            foreach (var proxy in type.Methods.Where(method => method.Name.StartsWith("<>iFixBaseProxy_", StringComparison.Ordinal) ||
                         method.Name.StartsWith("__iFixBaseProxy_", StringComparison.Ordinal)))
            {
                var name = proxy.Name[16..];
                Require(!proxy.IsStatic && type.Methods.Any(method => method.Name == name && method.ReturnType.FullName == proxy.ReturnType.FullName &&
                    method.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(proxy.Parameters.Select(parameter => parameter.ParameterType.FullName))),
                    "base proxy contract: " + proxy.FullName);
                layout.BaseProxyMethods.Add(proxy.FullName);
            }
        return layout;
    }
}
