using Mono.Cecil;

namespace IFixPatch.SourceBuilder.Core;

/// <summary>
/// 扫描闭合泛型类型在目标元数据签名中的引用，并记录出处。
/// DummyDll 不包含原始方法体，因此未找到引用时只能给出警告。
/// 找到元数据引用不保证对应成员的 AOT 实现或运行时调用可用。
/// </summary>
internal static class GenericInstanceProbe
{
    /// <summary>实例全名 → 在哪儿找到的（一句人可读的出处）。</summary>
    public static Dictionary<string, string> ScanMetadata(ModuleDefinition module)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);

        void Record(TypeReference? type, Func<string> where)
        {
            switch (type)
            {
                case null:
                    return;
                case GenericInstanceType instance:
                    // 递归记录泛型实参中的嵌套元数据引用。
                    found.TryAdd(instance.FullName, where());
                    foreach (var argument in instance.GenericArguments)
                        Record(argument, where);
                    return;
                case TypeSpecification specification:
                    Record(specification.ElementType, where);
                    return;
            }
        }

        foreach (var type in module.AllTypes())
        {
            Record(type.BaseType, () => $"base type of {type.FullName}");
            foreach (var implementation in type.Interfaces)
                Record(implementation.InterfaceType, () => $"interface of {type.FullName}");
            foreach (var field in type.Fields)
                Record(field.FieldType, () => $"type of field {type.FullName}::{field.Name}");
            foreach (var property in type.Properties)
                Record(property.PropertyType, () => $"type of property {type.FullName}::{property.Name}");
            foreach (var @event in type.Events)
                Record(@event.EventType, () => $"type of event {type.FullName}::{@event.Name}");
            foreach (var method in type.Methods)
            {
                Record(method.ReturnType, () => $"return type of {method.FullName}");
                foreach (var parameter in method.Parameters)
                    Record(parameter.ParameterType, () => $"parameter of {method.FullName}");
            }
        }
        return found;
    }
}
