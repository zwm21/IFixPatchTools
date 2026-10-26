using Mono.Cecil;
using Mono.Cecil.Cil;
using IFixPatch.Core;

namespace IFixPatch.SourceBuilder.Core;

public sealed class AssemblyPreparer
{
    public PreparedProfile Prepare(string profilePath, string outputDirectory)
    {
        profilePath = Path.GetFullPath(profilePath);
        outputDirectory = Path.GetFullPath(outputDirectory);
        var profile = PathAndHash.LoadResolvedProfile(profilePath);
        ValidateProfile(profile);

        Directory.CreateDirectory(outputDirectory);
        var cleanDirectory = Path.Combine(outputDirectory, "clean-shell");
        var compileRefDirectory = Path.Combine(outputDirectory, "compile-ref");
        var adapterDirectory = Path.Combine(outputDirectory, "adapter-ref");
        Directory.CreateDirectory(cleanDirectory);
        Directory.CreateDirectory(compileRefDirectory);
        Directory.CreateDirectory(adapterDirectory);

        var identity = System.Reflection.AssemblyName.GetAssemblyName(profile.AssemblyPath);
        var assemblyName = identity.Name ?? throw new InvalidDataException("Target assembly has no name.");
        PathAndHash.RequireAssemblyFileName(assemblyName, "Target assembly name");
        var cleanPath = Path.Combine(cleanDirectory, assemblyName + ".dll");
        var compileRefPath = Path.Combine(compileRefDirectory, assemblyName + ".dll");
        var adapterCoreSourcePath = Path.Combine(adapterDirectory, "Instruction.g.cs");
        var catalogPath = Path.Combine(outputDirectory, "catalog.json");
        var fieldCatalogPath = Path.Combine(outputDirectory, "field-catalog.json");
        var layoutPath = Path.Combine(outputDirectory, "injection-layout.json");
        var report = new PrepareReport { Profile = profile.Name, TargetAssemblyIdentity = identity.FullName, InjectionLayoutPath = layoutPath };

        AssemblyCatalog catalog;
        FieldCatalog fieldCatalog;
        InjectionLayout layout;
        using (var source = CecilModel.ReadAssembly(profile.AssemblyPath, profile.ReferenceDirectories))
        {
            layout = InjectionInspector.Discover(source, System.Reflection.AssemblyName.GetAssemblyName(profile.IFixCoreAssemblyPath).FullName);
            catalog = BuildCatalog(source, layout);
            // 从原始程序集读取字段可见性；编译引用会放宽访问权限。
            fieldCatalog = BuildFieldCatalog(source);
            report.TypeCount = source.MainModule.AllTypes().Count();
            report.MethodCount = source.MainModule.AllTypes().Sum(t => t.Methods.Count);
            report.IdMapFieldCount = catalog.IdMap.Count;
            report.FieldCount = fieldCatalog.Types.Sum(t => t.Fields.Count);
        }

        using (var clean = CecilModel.ReadAssembly(profile.AssemblyPath, profile.ReferenceDirectories))
        {
            Sanitize(clean, report, layout);
            clean.Write(cleanPath);
        }

        using (var compileReference = CecilModel.ReadAssembly(profile.AssemblyPath, profile.ReferenceDirectories))
        {
            WidenForCompilation(compileReference, report);
            compileReference.Write(compileRefPath);
        }

        new AdapterCoreSourceGenerator().Generate(
            profile.IFixCoreAssemblyPath,
            profile.ReferenceDirectories,
            adapterCoreSourcePath,
            report);
        var knownPatchMagic = profile.KnownPatchPath is null
            ? (ulong?)null
            : PathAndHash.ReadUInt64Prefix(profile.KnownPatchPath);
        if (knownPatchMagic is not null && report.AdapterMagic != knownPatchMagic)
            throw new InvalidDataException(
                $"IFix.Core magic 0x{report.AdapterMagic:X16} does not match known patch magic 0x{knownPatchMagic:X16}.");
        if (profile.KnownPatchPath is not null)
        {
            using var metadata = new DummyDllSource(profile.ReferenceDirectories.Prepend(Path.GetDirectoryName(profile.AssemblyPath)!)
                .Append(Path.GetDirectoryName(profile.IFixCoreAssemblyPath)!), [profile.AssemblyPath, profile.IFixCoreAssemblyPath]);
            layout.ValidatePatchIdentity(PatchReader.ReadFile(profile.KnownPatchPath, metadata));
        }

        var prepared = new PreparedProfile
        {
            TargetAssemblyName = assemblyName,
            TargetAssemblyIdentity = identity.FullName,
            InjectionLayoutPath = layoutPath,
            Name = profile.Name,
            TargetFramework = profile.TargetFramework,
            SourceProfilePath = profilePath,
            SourceAssemblyPath = profile.AssemblyPath,
            SourceAssemblySha256 = PathAndHash.Sha256(profile.AssemblyPath),
            IFixCoreAssemblyPath = profile.IFixCoreAssemblyPath,
            IFixCoreAssemblySha256 = PathAndHash.Sha256(profile.IFixCoreAssemblyPath),
            AdapterCoreSourcePath = adapterCoreSourcePath,
            AdapterCoreSourceSha256 = PathAndHash.Sha256(adapterCoreSourcePath),
            CatalogPath = catalogPath,
            FieldCatalogPath = fieldCatalogPath,
            KnownPatchPath = profile.KnownPatchPath,
            KnownPatchSha256 = profile.KnownPatchPath is null ? null : PathAndHash.Sha256(profile.KnownPatchPath),
            KnownPatchMagic = knownPatchMagic,
            ReferenceDirectories = profile.ReferenceDirectories,
            CleanAssemblyPath = cleanPath,
            CleanAssemblySha256 = PathAndHash.Sha256(cleanPath),
            CompileReferenceAssemblyPath = compileRefPath,
            CompileReferenceAssemblySha256 = PathAndHash.Sha256(compileRefPath),
        };

        SourceBuilderJson.Save(Path.Combine(outputDirectory, "prepare-report.json"), report);
        SourceBuilderJson.Save(layoutPath, layout);
        prepared.InjectionLayoutSha256 = PathAndHash.Sha256(layoutPath);
        SourceBuilderJson.Save(catalogPath, catalog);
        prepared.CatalogSha256 = PathAndHash.Sha256(catalogPath);
        SourceBuilderJson.Save(fieldCatalogPath, fieldCatalog);
        prepared.FieldCatalogSha256 = PathAndHash.Sha256(fieldCatalogPath);
        SourceBuilderJson.Save(Path.Combine(outputDirectory, "prepared-profile.json"), prepared);
        return prepared;
    }

