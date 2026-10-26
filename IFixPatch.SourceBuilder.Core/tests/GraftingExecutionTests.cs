using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using FieldAttributes = Mono.Cecil.FieldAttributes;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using PropertyAttributes = Mono.Cecil.PropertyAttributes;
using TypeAttributes = Mono.Cecil.TypeAttributes;

namespace IFixPatch.SourceBuilder.Core.Tests;

public sealed class GraftingExecutionTests
{
    [Theory]
    [InlineData("scalar", "return Example.Box<int>.Echo(42);", "T")]
    [InlineData("vector", "return Example.Box<int>.Echo(new int[42]).Length;", "T[]")]
    [InlineData("matrix", "return Example.Box<int>.Echo(new int[6,7]).Length;", "T[,]")]
    [InlineData("reference", "int value=42; return Example.Box<int>.Echo(ref value);", "T&")]
    [InlineData("nested", "return Example.Box<int>.Echo(new System.Collections.Generic.List<int> {40,2})[0]+2;", "System.Collections.Generic.List`1<T>")]
    [InlineData("method-generic", "return int.Parse(Example.Box<int>.Echo<string>(\"42\"));", "U")]
    public void RebindsClosedGenericMembersWithStructuredOpenSignatures(string kind, string body, string expectedParameter)
    {
        using var fixture = new SyntheticTargetFixture();
        AddGenericApi(fixture, kind);
        var manifest = CompilerPipelineTests.WriteSource(fixture,
            "namespace Patch { public static class Replacement { public static int Compute() {" + body + "} } }");
        var (_, output) = CompilerPipelineTests.CompileAndGraft(fixture, CompiledGraftingTests.Prepare(fixture), manifest);

        // Preparation intentionally replaces native method bodies with throwing metadata stubs.
        // Supply only the independent native-API identity body for this CLR execution harness;
        // the grafted replacement and every emitted MemberRef remain untouched.
        using (var runtime = AssemblyDefinition.ReadAssembly(output, new ReaderParameters { InMemory = true }))
        {
            var echo = runtime.MainModule.GetType("Example.Box`1").Methods.Single(method => method.Name == "Echo");
            echo.Body.Instructions.Clear();
            echo.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
            if (kind == "reference") echo.Body.Instructions.Add(Instruction.Create(OpCodes.Ldobj, echo.DeclaringType.GenericParameters[0]));
            echo.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            runtime.Write(output);
        }
        // The same C# inputs must retain their value after re-binding !0 / !!0 and compound signatures.
        Assert.Equal(42, Execute(output));
        using var assembly = AssemblyDefinition.ReadAssembly(output);
        var call = assembly.MainModule.GetType("Example.Calculator").Methods.Single(method => method.Name == "Compute")
            .Body.Instructions.Select(instruction => instruction.Operand).OfType<MethodReference>().Single(method => method.Name == "Echo");
        var element = call is GenericInstanceMethod generic ? generic.ElementMethod : call;
        var argument = Assert.Single(element.Parameters).ParameterType;
        if (kind == "method-generic")
        {
            // MemberRef generic parameters may have the ECMA display name !!0 rather than
            // the declaration's source name U. Ownership and position carry the signature.
            var parameter = Assert.IsType<GenericParameter>(argument);
            Assert.Equal(GenericParameterType.Method, parameter.Type);
            Assert.Equal(0, parameter.Position);
            Assert.Equal("System.String", Assert.Single(Assert.IsType<GenericInstanceMethod>(call).GenericArguments).FullName);
        }
        else Assert.Equal(expectedParameter, argument.FullName);
        Assert.Equal("System.Int32", Assert.Single(Assert.IsType<GenericInstanceType>(element.DeclaringType).GenericArguments).FullName);
        Assert.DoesNotContain(assembly.MainModule.AssemblyReferences, reference => reference.Name == manifest.AssemblyName);
    }

