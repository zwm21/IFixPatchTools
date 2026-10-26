using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IFixPatch.SourceBuilder.Core.Tests;

public sealed class GraftingRejectionTests
{
    [Theory]
    [InlineData("method-missing", "Unable to rebind target method")]
    [InlineData("method-array-rank", "Unable to rebind target method")]
    [InlineData("method-array-element", "Unable to rebind target method")]
    [InlineData("method-generic-instance", "Unable to rebind target method")]
    [InlineData("method-generic-arguments", "Unable to rebind target method")]
    [InlineData("method-generic-definition", "Unable to rebind target method")]
    [InlineData("method-generic-parameter", "Unable to rebind target method")]
    [InlineData("method-byref", "Unable to rebind target method")]
    [InlineData("method-byref-shape", "Unable to rebind target method")]
    [InlineData("field-missing", "Unable to rebind target field")]
    [InlineData("field-type", "Unable to rebind target field")]
    [InlineData("type-missing", "Type not found")]
    public void RejectsImplementationReferencesThatDoNotExistInTheGraftTarget(string mismatch, string requirement)
    {
        using var fixture = new SyntheticTargetFixture();
        fixture.Change(module =>
        {
            var native = SyntheticTargetFixture.Class(module, "Example", "Native`1");
            var parameter = new GenericParameter("T", native); native.GenericParameters.Add(parameter);
            var read = SyntheticTargetFixture.Method(native, "Read", true, module.TypeSystem.Int32);
            TypeReference argument = mismatch switch
            {
                "method-array-rank" or "method-array-element" => new ArrayType(module.TypeSystem.Int32),
                "method-generic-instance" or "method-generic-arguments" or "method-generic-definition" => List(module, module.TypeSystem.Int32),
                "method-generic-parameter" => parameter,
                "method-byref" => new ByReferenceType(module.TypeSystem.Int32),
                _ => module.TypeSystem.Int32,
            };
            read.Parameters.Add(new ParameterDefinition(argument));
            native.Fields.Add(new FieldDefinition("Value", FieldAttributes.Public | FieldAttributes.Static, module.TypeSystem.Int32));
        });
        var body = mismatch switch
        {
            "method-array-rank" or "method-array-element" => "return Example.Native<int>.Read(new int[0]);",
            "method-generic-instance" or "method-generic-arguments" or "method-generic-definition" => "return Example.Native<int>.Read(new System.Collections.Generic.List<int>());",
            "method-byref" => "int value=0; return Example.Native<int>.Read(ref value);",
            "field-missing" or "field-type" => "return Example.Native<int>.Value;",
            "type-missing" => "return typeof(Example.Native<int>).Name.Length;",
            _ => "return Example.Native<int>.Read(42);",
        };
        var manifest = CompilerPipelineTests.WriteSource(fixture,
            "namespace Patch {public static class Replacement {public static int Compute() {" + body + "}}}");
        var prepared = CompiledGraftingTests.Prepare(fixture);
        var implementation = Path.Combine(fixture.DirectoryPath, "implementation.dll");
        new SourceCompiler().Compile(prepared, manifest, implementation);

        // This models separately supplied compiler references and graft metadata that disagree.
        // A MemberRef that no longer resolves must be rejected, rather than silently bound to a
        // different array rank, generic slot or field type. Public Graft accepts these artifacts.
        using (var clean = AssemblyDefinition.ReadAssembly(prepared.CleanAssemblyPath, new ReaderParameters { InMemory = true }))
        {
            var module = clean.MainModule;
            var native = module.GetType("Example.Native`1");
            var read = native.Methods.Single(method => method.Name == "Read");
            switch (mismatch)
            {
                case "method-missing": native.Methods.Remove(read); break;
                case "method-array-rank": read.Parameters[0].ParameterType = new ArrayType(module.TypeSystem.Int32, 2); break;
                case "method-array-element": read.Parameters[0].ParameterType = new ArrayType(module.TypeSystem.String); break;
                case "method-generic-instance": read.Parameters[0].ParameterType = module.TypeSystem.Object; break;
                case "method-generic-arguments": read.Parameters[0].ParameterType = List(module, module.TypeSystem.String); break;
                case "method-generic-definition":
                    var contract = new GenericInstanceType(new TypeReference("System.Collections.Generic", "IReadOnlyList`1", module, module.TypeSystem.CoreLibrary));
                    contract.GenericArguments.Add(module.TypeSystem.Int32); read.Parameters[0].ParameterType = contract; break;
                case "method-generic-parameter": read.Parameters[0].ParameterType = module.TypeSystem.Int32; break;
                case "method-byref": read.Parameters[0].ParameterType = new ByReferenceType(module.TypeSystem.String); break;
                case "method-byref-shape": read.Parameters[0].ParameterType = new ByReferenceType(module.TypeSystem.Int32); break;
                case "field-missing": native.Fields.Clear(); break;
                case "field-type": native.Fields.Single().FieldType = module.TypeSystem.String; break;
                case "type-missing": module.Types.Remove(native); break;
            }
            clean.Write(prepared.CleanAssemblyPath);
        }
        var output = Path.Combine(fixture.DirectoryPath, "grafted.dll");
        var config = Path.Combine(fixture.DirectoryPath, "process.cfg");
        var error = Assert.Throws<InvalidDataException>(() => new BodyGrafter().Graft(prepared, manifest, implementation, output, config));
        Assert.Contains(requirement, error.Message);
        Assert.Contains("Example.Native", error.Message);
        Assert.False(File.Exists(output));
        Assert.False(File.Exists(config));
    }