    private static void ValidateProfile(SourceBuilderProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new InvalidDataException("Profile name is required.");
        if (!File.Exists(profile.AssemblyPath))
            throw new FileNotFoundException("Target assembly input does not exist.", profile.AssemblyPath);
        if (!File.Exists(profile.IFixCoreAssemblyPath))
            throw new FileNotFoundException("IFix.Core input does not exist.", profile.IFixCoreAssemblyPath);
        if (profile.KnownPatchPath is not null && !File.Exists(profile.KnownPatchPath))
            throw new FileNotFoundException("Known patch does not exist.", profile.KnownPatchPath);
        if (profile.ReferenceDirectories.Count == 0)
            profile.ReferenceDirectories.Add(Path.GetDirectoryName(profile.AssemblyPath)!);
        foreach (var directory in profile.ReferenceDirectories)
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException($"Reference directory does not exist: {directory}");
    }

    private static AssemblyCatalog BuildCatalog(AssemblyDefinition assembly, InjectionLayout layout)
    {
        var catalog = new AssemblyCatalog { AssemblyName = assembly.Name.FullName };
        foreach (var method in assembly.MainModule.AllTypes().SelectMany(t => t.Methods))
            catalog.Methods.Add(new MethodCatalogEntry
            {
                Signature = method.FullName,
                IdTag = CecilModel.ReadIdTag(method),
            });

        catalog.IdMap.AddRange(layout.IdMapSegments.SelectMany(segment => segment.Entries));
        return catalog;
    }

    /// <summary>
    /// 目标程序集的字段表。只读元数据，不做任何改写。
    /// </summary>
    private static FieldCatalog BuildFieldCatalog(AssemblyDefinition assembly)
    {
        var catalog = new FieldCatalog { AssemblyName = assembly.Name.FullName };
        foreach (var type in assembly.MainModule.AllTypes())
        {
            if (type.Fields.Count == 0)
                continue;
            var entry = new TypeFieldsEntry { Type = type.FullName };
            foreach (var field in type.Fields)
            {
                var backedProperty = BackedPropertyOf(field);
                entry.Fields.Add(new FieldCatalogEntry
                {
                    Name = field.Name,
                    FieldType = field.FieldType.FullName,
                    Visibility = DescribeVisibility(field),
                    IsStatic = field.IsStatic,
                    IsInitOnly = field.IsInitOnly,
                    IsLiteral = field.IsLiteral,
                    IsBackingField = backedProperty is not null,
                    BackedProperty = backedProperty,
                });
            }
            catalog.Types.Add(entry);
        }
        return catalog;
    }