    [Theory]
    [InlineData("get", "public int this[int index] {get {return 40+index;}}", "return new State()[2];")]
    [InlineData("set", "public int Value; public int this[int index] {set {Value=index+value;}}", "var state=new State(); state[2]=40; return state.Value;")]
    [InlineData("both", "int value; public int this[int index] {get {return value+index;} set {this.value=value;}}", "var state=new State(); state[1]=40; return state[2];")]
    public void PreservesIndexerParametersAndAccessorShapes(string access, string members, string body)
    {
        using var fixture = new SyntheticTargetFixture();
        var manifest = CompilerPipelineTests.WriteSource(fixture,
            "namespace Patch { public class State {" + members + "} public static class Replacement {public static int Compute() {" + body + "}}}");
        var (_, output) = CompilerPipelineTests.CompileAndGraft(fixture, CompiledGraftingTests.Prepare(fixture), manifest);
        Assert.Equal(42, Execute(output));
        using var assembly = AssemblyDefinition.ReadAssembly(output);
        var indexer = Assert.Single(assembly.MainModule.Types.SelectMany(type => type.Properties), property => property.Name == "Item");
        Assert.Equal("System.Int32", Assert.Single(indexer.Parameters).ParameterType.FullName);
        Assert.Equal(access != "set", indexer.GetMethod is not null);
        Assert.Equal(access != "get", indexer.SetMethod is not null);
    }

    [Fact]
    public void CarriesExplicitInterfaceOverridesAndExecutesThroughTheExistingBridgeContract()
    {
        using var fixture = new SyntheticTargetFixture();
        fixture.Change(module => module.GetType("IFix.ILFixInterfaceBridge").Interfaces.Add(new InterfaceImplementation(
            new TypeReference("System", "IDisposable", module, module.TypeSystem.CoreLibrary))));
        var manifest = CompilerPipelineTests.WriteSource(fixture, """
            namespace Patch {
              class State : System.IDisposable {
                public int Value;
                void System.IDisposable.Dispose() {Value=42;}
              }
              public static class Replacement {
                public static int Compute() {var item=new State(); ((System.IDisposable)item).Dispose(); return item.Value;}
              }
            }
            """);
        var (_, output) = CompilerPipelineTests.CompileAndGraft(fixture, CompiledGraftingTests.Prepare(fixture), manifest);
        Assert.Equal(42, Execute(output));
        using var assembly = AssemblyDefinition.ReadAssembly(output);
        var implementation = Assert.Single(assembly.MainModule.Types.SelectMany(type => type.Methods), method => method.Overrides.Count > 0);
        Assert.Equal("System.Void System.IDisposable::Dispose()", Assert.Single(implementation.Overrides).FullName);
    }

    [Fact]
    public void PreservesOwnedInheritanceExceptionHandlersAndInstanceArgumentsBeyondShortOpcodes()
    {
        using var fixture = new SyntheticTargetFixture();
        var manifest = CompilerPipelineTests.WriteSource(fixture, """
            namespace Patch {
              class Base {
                public virtual int Read(int a,int b,int c,int d,int e) {return a+b+c+d+e;}
              }
              class State:Base {
                public override int Read(int a,int b,int c,int d,int e) {
                  try {if(e==0) throw new System.InvalidOperationException(); return base.Read(a,b,c,d,e);}
                  catch(System.InvalidOperationException) {return a+b+c+d;}
                  finally {Count++;}
                }
                public static int Count;
              }
              public static class Replacement {
                public static int Compute() {Base item=new State(); return item.Read(1,2,3,4,30)+item.Read(0,0,0,0,0)+State.Count;}
              }
            }
            """);
        var (_, output) = CompilerPipelineTests.CompileAndGraft(fixture, CompiledGraftingTests.Prepare(fixture), manifest);
        Assert.Equal(42, Execute(output)); // 40 from the ordinary call, zero from catch, two finally effects.
        using var assembly = AssemblyDefinition.ReadAssembly(output);
        Assert.DoesNotContain(assembly.MainModule.AssemblyReferences, reference => reference.Name == manifest.AssemblyName);
        var derived = Assert.Single(assembly.MainModule.Types, type => type.BaseType?.Namespace == "IFixPatch.Generated");
        Assert.Contains(derived.Methods.Single(method => method.Name == "Read").Body.ExceptionHandlers,
            handler => handler.HandlerType == ExceptionHandlerType.Catch && handler.CatchType.FullName == "System.InvalidOperationException");
    }

    [Fact]
    public void PreservesConstantFieldsOnDiscoveredOwnedTypes()
    {
        using var fixture = new SyntheticTargetFixture();
        var manifest = CompilerPipelineTests.WriteSource(fixture, """
            namespace Patch {
              class State {public const int Answer=42; public int Read()=>Answer;}
              public static class Replacement {public static int Compute()=>new State().Read();}
            }
            """);
        var (_, output) = CompilerPipelineTests.CompileAndGraft(fixture, CompiledGraftingTests.Prepare(fixture), manifest);
        Assert.Equal(42, Execute(output));
        using var assembly = AssemblyDefinition.ReadAssembly(output);
        var answer = Assert.Single(assembly.MainModule.Types.SelectMany(type => type.Fields), field => field.Name == "Answer");
        Assert.True(answer.IsLiteral);
        Assert.True(answer.IsStatic);
        Assert.Equal(42, answer.Constant);
    }

