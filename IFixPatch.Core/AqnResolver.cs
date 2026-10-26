using Mono.Cecil;
// System.Reflection.Metadata 也有 TypeReference / ModuleDefinition，只取需要的解析器类型
using TypeName = System.Reflection.Metadata.TypeName;

namespace IFixPatch.Core;

/// <summary>
/// AQN → Cecil TypeReference。
///
/// 字符串解析交给 .NET 官方的 <see cref="TypeName"/>（不需要加载程序集，正确处理泛型实参、
/// 嵌套、byref、数组、指针）；引用构造交给 Cecil 的 MakeGenericInstanceType/MakeArrayType 等。
/// 这里只负责把前者的结构映射到后者，不自己解析类型名语法。
///
/// 之前手写的字符串切分会丢掉 `[[...]]` 里的类型实参，导致反编译出现
/// Dictionary&lt;TKey, TValue&gt; / !0 这类未实例化痕迹，这是替换它的原因。
/// </summary>
public sealed class AqnResolver(DummyDllSource src, ModuleDefinition module)
{
    private readonly DummyDllSource _src = src;
    private readonly ModuleDefinition _module = module;
    private readonly Dictionary<string, TypeReference?> _cache = [];

    public TypeReference? Resolve(string aqn)
    {
        if (_cache.TryGetValue(aqn, out var hit)) return hit;
        TypeReference? r;
        try { r = FromTypeName(TypeName.Parse(aqn)); }
        catch { r = null; }
        _cache[aqn] = r;
        return r;
    }

    private TypeReference? FromTypeName(TypeName tn)
    {
        if (tn.IsArray)
        {
            var el = FromTypeName(tn.GetElementType());
            if (el == null) return null;
            return tn.IsSZArray ? new ArrayType(el) : new ArrayType(el, tn.GetArrayRank());
        }
        if (tn.IsByRef)
        {
            var el = FromTypeName(tn.GetElementType());
            return el == null ? null : new ByReferenceType(el);
        }
        if (tn.IsPointer)
        {
            var el = FromTypeName(tn.GetElementType());
            return el == null ? null : new PointerType(el);
        }
        if (tn.IsConstructedGenericType)
        {
            var def = FromTypeName(tn.GetGenericTypeDefinition());
            if (def == null) return null;
            var args = new List<TypeReference>();
            foreach (var a in tn.GetGenericArguments())
            {
                var ar = FromTypeName(a);
                if (ar == null) return null;
                args.Add(ar);
            }
            var git = new GenericInstanceType(def);
            foreach (var a in args) git.GenericArguments.Add(a);
            return git;
        }

        // 简单/嵌套类型：用 Cecil 全名（嵌套用 '/'）到对应程序集里找定义
        var cecilName = tn.FullName.Replace('+', '/');
        var asmName = tn.AssemblyName?.Name;
        var td = _src.FindTypeByCecilName(cecilName, asmName);
        if (td == null) return null;
        try { return _module.ImportReference(td); } catch { return null; }
    }
}
