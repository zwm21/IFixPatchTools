using System.Text;

namespace IFixPatch.Core;

public sealed class PatchWriter(Stream s)
{
    public static void WriteFile(string path, PatchFile p)
    {
        using var fs = File.Create(path);
        new PatchWriter(fs).Write(p);
    }

    public static byte[] WriteToBytes(PatchFile p)
    {
        using var ms = new MemoryStream();
        new PatchWriter(ms).Write(p);
        return ms.ToArray();
    }

    private readonly BinaryWriter _w = new(s, Encoding.UTF8, leaveOpen: true);

    public void Write(PatchFile p)
    {
        _w.Write(p.Magic);
        _w.Write(p.InterfaceBridgeType ?? "");
        _w.Write(p.ExternTypes.Count);
        foreach (var t in p.ExternTypes) _w.Write(t);
        _w.Write(p.Methods.Count);
        foreach (var m in p.Methods)
        {
            _w.Write(m.Code.Count);
            foreach (var ins in m.Code)
            {
                _w.Write(ins.Code);
                _w.Write(ins.Operand);
            }
            _w.Write(m.Handlers.Count);
            foreach (var eh in m.Handlers)
            {
                _w.Write(eh.HandlerType);
                _w.Write(eh.CatchTypeId);
                _w.Write(eh.TryStart);
                _w.Write(eh.TryEnd);
                _w.Write(eh.HandlerStart);
                _w.Write(eh.HandlerEnd);
            }
        }
        _w.Write(p.ExternMethods.Count);
        foreach (var sig in p.ExternMethods) WriteMethodSig(sig);
        _w.Write(p.InternStrings.Count);
        foreach (var s in p.InternStrings) _w.Write(s);
        _w.Write(p.Fields.Count);
        foreach (var f in p.Fields)
        {
            _w.Write(f.IsNew);
            _w.Write(f.DeclaringTypeId);
            _w.Write(f.Name);
            if (f.IsNew)
            {
                _w.Write(f.FieldTypeId);
                _w.Write(f.CctorId);
            }
        }
        _w.Write(p.StaticFields.Count);
        foreach (var sf in p.StaticFields)
        {
            _w.Write(sf.FieldTypeId);
            _w.Write(sf.CctorId);
        }
        _w.Write(p.AnonStoreys.Count);
        foreach (var a in p.AnonStoreys)
        {
            _w.Write(a.FieldTypes.Count);
            foreach (var ft in a.FieldTypes) _w.Write(ft);
            _w.Write(a.CtorId);
            _w.Write(a.CtorParamNum);
            if (!a.HasInterfaces && a.Interfaces.Count == 0)
            {
                _w.Write(0);
            }
            else
            {
                _w.Write(a.Interfaces.Count);
                foreach (var itf in a.Interfaces)
                {
                    _w.Write(itf.TypeId);
                    foreach (var mid in itf.MethodIds) _w.Write(mid);
                }
            }
            _w.Write(a.VTable.Count);
            foreach (var v in a.VTable) _w.Write(v);
        }
        _w.Write(p.WrapperManagerType ?? "");
        _w.Write(p.IdMapSuffix ?? "");
        _w.Write(p.FixList.Count);
        foreach (var fx in p.FixList)
        {
            WriteMethodSig(fx.Method);
            _w.Write(fx.MethodId);
        }
        _w.Write(p.NewClasses.Count);
        foreach (var nc in p.NewClasses) _w.Write(nc);
    }

    private void WriteMethodSig(ExternMethodSig sig)
    {
        _w.Write(sig.IsGenericInstance);
        _w.Write(sig.DeclaringTypeId);
        _w.Write(sig.Name);
        if (sig.IsGenericInstance)
        {
            _w.Write(sig.GenericArgTypeIds.Count);
            foreach (var id in sig.GenericArgTypeIds) _w.Write(id);
            _w.Write(sig.Params.Count);
            foreach (var pr in sig.Params)
            {
                _w.Write(pr.IsGeneric);
                if (pr.IsGeneric) _w.Write(pr.GenericName ?? "");
                else _w.Write(pr.TypeId);
            }
        }
        else
        {
            _w.Write(sig.Params.Count);
            foreach (var pr in sig.Params) _w.Write(pr.TypeId);
        }
    }
}
