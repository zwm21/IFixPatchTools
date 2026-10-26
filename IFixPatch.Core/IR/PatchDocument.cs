using System.Text.Json;
using System.Text.Json.Serialization;

namespace IFixPatch.Core.IR;

public sealed class PatchDocument
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public ulong Magic { get; set; }
    public Dictionary<string, int> OpCodeMap { get; set; } = [];
    public string InterfaceBridgeType { get; set; } = "";
    public List<string> ExternTypes { get; set; } = [];
    public List<MethodIr> Methods { get; set; } = [];
    public List<ExternMethodIr> ExternMethods { get; set; } = [];
    public List<string> InternStrings { get; set; } = [];
    public List<FieldIr> Fields { get; set; } = [];
    public List<StaticFieldIr> StaticFields { get; set; } = [];
    public List<AnonStoreyIr> AnonStoreys { get; set; } = [];
    public string WrapperManagerType { get; set; } = "";
    public string IdMapSuffix { get; set; } = "";
    public List<FixEntryIr> FixList { get; set; } = [];
    public List<string> NewClasses { get; set; } = [];

    public static PatchDocument FromPatchFile(PatchFile p, OpCodeMap? map)
    {
        var doc = new PatchDocument
        {
            Magic = p.Magic,
            InterfaceBridgeType = p.InterfaceBridgeType,
            ExternTypes = [.. p.ExternTypes],
            InternStrings = [.. p.InternStrings],
            WrapperManagerType = p.WrapperManagerType,
            IdMapSuffix = p.IdMapSuffix,
            NewClasses = [.. p.NewClasses],
        };
        if (map != null && !map.IsEmpty)
            foreach (var kv in map.Enumerate()) doc.OpCodeMap[kv.Value] = kv.Key;

        // Keep shuffled value -> name for round-trip; serialize as name->value
        // Enumerate() helpers defined in OpCodeMap partial
        foreach (var m in p.Methods)
        {
            var mi = new MethodIr { Id = m.Id };
            foreach (var eh in m.Handlers) mi.Handlers.Add(new EhIr(eh));
            // store raw code as OpName + Operand; for borrowed slots keep as-is to stay lossless
            for (int i = 0; i < m.Code.Count; i++)
            {
                var ins = m.Code[i];
                string op = map is not null && map.TryName(ins.Code, out var n) ? n : $"code#{ins.Code}";
                mi.Code.Add(new InstrIr { Op = op, Operand = ins.Operand });
            }
            doc.Methods.Add(mi);
        }
        foreach (var em in p.ExternMethods) doc.ExternMethods.Add(ExternMethodIr.FromModel(em));
        foreach (var f in p.Fields) doc.Fields.Add(FieldIr.FromModel(f));
        foreach (var sf in p.StaticFields) doc.StaticFields.Add(new StaticFieldIr { FieldTypeId = sf.FieldTypeId, CctorId = sf.CctorId });
        foreach (var a in p.AnonStoreys) doc.AnonStoreys.Add(AnonStoreyIr.FromModel(a));
        foreach (var fx in p.FixList) doc.FixList.Add(new FixEntryIr { Method = ExternMethodIr.FromModel(fx.Method), MethodId = fx.MethodId });
        return doc;
    }

    public PatchFile ToPatchFile(out OpCodeMap map)
    {
        map = new OpCodeMap();
        if (OpCodeMap.Count > 0) map.LoadFromNameToValue(OpCodeMap);
        var p = new PatchFile
        {
            Magic = Magic,
            InterfaceBridgeType = InterfaceBridgeType,
            WrapperManagerType = WrapperManagerType,
            IdMapSuffix = IdMapSuffix,
        };
        p.ExternTypes.AddRange(ExternTypes);
        p.InternStrings.AddRange(InternStrings);
        p.NewClasses.AddRange(NewClasses);
        // reverse map for code
        var nameToCode = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var kv in OpCodeMap) nameToCode[kv.Key] = kv.Value;

        var maxId = Methods.Count == 0 ? -1 : Methods.Max(m => m.Id);
        var byId = Methods.ToDictionary(m => m.Id);
        for (int id = 0; id <= maxId; id++)
        {
            var m = new IFixMethod { Id = id };
            if (byId.TryGetValue(id, out var mir))
            {
                foreach (var ir in mir.Code)
                {
                    int code = ir.Op.StartsWith("code#") && int.TryParse(ir.Op[5..], out var v) ? v : nameToCode.TryGetValue(ir.Op, out var c) ? c : 0;
                    m.Code.Add(new Instr { Code = code, Operand = ir.Operand });
                }
                foreach (var eh in mir.Handlers) m.Handlers.Add(eh.ToModel());
            }
            p.Methods.Add(m);
        }
        foreach (var em in ExternMethods) p.ExternMethods.Add(em.ToModel());
        foreach (var f in Fields) p.Fields.Add(f.ToModel());
        foreach (var sf in StaticFields) p.StaticFields.Add(new StaticFieldEntry { FieldTypeId = sf.FieldTypeId, CctorId = sf.CctorId });
        foreach (var a in AnonStoreys) p.AnonStoreys.Add(a.ToModel());
        foreach (var fx in FixList) p.FixList.Add(new FixEntry { Method = fx.Method.ToModel(), MethodId = fx.MethodId });
        p.FullyParsed = true;
        return p;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);
    public static PatchDocument FromJson(string json) => JsonSerializer.Deserialize<PatchDocument>(json, JsonOpts)!;
    public void SaveJson(string path) => File.WriteAllText(path, ToJson());
    public static PatchDocument LoadJson(string path) => FromJson(File.ReadAllText(path));

    public List<string> Validate()
    {
        var errs = new List<string>();
        if (Version != CurrentVersion) errs.Add($"version {Version} != {CurrentVersion}");
        if (OpCodeMap.Count > 0)
        {
            var canon = new HashSet<string>(OpCodeMap.Keys);
            // allow partial map but check duplicates already handled by dict
            foreach (var kv in OpCodeMap) if (kv.Value < 0) errs.Add($"opcode {kv.Key} has negative value {kv.Value}");
        }
        for (int i = 0; i < ExternTypes.Count; i++) if (string.IsNullOrWhiteSpace(ExternTypes[i])) errs.Add($"externTypes[{i}] empty");
        var maxMethodId = Methods.Count == 0 ? -1 : Methods.Max(m => m.Id);
        if (Methods.Count != maxMethodId + 1 && Methods.Count > 0) errs.Add($"methods not dense: count {Methods.Count} maxId {maxMethodId}");
        var methodIds = new HashSet<int>(Methods.Select(m => m.Id));
        foreach (var fx in FixList) if (!methodIds.Contains(fx.MethodId) && fx.MethodId >= 0) errs.Add($"fixList methodId {fx.MethodId} not in methods");
        foreach (var m in Methods)
        {
            foreach (var eh in m.Handlers)
            {
                if (eh.TryStart < 0 || eh.TryEnd < eh.TryStart || eh.HandlerStart < 0) errs.Add($"method#{m.Id} eh range invalid");
                if (eh.HandlerEnd != -1 && eh.HandlerEnd < eh.HandlerStart) errs.Add($"method#{m.Id} eh HandlerEnd invalid");
                if (eh.CatchTypeId != -1 && (eh.CatchTypeId < 0 || eh.CatchTypeId >= ExternTypes.Count)) errs.Add($"method#{m.Id} eh CatchTypeId out of range");
            }
            foreach (var ins in m.Code)
                if (string.IsNullOrWhiteSpace(ins.Op)) errs.Add($"method#{m.Id} empty op");
        }
        return errs;
    }
}

