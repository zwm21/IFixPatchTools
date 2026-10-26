using Mono.Cecil;

namespace IFixPatch.SourceBuilder.Core.Tests;

public sealed class InjectionContractTests
{
    // These are serialized assemblies with incompatible injection ABIs, rather than
    // mocked inspector results. A usable prepared profile must never be published for them.
    [Theory]
    [InlineData("wrapper-generic", "ILFixDynamicMethodWrapper")]
    [InlineData("wrapper-base", "ILFixDynamicMethodWrapper")]
    [InlineData("wrapper-vm-static", "dynamic wrapper contract")]
    [InlineData("wrapper-vm-runtime", "dynamic wrapper contract")]
    [InlineData("wrapper-id-type", "dynamic wrapper contract")]
    [InlineData("wrapper-array-instance", "dynamic wrapper contract")]
    [InlineData("wrapper-array-type", "dynamic wrapper contract")]
    [InlineData("wrapper-constructor", "dynamic wrapper contract")]
    [InlineData("bridge-generic", "interface bridge base")]
    [InlineData("bridge-runtime", "interface bridge base")]
    [InlineData("manager-generic", "wrapper manager contract")]
    [InlineData("manager-interface", "wrapper manager contract")]
    [InlineData("manager-interface-runtime", "wrapper manager contract")]
    [InlineData("manager-.ctor", "wrapper manager contract")]
    [InlineData("manager-GetPatch", "wrapper manager contract")]
    [InlineData("manager-IsPatched", "wrapper manager contract")]
    [InlineData("manager-CreateWrapper", "wrapper manager contract")]
    [InlineData("manager-InitWrapperArray", "wrapper manager contract")]
    [InlineData("manager-CreateDelegate", "wrapper manager contract")]
    [InlineData("manager-CreateBridge", "wrapper manager contract")]
    [InlineData("idmap-instance-field", "invalid IDMAP field")]
    [InlineData("idmap-nonliteral-field", "invalid IDMAP field")]
    [InlineData("idmap-field-type", "invalid IDMAP field")]
    [InlineData("redirect-not-abstract", "redirect type contract")]
    [InlineData("redirect-not-sealed", "redirect type contract")]
    [InlineData("redirect-base", "redirect type contract")]
    [InlineData("redirect-instance-field", "redirect type contract")]
    [InlineData("redirect-field-type", "redirect type contract")]
    [InlineData("redirect-method", "redirect type contract")]
    public void RejectsIncompatibleInjectionAbisBeforePublishingPreparedArtifacts(string change, string diagnostic)
    {
        using var fixture = new SyntheticTargetFixture();
        fixture.Change(module =>
        {
            var wrapper = module.GetType("IFix.ILFixDynamicMethodWrapper");
            var bridge = module.GetType("IFix.ILFixInterfaceBridge");
            var manager = module.GetType("IFix.WrappersManagerImpl");
            var redirect = module.GetType("IFix.RedirectTo.Example.Calculator");
            var map = module.GetType("IFix.IDMAP0");
            var mapField = map.Fields.Single(field => field.IsLiteral);
            TypeReference OtherRuntime(string type)
            {
                var other = new AssemblyNameReference("IFix.Core", new Version(9, 0)); module.AssemblyReferences.Add(other);
                return new TypeReference("IFix.Core", type, module, other);
            }
            switch (change)
            {
                case "wrapper-generic": wrapper.GenericParameters.Add(new GenericParameter("T", wrapper)); break;
                case "wrapper-base": wrapper.BaseType = module.TypeSystem.String; break;
                case "wrapper-vm-static": wrapper.Fields.Single(field => field.Name == "virtualMachine").IsStatic = true; break;
                case "wrapper-vm-runtime": wrapper.Fields.Single(field => field.Name == "virtualMachine").FieldType = OtherRuntime("VirtualMachine"); break;
                case "wrapper-id-type": wrapper.Fields.Single(field => field.Name == "methodId").FieldType = module.TypeSystem.Int64; break;
                case "wrapper-array-instance": wrapper.Fields.Single(field => field.Name == "wrapperArray").IsStatic = false; break;
                case "wrapper-array-type": wrapper.Fields.Single(field => field.Name == "wrapperArray").FieldType = new ArrayType(module.TypeSystem.Object); break;
                case "wrapper-constructor": wrapper.Methods.Clear(); break;
                case "bridge-generic": bridge.GenericParameters.Add(new GenericParameter("T", bridge)); break;
                case "bridge-runtime": bridge.BaseType = OtherRuntime("AnonymousStorey"); break;
                case "manager-generic": manager.GenericParameters.Add(new GenericParameter("T", manager)); break;
                case "manager-interface": manager.Interfaces.Clear(); break;
                case "manager-interface-runtime": manager.Interfaces.Clear(); manager.Interfaces.Add(new InterfaceImplementation(OtherRuntime("WrappersManager"))); break;
                case "idmap-instance-field": mapField.IsStatic = false; break;
                case "idmap-nonliteral-field": mapField.IsLiteral = false; break;
                case "idmap-field-type": mapField.FieldType = module.TypeSystem.Int32; break;
                case "redirect-not-abstract": redirect.IsAbstract = false; break;
                case "redirect-not-sealed": redirect.IsSealed = false; break;
                case "redirect-base": redirect.BaseType = module.TypeSystem.String; break;
                case "redirect-instance-field": redirect.Fields.Single().IsStatic = false; break;
                case "redirect-field-type": redirect.Fields.Single().FieldType = module.TypeSystem.Object; break;
                case "redirect-method": SyntheticTargetFixture.Method(redirect, "Extra", true, module.TypeSystem.Void); break;
                default:
                    var name = change["manager-".Length..];
                    manager.Methods.Remove(manager.Methods.Single(method => method.Name == name));
                    break;
            }
        });
        Assert.Contains(diagnostic, Assert.Throws<InvalidDataException>(() => fixture.Prepare()).Message);
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, "prepared", "prepared-profile.json")));
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, "prepared", "injection-layout.json")));
    }

    [Theory]
    [InlineData("wrapper-constructor", "dynamic wrapper contract")]
    [InlineData("manager-constructor", "wrapper manager contract")]
    [InlineData("manager-bridge-parameter", "wrapper manager contract")]
    [InlineData("manager-bridge-result", "wrapper manager contract")]
    public void RejectsInjectionMethodsBoundToADifferentRuntimeAssemblyVersion(string role, string diagnostic)
    {
        using var fixture = new SyntheticTargetFixture();
        fixture.Change(module =>
        {
            var incompatibleRuntime = new AssemblyNameReference("IFix.Core", new Version(9, 0));
            module.AssemblyReferences.Add(incompatibleRuntime);
            TypeReference Runtime(string name) => new("IFix.Core", name, module, incompatibleRuntime);
            var wrapper = module.GetType("IFix.ILFixDynamicMethodWrapper");
            var manager = module.GetType("IFix.WrappersManagerImpl");
            if (role == "wrapper-constructor")
                wrapper.Methods.Single(method => method.Name == ".ctor").Parameters[0].ParameterType = Runtime("VirtualMachine");
            else if (role == "manager-constructor")
                manager.Methods.Single(method => method.Name == ".ctor").Parameters[0].ParameterType = Runtime("VirtualMachine");
            else if (role == "manager-bridge-parameter")
                manager.Methods.Single(method => method.Name == "CreateBridge").Parameters[^1].ParameterType = Runtime("VirtualMachine");
            else
                manager.Methods.Single(method => method.Name == "CreateBridge").ReturnType = Runtime("AnonymousStorey");
        });
        // CLR type identity includes its defining assembly: an IFix.Core v9 parameter
        // cannot implement the v1 injection ABI even though its namespace/name agree.
        Assert.Contains(diagnostic, Assert.Throws<InvalidDataException>(() => fixture.Prepare()).Message);
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, "prepared", "prepared-profile.json")));
    }

    [Theory]
    [InlineData("manager-result", "wrapper manager contract")]
    [InlineData("wrapper-array", "dynamic wrapper contract")]
    [InlineData("redirect-field", "redirect type contract")]
    public void RejectsSameNamedWrapperTypesDefinedInAnotherAssembly(string role, string diagnostic)
    {
        using var fixture = new SyntheticTargetFixture();
        fixture.Change(module =>
        {
            var otherAssembly = new AssemblyNameReference("Other.Game", new Version(2, 3, 4));
            module.AssemblyReferences.Add(otherAssembly);
            var unrelatedWrapper = new TypeReference("IFix", "ILFixDynamicMethodWrapper", module, otherAssembly);
            if (role == "manager-result")
                module.GetType("IFix.WrappersManagerImpl").Methods.Single(method => method.Name == "GetPatch").ReturnType = unrelatedWrapper;
            else if (role == "wrapper-array")
                module.GetType("IFix.ILFixDynamicMethodWrapper").Fields.Single(field => field.Name == "wrapperArray").FieldType = new ArrayType(unrelatedWrapper);
            else
                module.GetType("IFix.RedirectTo.Example.Calculator").Fields.Single().FieldType = unrelatedWrapper;
        });
        // An identically named class owned by another assembly is not the target's
        // generated wrapper and cannot participate in its wrapper/redirect protocol.
        Assert.Contains(diagnostic, Assert.Throws<InvalidDataException>(() => fixture.Prepare()).Message);
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, "prepared", "prepared-profile.json")));
    }

    [Fact]
    public void CleanShellRemovesCompilerGeneratedMarkersWithoutDiscardingOtherAttributes()
    {
        using var fixture = new SyntheticTargetFixture();
        fixture.Change(module =>
        {
            CustomAttribute Marker(string name) => new(new MethodReference(".ctor", module.TypeSystem.Void,
                new TypeReference(name == "CompilerGeneratedAttribute" ? "System.Runtime.CompilerServices" : "System", name, module, module.TypeSystem.CoreLibrary))
            { HasThis = true });
            var owner = module.GetType("Example.Calculator");
            owner.CustomAttributes.Add(Marker("CompilerGeneratedAttribute"));
            owner.CustomAttributes.Add(Marker("ObsoleteAttribute"));
            owner.Fields.Single().CustomAttributes.Add(Marker("CompilerGeneratedAttribute"));
            owner.Methods.Single().CustomAttributes.Add(Marker("CompilerGeneratedAttribute"));
        });
        var prepared = fixture.Prepare();
        using var clean = AssemblyDefinition.ReadAssembly(prepared.CleanAssemblyPath);
        var owner = clean.MainModule.GetType("Example.Calculator");
        Assert.Equal("System.ObsoleteAttribute", Assert.Single(owner.CustomAttributes).AttributeType.FullName);
        Assert.Empty(owner.Fields.Single().CustomAttributes);
        Assert.Empty(owner.Methods.Single().CustomAttributes);
        using var reference = AssemblyDefinition.ReadAssembly(prepared.CompileReferenceAssemblyPath);
        var compileOwner = reference.MainModule.GetType("Example.Calculator");
        Assert.Contains(compileOwner.CustomAttributes, attribute => attribute.AttributeType.FullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute");
        Assert.Contains(compileOwner.Fields.Single().CustomAttributes, attribute => attribute.AttributeType.FullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute");
        Assert.Contains(compileOwner.Methods.Single().CustomAttributes, attribute => attribute.AttributeType.FullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute");
    }

    [Theory]
    [InlineData("<>iFixBaseProxy_")]
    [InlineData("__iFixBaseProxy_")]
    public void RemovesRecognizedBaseProxiesFromTheCleanShellButKeepsOriginalMethodsAndCompileReferences(string prefix)
    {
        using var fixture = new SyntheticTargetFixture();
        fixture.Change(module =>
        {
            var owner = module.GetType("Example.Calculator");
            SyntheticTargetFixture.Method(owner, "Calculate", false, module.TypeSystem.Int32, module.TypeSystem.Int32);
            SyntheticTargetFixture.Method(owner, prefix + "Calculate", false, module.TypeSystem.Int32, module.TypeSystem.Int32);
        });
        var prepared = fixture.Prepare();
        var layout = InjectionLayout.Load(prepared);
        Assert.Contains("::" + prefix + "Calculate(", Assert.Single(layout.BaseProxyMethods));
        using var clean = AssemblyDefinition.ReadAssembly(prepared.CleanAssemblyPath);
        Assert.Single(clean.MainModule.GetType("Example.Calculator").Methods, method => method.Name == "Calculate");
        Assert.DoesNotContain(clean.MainModule.GetType("Example.Calculator").Methods, method => method.Name == prefix + "Calculate");
        using var reference = AssemblyDefinition.ReadAssembly(prepared.CompileReferenceAssemblyPath);
        Assert.Contains(reference.MainModule.GetType("Example.Calculator").Methods, method => method.Name == prefix + "Calculate");
        BuildPipeline.ValidatePrepared(prepared);
    }

    [Theory]
    [InlineData("static")]
    [InlineData("no-original")]
    [InlineData("return")]
    [InlineData("parameters")]
    public void RejectsBaseProxiesThatCannotCallAnOriginalInstanceSignature(string change)
    {
        using var fixture = new SyntheticTargetFixture();
        fixture.Change(module =>
        {
            var owner = module.GetType("Example.Calculator");
            if (change != "no-original") SyntheticTargetFixture.Method(owner, "Calculate", false, module.TypeSystem.Int32, module.TypeSystem.Int32);
            SyntheticTargetFixture.Method(owner, "<>iFixBaseProxy_Calculate", change == "static",
                change == "return" ? module.TypeSystem.String : module.TypeSystem.Int32,
                change == "parameters" ? module.TypeSystem.Int64 : module.TypeSystem.Int32);
        });
        Assert.Contains("base proxy contract", Assert.Throws<InvalidDataException>(() => fixture.Prepare()).Message);
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, "prepared", "prepared-profile.json")));
    }
}