    [Fact]
    public void PreservesAbstractOwnedContractsAndConcreteOverrides()
    {
        using var fixture = new SyntheticTargetFixture();
        var manifest = CompilerPipelineTests.WriteSource(fixture, """
            namespace Patch {
              abstract class Contract {public abstract int Read();}
              class State:Contract {public override int Read()=>42;}
              public static class Replacement {public static int Compute() {Contract item=new State(); return item.Read();}}
            }
            """);
        var (_, output) = CompilerPipelineTests.CompileAndGraft(fixture, CompiledGraftingTests.Prepare(fixture), manifest);
        Assert.Equal(42, Execute(output));
        using var assembly = AssemblyDefinition.ReadAssembly(output);
        var contract = Assert.Single(assembly.MainModule.Types, type => type.IsAbstract && type.Methods.Any(method => method.IsAbstract));
        var read = Assert.Single(contract.Methods, method => method.IsAbstract);
        Assert.False(read.HasBody);
        Assert.True(read.IsVirtual);
        Assert.Equal("System.Int32", read.ReturnType.FullName);
    }

    [Fact]
    public void SupportsExplicitByReferenceSelfForAValueTypeReplacement()
    {
        using var fixture = new SyntheticTargetFixture();
        fixture.Change(module =>
        {
            var type = module.GetType("Example.Calculator");
            type.BaseType = new TypeReference("System", "ValueType", module, module.TypeSystem.CoreLibrary);
            type.IsSealed = true;
            var method = type.Methods.Single(method => method.Name == "Compute");
            method.IsStatic = false; method.HasThis = true;
        });
        var manifest = CompilerPipelineTests.WriteSource(fixture, """
            namespace Patch { public static class Replacement {
              public static int Compute(ref Example.Calculator self) {self.Offset+=42; return self.Offset;}
            }}
            """);
        manifest.Patches[0].Implementation = "System.Int32 Patch.Replacement::Compute(Example.Calculator&)";
        var (_, output) = CompilerPipelineTests.CompileAndGraft(fixture, CompiledGraftingTests.Prepare(fixture), manifest);
        var target = Assembly.Load(File.ReadAllBytes(output)).GetType("Example.Calculator")!;
        var boxed = Activator.CreateInstance(target)!;
        Assert.Equal(42, target.GetMethod("Compute")!.Invoke(boxed, null));
        Assert.Equal(42, target.GetField("Offset", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(boxed));
    }

    private static object? Execute(string output) => Assembly.Load(File.ReadAllBytes(output))
        .GetType("Example.Calculator")!.GetMethod("Compute")!.Invoke(null, null);

    private static void AddGenericApi(SyntheticTargetFixture fixture, string kind) => fixture.Change(module =>
    {
        var box = SyntheticTargetFixture.Class(module, "Example", "Box`1");
        var typeParameter = new GenericParameter("T", box); box.GenericParameters.Add(typeParameter);
        var echo = new MethodDefinition("Echo", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
        box.Methods.Add(echo);
        TypeReference parameter = typeParameter;
        switch (kind)
        {
            case "vector": parameter = new ArrayType(typeParameter); break;
            case "matrix": parameter = new ArrayType(typeParameter, 2); break;
            case "reference": parameter = new ByReferenceType(typeParameter); break;
            case "nested":
                var list = new GenericInstanceType(new TypeReference("System.Collections.Generic", "List`1", module, module.TypeSystem.CoreLibrary));
                list.GenericArguments.Add(typeParameter); parameter = list; break;
            case "method-generic":
                var methodParameter = new GenericParameter("U", echo); echo.GenericParameters.Add(methodParameter); parameter = methodParameter; break;
        }
        echo.Parameters.Add(new ParameterDefinition("value", Mono.Cecil.ParameterAttributes.None, parameter));
        echo.ReturnType = kind == "reference" ? typeParameter : parameter;
        echo.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        if (kind == "reference") echo.Body.Instructions.Add(Instruction.Create(OpCodes.Ldobj, typeParameter));
        echo.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
    });
}