public sealed class MethodIr
{
    public int Id { get; set; }
    public List<InstrIr> Code { get; set; } = [];
    public List<EhIr> Handlers { get; set; } = [];
}
public sealed class InstrIr
{
    public string Op { get; set; } = "Nop";
    public int Operand { get; set; }
}
public sealed class EhIr
{
    public int HandlerType { get; set; }
    public int CatchTypeId { get; set; }
    public int TryStart { get; set; }
    public int TryEnd { get; set; }
    public int HandlerStart { get; set; }
    public int HandlerEnd { get; set; }
    public EhIr() { }
    public EhIr(Eh eh) { HandlerType = eh.HandlerType; CatchTypeId = eh.CatchTypeId; TryStart = eh.TryStart; TryEnd = eh.TryEnd; HandlerStart = eh.HandlerStart; HandlerEnd = eh.HandlerEnd; }
    public Eh ToModel() => new() { HandlerType = HandlerType, CatchTypeId = CatchTypeId, TryStart = TryStart, TryEnd = TryEnd, HandlerStart = HandlerStart, HandlerEnd = HandlerEnd };
}
public sealed class ExternMethodIr
{
    public bool IsGenericInstance { get; set; }
    public int DeclaringTypeId { get; set; }
    public string Name { get; set; } = "";
    public List<int> GenericArgTypeIds { get; set; } = [];
    public List<ParamIr> Params { get; set; } = [];
    public static ExternMethodIr FromModel(ExternMethodSig s) => new() { IsGenericInstance = s.IsGenericInstance, DeclaringTypeId = s.DeclaringTypeId, Name = s.Name, GenericArgTypeIds = [.. s.GenericArgTypeIds], Params = [.. s.Params.Select(p => new ParamIr { IsGeneric = p.IsGeneric, GenericName = p.GenericName, TypeId = p.TypeId })] };
    public ExternMethodSig ToModel() => new() { IsGenericInstance = IsGenericInstance, DeclaringTypeId = DeclaringTypeId, Name = Name, GenericArgTypeIds = [.. GenericArgTypeIds], Params = [.. Params.Select(p => new ExternMethodSig.Param { IsGeneric = p.IsGeneric, GenericName = p.GenericName, TypeId = p.TypeId })] };
}
public sealed class ParamIr
{
    public bool IsGeneric { get; set; }
    public string? GenericName { get; set; }
    public int TypeId { get; set; } = -1;
}
public sealed class FieldIr
{
    public bool IsNew { get; set; }
    public int DeclaringTypeId { get; set; }
    public string Name { get; set; } = "";
    public int FieldTypeId { get; set; } = -1;
    public int CctorId { get; set; } = -1;
    public static FieldIr FromModel(FieldEntry f) => new() { IsNew = f.IsNew, DeclaringTypeId = f.DeclaringTypeId, Name = f.Name, FieldTypeId = f.FieldTypeId, CctorId = f.CctorId };
    public FieldEntry ToModel() => new() { IsNew = IsNew, DeclaringTypeId = DeclaringTypeId, Name = Name, FieldTypeId = FieldTypeId, CctorId = CctorId };
}
public sealed class StaticFieldIr
{
    public int FieldTypeId { get; set; }
    public int CctorId { get; set; }
}
public sealed class AnonStoreyIr
{
    public List<int> FieldTypes { get; set; } = [];
    public int CtorId { get; set; }
    public int CtorParamNum { get; set; }
    public List<ItfSlotsIr> Interfaces { get; set; } = [];
    public List<int> VTable { get; set; } = [];
    public static AnonStoreyIr FromModel(AnonStorey a) => new() { FieldTypes = [.. a.FieldTypes], CtorId = a.CtorId, CtorParamNum = a.CtorParamNum, Interfaces = [.. a.Interfaces.Select(it => new ItfSlotsIr { TypeId = it.TypeId, MethodIds = [.. it.MethodIds] })], VTable = [.. a.VTable] };
    public AnonStorey ToModel() => new() { FieldTypes = [.. FieldTypes], CtorId = CtorId, CtorParamNum = CtorParamNum, HasInterfaces = Interfaces.Count > 0, Interfaces = [.. Interfaces.Select(it => new AnonStorey.ItfSlots { TypeId = it.TypeId, MethodIds = [.. it.MethodIds] })], VTable = [.. VTable] };
}
public sealed class ItfSlotsIr
{
    public int TypeId { get; set; }
    public List<int> MethodIds { get; set; } = [];
}
public sealed class FixEntryIr
{
    public ExternMethodIr Method { get; set; } = new();
    public int MethodId { get; set; }
}
