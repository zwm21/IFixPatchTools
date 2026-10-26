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
    /// <paramref name="readSymbols"/> 用于补丁实现程序集的源码定位。
    /// 符号缺失或不匹配时按无符号读取；目标元数据壳不要求携带符号。
    /// </summary>
    public static AssemblyDefinition ReadAssembly(
        string path, IEnumerable<string> directories, bool readSymbols = false)
    {
        var search = directories.Append(Path.GetDirectoryName(Path.GetFullPath(path))!).ToList();
        AssemblyDefinition Read(bool symbols)
        {
            var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters
            {
                AssemblyResolver = CreateResolver(search),
                InMemory = true,
                ReadSymbols = symbols,
                ReadingMode = ReadingMode.Immediate,
            });
            try
            {
                // Preparation and grafting rebuild the main module only. Reject module-scoped
                // types before writing an artifact that would silently omit part of an assembly.
                var moduleTypes = assembly.MainModule.GetTypeReferences().Where(type => type.Scope is ModuleReference).Select(type => type.FullName).ToArray();
                if (assembly.Modules.Count != 1 || moduleTypes.Length != 0)
                    throw new NotSupportedException("Multi-module assemblies and module-scoped type references are not supported: " + path + ". " + string.Join(", ", moduleTypes));
                return assembly;
            }
            catch
            {
                assembly.Dispose();
                throw;
            }
        }

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
