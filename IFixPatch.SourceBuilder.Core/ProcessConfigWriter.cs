using Mono.Cecil;

namespace IFixPatch.SourceBuilder.Core;

/// <summary>
/// 写 InjectFix 的 process_cfg。
///
/// 上游 <c>PatchGenerateConfigure</c> 按固定顺序读五段：
/// <list type="number">
/// <item>patchMethods —— 替换现有方法（IFixAttribute / InjectType.Switch）</item>
/// <item>newMethods —— 原程序集里不存在的方法，编译进 VM 作为内部方法（helper）</item>
/// <item>newFields</item>
/// <item>newProperties</item>
/// <item>newClasses</item>
/// </list>
/// 第 1、2 段共用同一套编码（声明类型 → 名字 → 返回类型 → 参数表），因此 <see cref="WriteMethods"/> 两处复用。
///
/// 第 2 段就是 helper 通道：<c>CodeTranslator.getMethodId</c> 在 <c>mode == Patch &amp;&amp; isNewMethod(method)</c>
/// 时把被调用方递归翻译成内部方法（<c>CallType.Internal</c>），既不需要 IDMAP slot 也不需要 Native wrapper。
/// </summary>
public static class ProcessConfigWriter
{
    public static void Write(
        string path,
        IReadOnlyCollection<MethodDefinition> patchedMethods,
        IReadOnlyCollection<MethodDefinition> newMethods,
        IReadOnlyCollection<FieldDefinition>? newFields = null,
        IReadOnlyCollection<PropertyDefinition>? newProperties = null,
        IReadOnlyCollection<TypeDefinition>? newClasses = null)
    {
        using var writer = new BinaryWriter(File.Create(path));
        WriteMethods(writer, patchedMethods);
        WriteMethods(writer, newMethods);
        var fields = (newFields ?? []).GroupBy(field => field.DeclaringType.FullName).ToArray();
        writer.Write(fields.Length);
        foreach (var group in fields)
        {
            writer.Write(group.Key); writer.Write(group.Count());
            foreach (var field in group) { writer.Write(field.Name); writer.Write(field.FieldType.FullName); }
        }
        var properties = (newProperties ?? []).GroupBy(property => property.DeclaringType.FullName).ToArray();
        writer.Write(properties.Length);
        foreach (var group in properties)
        {
            writer.Write(group.Key); writer.Write(group.Count());
            foreach (var property in group) { writer.Write(property.Name); writer.Write(property.PropertyType.FullName); }
        }
        writer.Write(newClasses?.Count ?? 0);
        foreach (var type in newClasses ?? []) writer.Write(type.FullName);
    }

    private static void WriteMethods(BinaryWriter writer, IReadOnlyCollection<MethodDefinition> methods)
    {
        var groups = methods.GroupBy(method => method.DeclaringType.FullName).ToList();
        writer.Write(groups.Count);
        foreach (var group in groups)
        {
            writer.Write(group.Key);
            writer.Write(group.Count());
            foreach (var method in group)
            {
                writer.Write(method.Name);
                writer.Write(method.ReturnType.FullName);
                writer.Write(method.Parameters.Count);
                foreach (var parameter in method.Parameters)
                {
                    writer.Write(parameter.IsOut);
                    var type = parameter.ParameterType is RequiredModifierType required
                        ? required.ElementType
                        : parameter.ParameterType;
                    writer.Write(type.FullName);
                }
            }
        }
    }

}
