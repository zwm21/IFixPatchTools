using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IFixPatch.SourceBuilder.Core;

internal static class CecilModel
{
    public static IEnumerable<TypeDefinition> AllTypes(this ModuleDefinition module)
    {
        foreach (var type in module.Types)
        {
            yield return type;
            foreach (var nested in AllNestedTypes(type))
                yield return nested;
        }
    }

    private static IEnumerable<TypeDefinition> AllNestedTypes(TypeDefinition type)
    {
        foreach (var nested in type.NestedTypes)
        {
            yield return nested;
            foreach (var child in AllNestedTypes(nested))
                yield return child;
        }
    }

    public static DefaultAssemblyResolver CreateResolver(IEnumerable<string> directories)
    {
        var resolver = new DefaultAssemblyResolver();
        foreach (var directory in directories.Where(Directory.Exists).Distinct(StringComparer.Ordinal))
            resolver.AddSearchDirectory(directory);
        return resolver;
    }

    /// <summary>
    /// <paramref name="readSymbols"/> 只对自己编出来的实现程序集开。目标程序集是 Il2CppInspector
    /// 的 dummy dll，没有符号，强读会直接抛。即使开了也做降级：符号缺失或不匹配时按无符号读，
    /// 代价只是报错里少一个源码位置，不该让整个 build 失败。
    /// </summary>
    public static AssemblyDefinition ReadAssembly(
        string path, IEnumerable<string> directories, bool readSymbols = false)
    {
        var search = directories.Append(Path.GetDirectoryName(Path.GetFullPath(path))!).ToList();
        AssemblyDefinition Read(bool symbols) => AssemblyDefinition.ReadAssembly(path, new ReaderParameters
        {
            AssemblyResolver = CreateResolver(search),
            InMemory = true,
            ReadSymbols = symbols,
            ReadingMode = ReadingMode.Immediate,
        });

        if (!readSymbols || !File.Exists(Path.ChangeExtension(Path.GetFullPath(path), ".pdb")))
            return Read(false);
        try
        {
            return Read(true);
        }
        catch (Exception exception) when (exception is SymbolsNotFoundException or SymbolsNotMatchingException)
        {
            return Read(false);
        }
    }

    public static TypeDefinition FindType(this ModuleDefinition module, string fullName) =>
        module.AllTypes().SingleOrDefault(t => t.FullName == fullName)
        ?? throw new InvalidDataException($"Type not found: {fullName}");

    public static MethodDefinition FindMethod(this ModuleDefinition module, string fullName)
    {
        var matches = module.AllTypes().SelectMany(t => t.Methods)
            .Where(m => m.FullName == fullName).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidDataException($"Method not found: {fullName}"),
            _ => throw new InvalidDataException($"Method is ambiguous: {fullName}"),
        };
    }

    public static int? ReadIdTag(MethodDefinition method)
    {
        var attr = method.CustomAttributes.FirstOrDefault(a =>
            a.AttributeType.FullName is "IFix.Core.IDTagAttribute" or "IFix.IDTagAttribute");
        if (attr is null || attr.ConstructorArguments.Count != 1)
            return null;
        return Convert.ToInt32(attr.ConstructorArguments[0].Value);
    }
}