    [Theory]
    [InlineData("generic")]
    [InlineData("value-type")]
    public void TypeBindingsCannotAddReferenceClassStateToIncompatibleTargetKinds(string kind)
    {
        using var fixture = new SyntheticTargetFixture();
        fixture.Change(module =>
        {
            var target = SyntheticTargetFixture.Class(module, "Example", kind == "generic" ? "Destination`1" : "Destination");
            if (kind == "generic") target.GenericParameters.Add(new GenericParameter("T", target));
            else { target.BaseType = new TypeReference("System", "ValueType", module, module.TypeSystem.CoreLibrary); target.IsSealed = true; }
        });
        var manifest = CompilerPipelineTests.WriteSource(fixture,
            "namespace Patch {public class State {public int Value;} public static class Replacement {public static int Compute()=>42;}}");
        manifest.TypeBindings.Add("Patch.State", kind == "generic" ? "Example.Destination`1" : "Example.Destination");
        var error = Assert.Throws<NotSupportedException>(() => CompilerPipelineTests.CompileAndGraft(fixture, CompiledGraftingTests.Prepare(fixture), manifest));
        Assert.Contains("existing non-generic reference class", error.Message);
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, "grafted.dll")));
    }

    [Fact]
    public void RejectsATypeBoundPropertyWhoseMetadataNameAlreadyExists()
    {
        using var fixture = new SyntheticTargetFixture();
        fixture.Change(module =>
        {
            var owner = module.GetType("Example.Calculator");
            var getter = SyntheticTargetFixture.Method(owner, "ReadExisting", false, module.TypeSystem.Int32);
            getter.IsSpecialName = true;
            owner.Properties.Add(new PropertyDefinition("Value", PropertyAttributes.None, module.TypeSystem.Int32) { GetMethod = getter });
        });
        var manifest = CompilerPipelineTests.WriteSource(fixture,
            "namespace Patch {public class State {public int Value {get {return 42;}}} public static class Replacement {public static int Compute()=>42;}}");
        manifest.TypeBindings.Add("Patch.State", "Example.Calculator");
        Assert.Contains("Property already exists", Assert.Throws<InvalidDataException>(() =>
            CompilerPipelineTests.CompileAndGraft(fixture, CompiledGraftingTests.Prepare(fixture), manifest)).Message);
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, "grafted.dll")));
    }

    [Fact]
    public void RejectsAnOwnedRvaBackedFieldBeforeLosingItsInitialData()
    {
        using var fixture = new SyntheticTargetFixture();
        var manifest = CompilerPipelineTests.WriteSource(fixture,
            "namespace Patch {class State {public static int Value;} public static class Replacement {public static int Compute()=>State.Value;}}");
        var prepared = CompiledGraftingTests.Prepare(fixture);
        var implementation = Path.Combine(fixture.DirectoryPath, "implementation.dll");
        new SourceCompiler().Compile(prepared, manifest, implementation);
        using (var assembly = AssemblyDefinition.ReadAssembly(implementation, new ReaderParameters { InMemory = true }))
        {
            // A valid CLI RVA field has bytes in the PE data section, beyond a Constant row.
            // The graft contract rejects this shape because it cannot yet preserve that data.
            assembly.MainModule.GetType("Patch.State").Fields.Single().InitialValue = [42, 0, 0, 0];
            assembly.Write(implementation);
        }
        var output = Path.Combine(fixture.DirectoryPath, "grafted.dll");
        Assert.Contains("RVA data fields require", Assert.Throws<NotSupportedException>(() =>
            new BodyGrafter().Graft(prepared, manifest, implementation, output, Path.Combine(fixture.DirectoryPath, "process.cfg"))).Message);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void GraphDiscoveryRejectsCallingAnotherReplacementEntryEvenWhenOwnedStateIsPresent()
    {
        using var fixture = new SyntheticTargetFixture();
        fixture.Change(module =>
        {
            SyntheticTargetFixture.Method(module.GetType("Example.Calculator"), "Other", true, module.TypeSystem.Int32);
            module.GetType("IFix.IDMAP0").Fields.Add(new FieldDefinition("Example-Calculator-Other0",
                FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault,
                module.GetType("IFix.IDMAP0"))
            { Constant = 4 });
        });
        var manifest = CompilerPipelineTests.WriteSource(fixture, """
            namespace Patch {
              class State {public int Value;}
              public static class Replacement {
                public static int Compute() {var state=new State(); return Other()+state.Value;}
                public static int Other()=>42;
              }
            }
            """);
        manifest.Patches.Add(new PatchBinding { Target = "System.Int32 Example.Calculator::Other()", Implementation = "System.Int32 Patch.Replacement::Other()" });
        Assert.Contains("Call the target method", Assert.Throws<NotSupportedException>(() =>
            CompilerPipelineTests.CompileAndGraft(fixture, CompiledGraftingTests.Prepare(fixture), manifest)).Message);
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, "grafted.dll")));
    }

    private static GenericInstanceType List(ModuleDefinition module, TypeReference argument)
    {
        var list = new GenericInstanceType(new TypeReference("System.Collections.Generic", "List`1", module, module.TypeSystem.CoreLibrary));
        list.GenericArguments.Add(argument); return list;
    }
}
