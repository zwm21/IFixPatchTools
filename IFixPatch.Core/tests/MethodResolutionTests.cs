using Mono.Cecil;

namespace IFixPatch.Core.Tests;

public sealed class MethodResolutionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownParameterSignaturesCannotBindToAnotherOverload(bool multiple)
    {
        using var directory = new TemporaryDirectory();
        WriteMethods(directory, multiple);
        using var source = new DummyDllSource(directory.Path);
        Assert.Null(source.FindMethodByParamTypes("Example.Api, Synthetic", "Read", ["System.DateTime"], type => type.FullName));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ExplicitGenericArityRestrictsCandidateMethods(int arity)
    {
        using var directory = new TemporaryDirectory();
        WriteMethods(directory, true);
        using var source = new DummyDllSource(directory.Path);
        var method = source.FindMethodByParamTypes("Example.Api, Synthetic", "Read", [null], type => type.FullName, arity);
        if (arity == 2) Assert.Null(method);
        else Assert.Equal(arity, method!.GenericParameters.Count);
    }

    private static void WriteMethods(TemporaryDirectory directory, bool multiple)
    {
        using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Synthetic", new Version(1, 0)), "Synthetic", ModuleKind.Dll);
        var module = assembly.MainModule;
        var owner = new TypeDefinition("Example", "Api", TypeAttributes.Public, module.TypeSystem.Object); module.Types.Add(owner);
        var ordinary = new MethodDefinition("Read", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
        ordinary.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32)); owner.Methods.Add(ordinary);
        if (multiple)
        {
            var generic = new MethodDefinition("Read", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
            generic.GenericParameters.Add(new GenericParameter("T", generic));
            generic.Parameters.Add(new ParameterDefinition(module.TypeSystem.String)); owner.Methods.Add(generic);
        }
        assembly.Write(directory.File("Synthetic.dll"));
    }
}