    /// <summary>
    /// 自动属性的后备字段。判据是**元数据里的真实名字** <c>&lt;Prop&gt;k__BackingField</c>；
    /// Il2CppInspector 的 dump.cs 会把它渲染成 <c>_Prop_k__BackingField</c>，那是渲染形态、
    /// 不是元数据，按那个名字找会一个都找不到。
    /// </summary>
    private static string? BackedPropertyOf(FieldDefinition field)
    {
        const string suffix = ">k__BackingField";
        if (!field.Name.StartsWith('<') || !field.Name.EndsWith(suffix, StringComparison.Ordinal))
            return null;
        var name = field.Name[1..^suffix.Length];
        return name.Length == 0 ? null : name;
    }

    private static string DescribeVisibility(FieldDefinition field) =>
        (field.Attributes & FieldAttributes.FieldAccessMask) switch
        {
            FieldAttributes.Private => "private",
            FieldAttributes.FamANDAssem => "private protected",
            FieldAttributes.Assembly => "internal",
            FieldAttributes.Family => "protected",
            FieldAttributes.FamORAssem => "protected internal",
            FieldAttributes.Public => "public",
            FieldAttributes.CompilerControlled => "compiler-controlled",
            _ => "unknown",
        };

    private static void Sanitize(AssemblyDefinition assembly, PrepareReport report, InjectionLayout layout)
    {
        var module = assembly.MainModule;
        var injectedNames = layout.RemovedTypes.ToHashSet(StringComparer.Ordinal);
        var proxyNames = layout.BaseProxyMethods.ToHashSet(StringComparer.Ordinal);
        var remove = module.Types.Where(type => injectedNames.Contains(type.FullName)).ToList();
        foreach (var type in remove)
        {
            report.RemovedTypes.Add(type.FullName);
            module.Types.Remove(type);
        }

        foreach (var type in module.AllTypes())
        {
            report.StrippedCompilerGeneratedTypes += StripCompilerGenerated(type.CustomAttributes);
            foreach (var field in type.Fields)
                report.StrippedCompilerGeneratedFields += StripCompilerGenerated(field.CustomAttributes);
            foreach (var method in type.Methods)
            {
                report.StrippedCompilerGeneratedMethods += StripCompilerGenerated(method.CustomAttributes);
                // The clean shell is metadata-only. Retained injected bodies may reference removed wrapper types.
                if (method.HasBody)
                {
                    method.Body = new MethodBody(method);
                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Throw));
                }
            }

            var proxies = type.Methods.Where(method => proxyNames.Contains(method.FullName)).ToList();
            foreach (var proxy in proxies)
            {
                type.Methods.Remove(proxy);
                report.RemovedBaseProxyMethods++;
            }
        }

        if (module.AllTypes().Any(type => injectedNames.Contains(type.FullName)))
            throw new InvalidDataException("Sanitizer left injected metadata in the clean shell.");
    }

    private static int StripCompilerGenerated(Mono.Collections.Generic.Collection<CustomAttribute> attributes)
    {
        var markers = attributes.Where(attribute =>
            attribute.AttributeType.FullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute").ToList();
        foreach (var marker in markers)
            attributes.Remove(marker);
        return markers.Count;
    }

    private static void WidenForCompilation(AssemblyDefinition assembly, PrepareReport report)
    {
        foreach (var type in assembly.MainModule.AllTypes())
        {
            var originalTypeAttributes = type.Attributes;
            type.Attributes &= ~TypeAttributes.VisibilityMask;
            type.Attributes |= type.IsNested ? TypeAttributes.NestedPublic : TypeAttributes.Public;
            if (type.Attributes != originalTypeAttributes)
                report.WidenedTypes++;

            foreach (var method in type.Methods)
            {
                var original = method.Attributes;
                method.Attributes &= ~MethodAttributes.MemberAccessMask;
                method.Attributes |= MethodAttributes.Public;
                if (method.Attributes != original)
                    report.WidenedMethods++;
            }

            // Il2CppInspector can expose both an event and its private backing field with
            // the same metadata name. Making both public makes ordinary C# event syntax
            // ambiguous, so keep the colliding field private and expose the event through
            // its widened add/remove methods.
            var eventNames = type.Events.Select(eventDefinition => eventDefinition.Name)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var field in type.Fields)
            {
                if (eventNames.Contains(field.Name))
                    continue;
                var original = field.Attributes;
                field.Attributes &= ~FieldAttributes.FieldAccessMask;
                field.Attributes |= FieldAttributes.Public;
                if (field.Attributes != original)
                    report.WidenedFields++;
            }
        }
    }
}
