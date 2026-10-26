using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using CecilTypeAttributes = Mono.Cecil.TypeAttributes;
using CecilFieldAttributes = Mono.Cecil.FieldAttributes;

namespace IFixPatch.SourceBuilder.Core.Tests;

public sealed class CompiledGraftingTests
{
    [Fact]
    public void PreservesSwitchLocalsArraysExceptionsAndFinallySemantics()
    {
        using var fixture = new SyntheticTargetFixture();
        var prepared = Prepare(fixture);
        var manifest = CompilerPipelineTests.WriteSource(fixture, """
            namespace Patch {
              public static class Replacement {
                public static int Compute() {
                  int total = 0;
                  for(int i=0; i<7; i++) {
                    try { switch(i) { case 0: total += 1; break; case 1: total += 2; break;
                      case 2: total += 4; break; case 3: throw new System.InvalidOperationException();
                      case 4: total += 8; break; default: total += new int[] {10, 20}[i-5]; break; } }
                    catch(System.InvalidOperationException) { total += 16; }
                    finally { total += 1; }
                  }
                  return total;
                }
              }
            }
            """);
        var (grafter, output) = CompilerPipelineTests.CompileAndGraft(fixture, prepared, manifest);
        Assert.Equal(68, Execute(output)); // 1+2+4+16+8+10+20, plus seven finally blocks.
        using var assembly = AssemblyDefinition.ReadAssembly(output);
        var method = assembly.MainModule.GetType("Example.Calculator").Methods.Single(method => method.Name == "Compute");
        Assert.Contains(method.Body.Instructions, instruction => instruction.OpCode == OpCodes.Switch);
        Assert.Contains(method.Body.ExceptionHandlers, handler => handler.HandlerType == ExceptionHandlerType.Catch);
        Assert.Contains(method.Body.ExceptionHandlers, handler => handler.HandlerType == ExceptionHandlerType.Finally);
        Assert.True(grafter.Size.MaxLocalCount > 0);
    }

    [Fact]
    public void GraftsOverloadedHelpersAndSharedCalleesWithoutIncludingUnreachableCode()
    {
        using var fixture = new SyntheticTargetFixture();
        var manifest = CompilerPipelineTests.WriteSource(fixture, """
            namespace Patch { public static class Replacement {
              public static int Compute() => Sum(3) + Sum(4, 5);
              static int Sum(int x) => Leaf(x);
              static int Sum(int x, int y) => Leaf(x) + Leaf(y);
              static int Leaf(int x) => x * 2;
              static int Unreachable() => 999;
            } }
            """);
        var (grafter, output) = CompilerPipelineTests.CompileAndGraft(fixture, Prepare(fixture), manifest);
        Assert.Equal(24, Execute(output));
        Assert.Equal(3, grafter.Helpers.Count);
        Assert.Equal(3, grafter.Helpers.Select(helper => helper.Grafted).Distinct().Count());
        Assert.DoesNotContain(grafter.Helpers, helper => helper.Implementation.Contains("Unreachable"));
    }

    [Fact]
    public void ReportsRecursiveHelperDepthWithoutRejectingAFiniteRecursiveComputation()
    {
        using var fixture = new SyntheticTargetFixture();
        var manifest = CompilerPipelineTests.WriteSource(fixture, """
            namespace Patch { public static class Replacement {
              public static int Compute() => Factorial(5);
              static int Factorial(int n) => n <= 1 ? 1 : n * Factorial(n-1);
            } }
            """);
        var (grafter, output) = CompilerPipelineTests.CompileAndGraft(fixture, Prepare(fixture), manifest);
        Assert.Equal(120, Execute(output));
        Assert.False(grafter.Size.ChainBounded);
        Assert.Contains(grafter.Diagnostics, item => item.Rule == "evaluation-stack" && item.Severity == "warn" && item.Message.Contains("recursive"));
    }

    [Theory]
    [InlineData("field")]
    [InlineData("property")]
    [InlineData("event")]
    [InlineData("return")]
    [InlineData("parameter")]
    [InlineData("base")]
    [InlineData("interface")]
    [InlineData("nested-argument")]
    [InlineData("array")]
    public void GenericInstanceEvidenceComesFromTargetMetadataSignatures(string origin)
    {
        using var fixture = new SyntheticTargetFixture();
        fixture.Change(module => AddGenericMetadata(module, origin));
        var manifest = CompilerPipelineTests.WriteSource(fixture,
            "namespace Patch { public static class Replacement { public static int Compute() => typeof(System.Collections.Generic.List<int>).IsGenericType ? 42 : 0; } }");
        var (grafter, output) = CompilerPipelineTests.CompileAndGraft(fixture, Prepare(fixture), manifest);
        Assert.Equal(42, Execute(output));
        var instance = Assert.Single(grafter.GenericInstances, item => item.Instance == "System.Collections.Generic.List`1<System.Int32>");
        Assert.True(instance.Proven);
        Assert.False(string.IsNullOrWhiteSpace(instance.ProvenBy));
        Assert.DoesNotContain(grafter.Diagnostics, item => item.Rule == "generic-instance" && item.Method == instance.Instance);
    }

