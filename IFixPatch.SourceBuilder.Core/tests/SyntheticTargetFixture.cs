using IFixPatch.Core;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IFixPatch.SourceBuilder.Core.Tests;

internal sealed class SyntheticTargetFixture : IDisposable
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "ifix-sourcebuilder-unit-" + Guid.NewGuid().ToString("N"));
    public string TargetPath => Path.Combine(DirectoryPath, "renamed-metadata.dll");
    public string RuntimePath => Path.Combine(DirectoryPath, "IFix.Core.dll");
    public string ProfilePath => Path.Combine(DirectoryPath, "profile.json");

    public SyntheticTargetFixture(string assemblyName = "Example.Game", int segmentCount = 4)
    {
        System.IO.Directory.CreateDirectory(DirectoryPath);
        using var runtime = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("IFix.Core", new Version(1, 0)), "IFix.Core", ModuleKind.Dll);
        var rm = runtime.MainModule;
        var code = Enum(rm, "IFix.Core", "Code");
        foreach (var (name, number) in new OpCodeMap().Validate().Missing.Select((name, number) => (name, number)))
            Constant(code, name, number);
        var instruction = new TypeDefinition("IFix.Core", "Instruction", TypeAttributes.Public | TypeAttributes.Sealed,
            new TypeReference("System", "ValueType", rm, rm.TypeSystem.CoreLibrary));
        rm.Types.Add(instruction);
        instruction.Fields.Add(new FieldDefinition("INSTRUCTION_FORMAT_MAGIC", FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault, rm.TypeSystem.UInt64) { Constant = 0x123456789UL });
        var handlers = Enum(rm, "IFix.Core", "ExceptionHandlerType");
        Constant(handlers, "Catch", 0); Constant(handlers, "Finally", 2);
        Class(rm, "IFix.Core", "VirtualMachine");
        Class(rm, "IFix.Core", "AnonymousStorey");
        rm.Types.Add(new TypeDefinition("IFix.Core", "WrappersManager", TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract));
        runtime.Write(RuntimePath);

        using var target = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(assemblyName, new Version(2, 3, 4)), assemblyName, ModuleKind.Dll);
        var module = target.MainModule;
        var runtimeReference = AssemblyNameReference.Parse(runtime.Name.FullName);
        module.AssemblyReferences.Add(runtimeReference);
        TypeReference Runtime(string name) => new("IFix.Core", name, module, runtimeReference);
        var wrapper = Class(module, "IFix", "ILFixDynamicMethodWrapper");
        wrapper.Fields.Add(new FieldDefinition("virtualMachine", FieldAttributes.Private, Runtime("VirtualMachine")));
        wrapper.Fields.Add(new FieldDefinition("methodId", FieldAttributes.Private, module.TypeSystem.Int32));
        wrapper.Fields.Add(new FieldDefinition("wrapperArray", FieldAttributes.Public | FieldAttributes.Static, new ArrayType(wrapper)));
        Method(wrapper, ".ctor", false, module.TypeSystem.Void, Runtime("VirtualMachine"), module.TypeSystem.Int32, module.TypeSystem.Object);
        Class(module, "IFix", "ILFixInterfaceBridge").BaseType = Runtime("AnonymousStorey");
        var manager = Class(module, "IFix", "WrappersManagerImpl");
        manager.Interfaces.Add(new InterfaceImplementation(Runtime("WrappersManager")));
        Method(manager, ".ctor", false, module.TypeSystem.Void, Runtime("VirtualMachine"));
        Method(manager, "GetPatch", true, wrapper, module.TypeSystem.Int32);
        Method(manager, "IsPatched", true, module.TypeSystem.Boolean, module.TypeSystem.Int32);
        Method(manager, "CreateWrapper", false, module.TypeSystem.Object, module.TypeSystem.Int32);
        Method(manager, "InitWrapperArray", false, module.TypeSystem.Object, module.TypeSystem.Int32);
        Method(manager, "CreateDelegate", false, new TypeReference("System", "Delegate", module, module.TypeSystem.CoreLibrary),
            new TypeReference("System", "Type", module, module.TypeSystem.CoreLibrary), module.TypeSystem.Int32, module.TypeSystem.Object);
        Method(manager, "CreateBridge", false, Runtime("AnonymousStorey"), module.TypeSystem.Int32, new ArrayType(module.TypeSystem.Int32),
            module.TypeSystem.Int32, new ArrayType(module.TypeSystem.Int32), new ArrayType(module.TypeSystem.Int32), Runtime("VirtualMachine"));
        for (var index = 0; index < segmentCount; index++)
        {
            var map = Enum(module, "IFix", "IDMAP" + index);
            Constant(map, index == 0 ? "Example-Calculator-Compute0" : $"Example-Calculator-Method{index}0", 3 + index * 17);
        }
        var calculator = Class(module, "Example", "Calculator");
        Method(calculator, "Compute", true, module.TypeSystem.Int32);
        calculator.Fields.Add(new FieldDefinition("Offset", FieldAttributes.Private, module.TypeSystem.Int32));
        // Similar names outside the supported injection identities are ordinary user metadata.
        Class(module, "Example", "ILFixDynamicMethodWrapper");
        Class(module, "IFix", "IDMAPHelper");
        Class(module, "IFix.RedirectTools", "UserType");
        var redirect = Class(module, "IFix.RedirectTo.Example", "Calculator");
        redirect.Attributes |= TypeAttributes.Abstract | TypeAttributes.Sealed;
        redirect.Fields.Add(new FieldDefinition("_rf_Compute0", FieldAttributes.Public | FieldAttributes.Static, wrapper));
        var nested = new TypeDefinition("", "Nested", TypeAttributes.NestedPublic | TypeAttributes.Abstract | TypeAttributes.Sealed, module.TypeSystem.Object);
        redirect.NestedTypes.Add(nested);
        nested.Fields.Add(new FieldDefinition("_rf_Read0", FieldAttributes.Public | FieldAttributes.Static, wrapper));
        target.Write(TargetPath);
        var emptyReferences = Path.Combine(DirectoryPath, "empty-references");
        System.IO.Directory.CreateDirectory(emptyReferences);
        SourceBuilderJson.Save(ProfilePath, new SourceBuilderProfile
        {
            Name = "synthetic",
            AssemblyPath = TargetPath,
            IFixCoreAssemblyPath = RuntimePath,
            ReferenceDirectories = [emptyReferences, DirectoryPath],
        });
    }

    public PreparedProfile Prepare() => new AssemblyPreparer().Prepare(ProfilePath, Path.Combine(DirectoryPath, "prepared"));

    public void Change(Action<ModuleDefinition> mutation)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(TargetPath, new ReaderParameters { InMemory = true });
        mutation(assembly.MainModule);
        assembly.Write(TargetPath);
    }

    public static TypeDefinition Class(ModuleDefinition module, string ns, string name)
    {
        var type = new TypeDefinition(ns, name, TypeAttributes.Public, module.TypeSystem.Object);
        module.Types.Add(type);
        return type;
    }

    public static MethodDefinition Method(TypeDefinition owner, string name, bool isStatic, TypeReference result, params TypeReference[] parameters)
    {
        var attributes = MethodAttributes.Public | (isStatic ? MethodAttributes.Static : 0);
        if (name == ".ctor") attributes |= MethodAttributes.SpecialName | MethodAttributes.RTSpecialName;
        var method = new MethodDefinition(name, attributes, result);
        foreach (var parameter in parameters) method.Parameters.Add(new ParameterDefinition(parameter));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Throw));
        owner.Methods.Add(method);
        return method;
    }

    private static TypeDefinition Enum(ModuleDefinition module, string ns, string name)
    {
        var type = new TypeDefinition(ns, name, TypeAttributes.Public | TypeAttributes.Sealed,
            new TypeReference("System", "Enum", module, module.TypeSystem.CoreLibrary));
        type.Fields.Add(new FieldDefinition("value__", FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RTSpecialName, module.TypeSystem.Int32));
        module.Types.Add(type);
        return type;
    }

    private static void Constant(TypeDefinition owner, string name, int value) => owner.Fields.Add(
        new FieldDefinition(name, FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault, owner) { Constant = value });

    public void Dispose() => System.IO.Directory.Delete(DirectoryPath, recursive: true);
}
