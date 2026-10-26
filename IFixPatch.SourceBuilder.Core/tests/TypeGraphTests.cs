using System.Reflection;
using Mono.Cecil;

namespace IFixPatch.SourceBuilder.Core.Tests;

public sealed class TypeGraphTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BindsExtensionFieldsPropertiesAndInitializerToAnExistingTargetClass(bool targetHasConstructor)
    {
        using var fixture = new SyntheticTargetFixture();
        if (targetHasConstructor) fixture.Change(module => SyntheticTargetFixture.Method(module.GetType("Example.Calculator"), ".ctor", false, module.TypeSystem.Void));
        var manifest = CompilerPipelineTests.WriteSource(fixture, """
            namespace Patch {
              public class Extension {
                public int Extra;
                public Extension() { Extra=5; }
                public int Value {get {return Extra+10;} set {Extra=value;}}
              }
              public static class Replacement { public static int Compute() {var item=new Extension();item.Value+=17;return item.Value;} }
            }
            """);
        manifest.TypeBindings.Add("Patch.Extension", "Example.Calculator");
        var (grafter, output) = CompilerPipelineTests.CompileAndGraft(fixture, CompiledGraftingTests.Prepare(fixture), manifest);
        Assert.Equal(42, Assembly.Load(File.ReadAllBytes(output)).GetType("Example.Calculator")!.GetMethod("Compute")!.Invoke(null, null));
        Assert.Contains("System.Int32 Example.Calculator::Extra", grafter.AddedFields);
        Assert.Contains(grafter.AddedProperties, property => property.Contains("Example.Calculator::Value()"));
        using var assembly = AssemblyDefinition.ReadAssembly(output);
        Assert.Equal("System.Int32", assembly.MainModule.GetType("Example.Calculator").Fields.Single(field => field.Name == "Extra").FieldType.FullName);
        Assert.DoesNotContain(assembly.MainModule.AssemblyReferences, reference => reference.Name == "SyntheticImplementation");
    }

    [Theory]
    [InlineData("field", "Field already exists")]
    [InlineData("method", "Method already exists")]
    [InlineData("constructor", "parameterless constructors")]
    public void RejectsExtensionMemberCollisionsAndUnsupportedInitializers(string conflict, string requirement)
    {
        using var fixture = new SyntheticTargetFixture();
        var member = conflict switch { "field" => "public int Offset;", "method" => "public static int Compute()=>1;", _ => "public Extension(int value) {}" };
        var manifest = CompilerPipelineTests.WriteSource(fixture, "namespace Patch { public class Extension {" + member + "} public static class Replacement {public static int Compute()=>42;} }");
        manifest.TypeBindings.Add("Patch.Extension", "Example.Calculator");
        var error = Record.Exception(() => CompilerPipelineTests.CompileAndGraft(fixture, CompiledGraftingTests.Prepare(fixture), manifest));
        Assert.NotNull(error); Assert.True(error is InvalidDataException or NotSupportedException); Assert.Contains(requirement, error.Message);
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, "grafted.dll")));
    }

    [Theory]
    [InlineData("native-base")]
    [InlineData("generic-class")]
    [InlineData("new-interface")]
    public void RejectsNewTypeShapesThatCannotBeRepresentedByTheRuntime(string shape)
    {
        using var fixture = new SyntheticTargetFixture();
        var source = shape switch
        {
            "native-base" => "class State:System.Exception {} public static class Replacement {public static int Compute(){new State();return 42;}}",
            "generic-class" => "class State<T> {} public static class Replacement {public static int Compute(){new State<int>();return 42;}}",
            _ => "interface IState {int Read();} class State:IState {public int Read()=>42;} public static class Replacement {public static int Compute()=>new State().Read();}"
        };
        var manifest = CompilerPipelineTests.WriteSource(fixture, "namespace Patch {" + source + "}");
        Assert.Throws<NotSupportedException>(() => CompilerPipelineTests.CompileAndGraft(fixture, CompiledGraftingTests.Prepare(fixture), manifest));
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, "grafted.dll")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapturingDelegatesRequireAMatchingGeneratedNativeWrapper(bool wrapperAvailable)
    {
        using var fixture = new SyntheticTargetFixture();
        if (wrapperAvailable) fixture.Change(module => SyntheticTargetFixture.Method(module.GetType("IFix.ILFixDynamicMethodWrapper"), "__Gen_Wrap_0", false, module.TypeSystem.Int32, module.TypeSystem.Int32));
        var manifest = CompilerPipelineTests.WriteSource(fixture, """
            namespace Patch {public static class Replacement {
              public static int Compute() {int initial=40;System.Func<int,int> add=x=>x+initial;return add(2);}
            }}
            """);
        if (wrapperAvailable)
        {
            var (grafter, output) = CompilerPipelineTests.CompileAndGraft(fixture, CompiledGraftingTests.Prepare(fixture), manifest);
            Assert.Equal(42, Assembly.Load(File.ReadAllBytes(output)).GetType("Example.Calculator")!.GetMethod("Compute")!.Invoke(null, null));
            Assert.True(grafter.HasTypeGraph);
        }
        else
        {
            Assert.Contains("delegate wrapper does not cover", Assert.Throws<NotSupportedException>(() => CompilerPipelineTests.CompileAndGraft(fixture, CompiledGraftingTests.Prepare(fixture), manifest)).Message);
            Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, "grafted.dll")));
        }
    }
}