    [Fact]
    public void MissingGenericMetadataEvidenceIsAWarningRatherThanProofOfUnavailableAotCode()
    {
        using var fixture = new SyntheticTargetFixture();
        var manifest = CompilerPipelineTests.WriteSource(fixture,
            "namespace Patch { public static class Replacement { public static int Compute() => typeof(System.Collections.Generic.List<int>).IsGenericType ? 42 : 0; } }");
        var (grafter, output) = CompilerPipelineTests.CompileAndGraft(fixture, Prepare(fixture), manifest);
        Assert.Equal(42, Execute(output));
        var instance = Assert.Single(grafter.GenericInstances);
        Assert.False(instance.Proven); Assert.Null(instance.ProvenBy);
        Assert.Contains(grafter.Diagnostics, item => item.Rule == "generic-instance" && item.Method == instance.Instance && item.Severity == "warn");
    }

    [Fact]
    public void OwnedClassStaticInitializationAndMutableStateKeepCSharpBehavior()
    {
        using var fixture = new SyntheticTargetFixture();
        var manifest = CompilerPipelineTests.WriteSource(fixture, """
            namespace Patch {
              public class State {
                static int count;
                static State() { count = 10; }
                public int Value {get;set;}
                public State(int value) { Value = value; count++; }
                public int Read() => Value + count;
              }
              public static class Replacement { public static int Compute() { var a=new State(20); var b=new State(30); return a.Read()+b.Read(); } }
            }
            """);
        var (grafter, output) = CompilerPipelineTests.CompileAndGraft(fixture, Prepare(fixture), manifest);
        Assert.Equal(74, Execute(output)); // both instances observe the same initialized counter, now 12.
        Assert.True(grafter.HasTypeGraph);
        Assert.DoesNotContain(grafter.Diagnostics, item => item.Severity == "fail");
    }

    [Fact]
    public void OwnedClassesCanImplementAContractAlreadyPresentOnTheTargetBridge()
    {
        using var fixture = new SyntheticTargetFixture();
        fixture.Change(module => module.GetType("IFix.ILFixInterfaceBridge").Interfaces.Add(new InterfaceImplementation(
            new TypeReference("System", "IDisposable", module, module.TypeSystem.CoreLibrary))));
        var manifest = CompilerPipelineTests.WriteSource(fixture, """
            namespace Patch {
              class State : System.IDisposable { public int Value; public void Dispose() {Value=42;} }
              public static class Replacement { public static int Compute() { var value=new State(); ((System.IDisposable)value).Dispose(); return value.Value; } }
            }
            """);
        var (_, output) = CompilerPipelineTests.CompileAndGraft(fixture, Prepare(fixture), manifest);
        Assert.Equal(42, Execute(output));
    }

    [Fact]
    public void RejectsNewInterfaceContractsThatTheTargetBridgeCannotProvide()
    {
        using var fixture = new SyntheticTargetFixture();
        var manifest = CompilerPipelineTests.WriteSource(fixture, """
            namespace Patch {
              class State : System.IDisposable { public void Dispose() {} }
              public static class Replacement { public static int Compute() { new State().Dispose(); return 42; } }
            }
            """);
        Assert.Contains("bridge does not implement System.IDisposable", Assert.Throws<NotSupportedException>(() =>
            CompilerPipelineTests.CompileAndGraft(fixture, Prepare(fixture), manifest)).Message);
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, "grafted.dll")));
    }

    [Fact]
    public void GenericImplementationHelpersRequireMonomorphization()
    {
        using var fixture = new SyntheticTargetFixture();
        var manifest = CompilerPipelineTests.WriteSource(fixture, """
            namespace Patch { public static class Replacement {
              public static int Compute() => Identity<int>(42);
              static T Identity<T>(T value) => value;
            } }
            """);
        var error = Assert.Throws<NotSupportedException>(() => CompilerPipelineTests.CompileAndGraft(fixture, Prepare(fixture), manifest));
        Assert.Contains("Generic implementation", error.Message);
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, "grafted.dll")));
    }

    internal static PreparedProfile Prepare(SyntheticTargetFixture fixture)
    {
        var prepared = fixture.Prepare();
        var references = typeof(CompiledGraftingTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(attribute => attribute.Key == "FrameworkReferenceDirectory").Value!;
        Assert.True(Directory.Exists(references), "The explicitly restored net471 reference assemblies must be available.");
        prepared.ReferenceDirectories.Add(references);
        return prepared;
    }

    [Fact]
    public void PreservesRefHelperArgumentsAndExternalGenericMethodInstantiation()
    {
        using var fixture = new SyntheticTargetFixture();
        var manifest = CompilerPipelineTests.WriteSource(fixture, """
            namespace Patch {public static class Replacement {
              public static int Compute() {
                int value=30; Add(1,2,3,4,ref value);
                string text=null;
                var previous=System.Threading.Interlocked.CompareExchange<string>(ref text,"2",null);
                return previous==null ? value+int.Parse(text) : -1;
              }
              static void Add(int a,int b,int c,int d,ref int value) {value+=a+b+c+d;}
            }}
            """);
        var (_, output) = CompilerPipelineTests.CompileAndGraft(fixture, Prepare(fixture), manifest);
        Assert.Equal(42, Execute(output));
        using var assembly = AssemblyDefinition.ReadAssembly(output);
        var method = assembly.MainModule.GetType("Example.Calculator").Methods.Single(method => method.Name == "Compute");
        var call = method.Body.Instructions.Select(instruction => instruction.Operand).OfType<GenericInstanceMethod>().Single();
        Assert.Equal("System.Threading.Interlocked", call.DeclaringType.FullName);
        Assert.Equal("System.String", Assert.Single(call.GenericArguments).FullName);
    }

    [Fact]
    public void ImportsExternalFieldsAndArrayTypedLocals()
    {
        using var fixture = new SyntheticTargetFixture();
        var manifest = CompilerPipelineTests.WriteSource(fixture, """
            namespace Patch {public static class Replacement {
              public static int Compute(){int[] values=Get();return values[0]+(int)System.DateTime.MinValue.Ticks;}
              static int[] Get()=>new int[]{42};
            }}
            """);
        var (_, output) = CompilerPipelineTests.CompileAndGraft(fixture, Prepare(fixture), manifest);
        Assert.Equal(42, Execute(output));
    }

    private static object? Execute(string output) => Assembly.Load(File.ReadAllBytes(output)).GetType("Example.Calculator")!.GetMethod("Compute")!.Invoke(null, null);

    private static void AddGenericMetadata(ModuleDefinition module, string origin)
    {
        var type = SyntheticTargetFixture.Class(module, "Evidence", "Container");
        GenericInstanceType List(TypeReference argument)
        {
            var reference = new TypeReference("System.Collections.Generic", "List`1", module, module.TypeSystem.CoreLibrary);
            var instance = new GenericInstanceType(reference); instance.GenericArguments.Add(argument); return instance;
        }
        var generic = List(module.TypeSystem.Int32);
        switch (origin)
        {
            case "field": type.Fields.Add(new FieldDefinition("Items", CecilFieldAttributes.Public, generic)); break;
            case "array": type.Fields.Add(new FieldDefinition("Items", CecilFieldAttributes.Public, new ArrayType(generic))); break;
            case "nested-argument": type.Fields.Add(new FieldDefinition("Items", CecilFieldAttributes.Public, List(generic))); break;
            case "property": type.Properties.Add(new PropertyDefinition("Items", Mono.Cecil.PropertyAttributes.None, generic)); break;
            case "event":
                var eventType = new GenericInstanceType(new TypeReference("System", "Action`1", module, module.TypeSystem.CoreLibrary));
                eventType.GenericArguments.Add(generic);
                type.Events.Add(new EventDefinition("Items", Mono.Cecil.EventAttributes.None, eventType)); break;
            case "return": SyntheticTargetFixture.Method(type, "Items", true, generic); break;
            case "parameter": SyntheticTargetFixture.Method(type, "Items", true, module.TypeSystem.Void, generic); break;
            case "base": type.BaseType = generic; break;
            case "interface":
                // The interface includes List<int> as its type argument, so both closed instances are observable.
                var contract = new GenericInstanceType(new TypeReference("System.Collections.Generic", "IEnumerable`1", module, module.TypeSystem.CoreLibrary));
                contract.GenericArguments.Add(generic); type.Interfaces.Add(new InterfaceImplementation(contract));
                type.Attributes |= CecilTypeAttributes.Abstract; break;
        }
    }
}
