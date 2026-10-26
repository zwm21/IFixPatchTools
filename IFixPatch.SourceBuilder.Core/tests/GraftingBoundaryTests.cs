using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using FieldAttributes = Mono.Cecil.FieldAttributes;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using ParameterAttributes = Mono.Cecil.ParameterAttributes;
using TypeAttributes = Mono.Cecil.TypeAttributes;

namespace IFixPatch.SourceBuilder.Core.Tests;

public sealed class GraftingBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsExistingGeneratedDefinitionsInsteadOfDuplicatingThemOnRepeatedGraft(bool graph)
    {
        using var fixture = new SyntheticTargetFixture();
        var source = graph
            ? "namespace Patch {class State {public int Read()=>42;} public static class Replacement {public static int Compute()=>new State().Read();}}"
            : "namespace Patch {public static class Replacement {public static int Compute()=>Add(40,2); static int Add(int a,int b)=>a+b;}}";
        var manifest = CompilerPipelineTests.WriteSource(fixture, source);
        var prepared = CompiledGraftingTests.Prepare(fixture);
        var (_, first) = CompilerPipelineTests.CompileAndGraft(fixture, prepared, manifest);
        // Reuse the first graft as a caller-supplied shell. Existing generated definitions must
        // be diagnosed rather than producing two same-name TypeDefs in the second artifact.
        prepared.CleanAssemblyPath = first;
        var output = Path.Combine(fixture.DirectoryPath, "second-graft.dll");
        var error = Assert.Throws<InvalidDataException>(() => new BodyGrafter().Graft(prepared, manifest,
            Path.Combine(fixture.DirectoryPath, "SyntheticImplementation.dll"), output, Path.Combine(fixture.DirectoryPath, "second.cfg")));
        Assert.Contains(graph ? "collision" : "already exists", error.Message);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void TraversesOwnedArrayAndByReferenceTypesAndRebindsANonGenericNativeMember()
    {
        using var fixture = new SyntheticTargetFixture();
        fixture.Change(module => SyntheticTargetFixture.Method(SyntheticTargetFixture.Class(module, "Example", "Native"), "Identity", true,
            module.TypeSystem.Int32, module.TypeSystem.Int32));
        var manifest = CompilerPipelineTests.WriteSource(fixture, """
            namespace Patch {
              class State {public int Value;}
              public static class Replacement {
                static int Read(ref State state) {return Example.Native.Identity(state.Value);}
                public static int Compute() {var states=new[]{new State {Value=42}}; return Read(ref states[0]);}
              }
            }
            """);
        var (_, output) = CompilerPipelineTests.CompileAndGraft(fixture, CompiledGraftingTests.Prepare(fixture), manifest);
        using (var runtime = AssemblyDefinition.ReadAssembly(output, new ReaderParameters { InMemory = true }))
        {
            var identity = runtime.MainModule.GetType("Example.Native").Methods.Single();
            identity.Body.Instructions.Clear(); identity.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
            identity.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); runtime.Write(output);
        }
        Assert.Equal(42, Assembly.Load(File.ReadAllBytes(output)).GetType("Example.Calculator")!.GetMethod("Compute")!.Invoke(null, null));
    }

    [Fact]
    public void FindsAReplacementTargetNestedAtTwoLevels()
    {
        using var fixture = new SyntheticTargetFixture();
        fixture.Change(module =>
        {
            var target = module.GetType("Example.Calculator"); module.Types.Remove(target);
            var outer = SyntheticTargetFixture.Class(module, "Example", "Outer");
            var middle = new TypeDefinition("", "Middle", TypeAttributes.NestedPublic, module.TypeSystem.Object);
            outer.NestedTypes.Add(middle);
            target.Namespace = ""; target.Attributes = (target.Attributes & ~TypeAttributes.VisibilityMask) | TypeAttributes.NestedPublic;
            middle.NestedTypes.Add(target);
            module.GetType("IFix.IDMAP0").Fields.Single(field => field.IsLiteral).Name = "Example-Outer-Middle-Calculator-Compute0";
        });
        var manifest = CompilerPipelineTests.WriteSource(fixture,
            "namespace Patch {public static class Replacement {public static int Compute()=>42;}}");
        manifest.Patches[0].Target = "System.Int32 Example.Outer/Middle/Calculator::Compute()";
        var (grafter, output) = CompilerPipelineTests.CompileAndGraft(fixture, CompiledGraftingTests.Prepare(fixture), manifest);
        Assert.Equal(42, Assembly.Load(File.ReadAllBytes(output)).GetType("Example.Outer+Middle+Calculator")!.GetMethod("Compute")!.Invoke(null, null));
        Assert.False(grafter.HasTypeGraph);
    }

    [Theory]
    [InlineData("IFix.Core")]
    [InlineData("IFix")]
    public void ResolvesNativeIdMapSlotsUsingEitherSupportedIdTagIdentity(string attributeNamespace)
    {
        using var fixture = new SyntheticTargetFixture();
        using (var runtime = AssemblyDefinition.ReadAssembly(fixture.RuntimePath, new ReaderParameters { InMemory = true }))
        {
            var module = runtime.MainModule;
            var attribute = SyntheticTargetFixture.Class(module, attributeNamespace, "IDTagAttribute");
            attribute.BaseType = new TypeReference("System", "Attribute", module, module.TypeSystem.CoreLibrary);
            var constructor = SyntheticTargetFixture.Method(attribute, ".ctor", false, module.TypeSystem.Void, module.TypeSystem.Int32);
            constructor.Body.Instructions.Clear(); constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
            constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Call, new MethodReference(".ctor", module.TypeSystem.Void, attribute.BaseType) { HasThis = true }));
            constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); runtime.Write(fixture.RuntimePath);
        }
        fixture.Change(module =>
        {
            var attribute = new TypeReference(attributeNamespace, "IDTagAttribute", module, module.AssemblyReferences.Single(reference => reference.Name == "IFix.Core"));
            var ctor = new MethodReference(".ctor", module.TypeSystem.Void, attribute) { HasThis = true };
            ctor.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
            var tag = new CustomAttribute(ctor); tag.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.Int32, 7));
            var target = module.GetType("Example.Calculator").Methods.Single(method => method.Name == "Compute");
            var ordinaryAttribute = new TypeReference("System", "ObsoleteAttribute", module, module.TypeSystem.CoreLibrary);
            target.CustomAttributes.Add(new CustomAttribute(new MethodReference(".ctor", module.TypeSystem.Void, ordinaryAttribute) { HasThis = true }));
            target.CustomAttributes.Add(tag);
            module.GetType("IFix.IDMAP0").Fields.Single(field => field.IsLiteral).Name = "Example-Calculator-Compute7";
        });
        var manifest = CompilerPipelineTests.WriteSource(fixture, "namespace Patch {public static class Replacement {public static int Compute()=>42;}}");
        var prepared = CompiledGraftingTests.Prepare(fixture);
        var implementation = Path.Combine(fixture.DirectoryPath, "implementation.dll"); new SourceCompiler().Compile(prepared, manifest, implementation);
        var binding = Assert.Single(new BodyGrafter().Graft(prepared, manifest, implementation, Path.Combine(fixture.DirectoryPath, "output.dll"), Path.Combine(fixture.DirectoryPath, "process.cfg")));
        Assert.Equal(7, binding.IdTag); Assert.Equal(3, binding.NativeSlot); Assert.Equal("Example-Calculator-Compute7", binding.NativeIdMapField);
    }

    [Theory]
    [InlineData("position")]
    [InlineData("owner-kind")]
    public void DistinguishesGenericParameterPositionsAndMethodVersusTypeOwnership(string mismatch)
    {
        using var fixture = new SyntheticTargetFixture();
        fixture.Change(module =>
        {
            var native = SyntheticTargetFixture.Class(module, "Example", mismatch == "position" ? "Native`2" : "Native`1");
            var first = new GenericParameter("T", native); native.GenericParameters.Add(first);
            var second = new GenericParameter("U", native);
            if (mismatch == "position") native.GenericParameters.Add(second);
            var read = SyntheticTargetFixture.Method(native, "Read", true, module.TypeSystem.Int32);
            if (mismatch == "owner-kind") { second = new GenericParameter("U", read); read.GenericParameters.Add(second); }
            read.Parameters.Add(new ParameterDefinition(mismatch == "position" ? first : second));
        });
        var expression = mismatch == "position" ? "Example.Native<int,int>.Read(42)" : "Example.Native<int>.Read<int>(42)";
        var manifest = CompilerPipelineTests.WriteSource(fixture, "namespace Patch {public static class Replacement {public static int Compute()=>" + expression + ";}}");
        var prepared = CompiledGraftingTests.Prepare(fixture);
        var implementation = Path.Combine(fixture.DirectoryPath, "implementation.dll"); new SourceCompiler().Compile(prepared, manifest, implementation);
        using (var target = AssemblyDefinition.ReadAssembly(prepared.CleanAssemblyPath, new ReaderParameters { InMemory = true }))
        {
            var native = target.MainModule.GetType(mismatch == "position" ? "Example.Native`2" : "Example.Native`1");
            native.Methods.Single().Parameters[0].ParameterType = native.GenericParameters[mismatch == "position" ? 1 : 0]; target.Write(prepared.CleanAssemblyPath);
        }
        Assert.Contains("Unable to rebind target method", Assert.Throws<InvalidDataException>(() => new BodyGrafter().Graft(prepared, manifest,
            implementation, Path.Combine(fixture.DirectoryPath, "output.dll"), Path.Combine(fixture.DirectoryPath, "process.cfg"))).Message);
    }

    [Theory]
    [InlineData("same")]
    [InlineData("element")]
    [InlineData("shape")]
    public void ComparesPointerMemberSignaturesWithoutDereferencingThePointer(string difference)
    {
        using var fixture = new SyntheticTargetFixture();
        fixture.Change(module => SyntheticTargetFixture.Method(SyntheticTargetFixture.Class(module, "Example", "Native"), "Ignore", true, module.TypeSystem.Void, new PointerType(module.TypeSystem.Int32)));
        var prepared = fixture.Prepare(); var implementation = Path.Combine(fixture.DirectoryPath, "implementation.dll");
        using (var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("SyntheticImplementation", new Version(1, 0)), "SyntheticImplementation", ModuleKind.Dll))
        {
            var module = assembly.MainModule; var type = SyntheticTargetFixture.Class(module, "Patch", "Replacement");
            var root = SyntheticTargetFixture.Method(type, "Compute", true, module.TypeSystem.Int32); root.Body.Instructions.Clear();
            var scope = AssemblyNameReference.Parse(prepared.TargetAssemblyIdentity); module.AssemblyReferences.Add(scope);
            var native = new TypeReference("Example", "Native", module, scope);
            var ignore = new MethodReference("Ignore", module.TypeSystem.Void, native);
            ignore.Parameters.Add(new ParameterDefinition(difference == "shape" ? module.TypeSystem.Int32 : new PointerType(difference == "element" ? module.TypeSystem.String : module.TypeSystem.Int32)));
            root.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
            if (difference != "shape") root.Body.Instructions.Add(Instruction.Create(OpCodes.Conv_U));
            root.Body.Instructions.Add(Instruction.Create(OpCodes.Call, ignore));
            root.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, 42)); root.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); assembly.Write(implementation);
        }
        var manifest = new PatchManifest { AssemblyName = "SyntheticImplementation", Patches = [new PatchBinding { Target = "System.Int32 Example.Calculator::Compute()", Implementation = "System.Int32 Patch.Replacement::Compute()" }] };
        var output = Path.Combine(fixture.DirectoryPath, "output.dll");
        void Graft() => new BodyGrafter().Graft(prepared, manifest, implementation, output, Path.Combine(fixture.DirectoryPath, "process.cfg"));
        if (difference != "same")
        {
            Assert.Contains("Unable to rebind target method", Assert.Throws<InvalidDataException>(Graft).Message); Assert.False(File.Exists(output)); return;
        }
        Graft();
        using (var runtime = AssemblyDefinition.ReadAssembly(output, new ReaderParameters { InMemory = true }))
        {
            var ignore = runtime.MainModule.GetType("Example.Native").Methods.Single(); ignore.Body.Instructions.Clear(); ignore.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); runtime.Write(output);
        }
        Assert.Equal(42, Assembly.Load(File.ReadAllBytes(output)).GetType("Example.Calculator")!.GetMethod("Compute")!.Invoke(null, null));
    }

    [Theory]
    [InlineData(false, ParameterAttributes.None, true)]
    [InlineData(true, ParameterAttributes.Out, true)]
    [InlineData(true, ParameterAttributes.None, false)]
    [InlineData(false, ParameterAttributes.Out, false)]
    [InlineData(false, ParameterAttributes.In, false)]
    public void DelegateWrappersMustPreserveByReferenceParameterDirection(bool output, ParameterAttributes wrapperDirection, bool accepted)
    {
        using var fixture = new SyntheticTargetFixture();
        fixture.Change(module =>
        {
            var callback = SyntheticTargetFixture.Class(module, "Example", "Callback"); callback.BaseType = new TypeReference("System", "MulticastDelegate", module, module.TypeSystem.CoreLibrary); callback.IsSealed = true;
            var ctor = SyntheticTargetFixture.Method(callback, ".ctor", false, module.TypeSystem.Void, module.TypeSystem.Object, module.TypeSystem.IntPtr);
            ctor.ImplAttributes = Mono.Cecil.MethodImplAttributes.Runtime | Mono.Cecil.MethodImplAttributes.Managed; ctor.Body = null;
            var invoke = SyntheticTargetFixture.Method(callback, "Invoke", false, module.TypeSystem.Int32, new ByReferenceType(module.TypeSystem.Int32));
            invoke.IsVirtual = true; invoke.IsNewSlot = true; invoke.ImplAttributes = Mono.Cecil.MethodImplAttributes.Runtime | Mono.Cecil.MethodImplAttributes.Managed; invoke.Body = null;
            invoke.Parameters[0].Attributes = output ? ParameterAttributes.Out : ParameterAttributes.None;
            var wrapper = SyntheticTargetFixture.Method(module.GetType("IFix.ILFixDynamicMethodWrapper"), "__Gen_Wrap_0", false, module.TypeSystem.Int32, new ByReferenceType(module.TypeSystem.Int32));
            wrapper.Parameters[0].Attributes = wrapperDirection;
        });
        var lambda = output ? "(out int value)=>{value=initial+2; return value;}" : "(ref int value)=>{value+=initial; return value;}";
        var manifest = CompilerPipelineTests.WriteSource(fixture, "namespace Patch {public static class Replacement {public static int Compute(){int initial=40; Example.Callback callback=" + lambda + "; int result=2; return callback(" + (output ? "out" : "ref") + " result);}}}");
        var prepared = CompiledGraftingTests.Prepare(fixture);
        if (accepted)
        {
            var (_, path) = CompilerPipelineTests.CompileAndGraft(fixture, prepared, manifest);
            Assert.Equal(42, Assembly.Load(File.ReadAllBytes(path)).GetType("Example.Calculator")!.GetMethod("Compute")!.Invoke(null, null));
        }
        else
        {
            Assert.Contains("delegate wrapper does not cover", Assert.Throws<NotSupportedException>(() => CompilerPipelineTests.CompileAndGraft(fixture, prepared, manifest)).Message);
            Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, "grafted.dll")));
        }
    }

    [Fact]
    public void MissingReplacementTargetIsReportedBeforeProducingArtifacts()
    {
        using var fixture = new SyntheticTargetFixture();
        var manifest = CompilerPipelineTests.WriteSource(fixture, "namespace Patch {public static class Replacement {public static int Compute()=>42;}}");
        manifest.Patches[0].Target = "System.Int32 Example.Calculator::Missing()";
        Assert.Contains("Method not found", Assert.Throws<InvalidDataException>(() => CompilerPipelineTests.CompileAndGraft(fixture, CompiledGraftingTests.Prepare(fixture), manifest)).Message);
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, "grafted.dll")));
    }

    [Fact]
    public void MovedReplacementEntryCanStillAccessItsPrivateOwnedStaticField()
    {
        using var fixture = new SyntheticTargetFixture();
        var manifest = CompilerPipelineTests.WriteSource(fixture, """
            namespace Patch {
              class State {public int Value;}
              public static class Replacement {
                static int increment;
                public static int Compute() {var state=new State {Value=40}; increment=2; return state.Value+increment;}
              }
            }
            """);
        var (_, output) = CompilerPipelineTests.CompileAndGraft(fixture, CompiledGraftingTests.Prepare(fixture), manifest);
        Assert.Equal(42, Assembly.Load(File.ReadAllBytes(output)).GetType("Example.Calculator")!.GetMethod("Compute")!.Invoke(null, null));
    }

    [Theory]
    [InlineData("long")]
    [InlineData("float")]
    [InlineData("double")]
    public void PreservesWideAndFloatingLiteralOperandsThroughTheSerializedIlBoundary(string kind)
    {
        using var fixture = new SyntheticTargetFixture(); var prepared = fixture.Prepare();
        var implementation = Path.Combine(fixture.DirectoryPath, "implementation.dll");
        using (var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("SyntheticImplementation", new Version(1, 0)), "SyntheticImplementation", ModuleKind.Dll))
        {
            var module = assembly.MainModule; var root = SyntheticTargetFixture.Method(SyntheticTargetFixture.Class(module, "Patch", "Replacement"), "Compute", true, module.TypeSystem.Int32); root.Body.Instructions.Clear();
            var literal = kind switch
            {
                "long" => Instruction.Create(OpCodes.Ldc_I8, 0x10000002AL),
                "float" => Instruction.Create(OpCodes.Ldc_R4, 42.75f),
                _ => Instruction.Create(OpCodes.Ldc_R8, 42.75d),
            };
            root.Body.Instructions.Add(literal); root.Body.Instructions.Add(Instruction.Create(OpCodes.Conv_I4)); root.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); assembly.Write(implementation);
        }
        var manifest = new PatchManifest { AssemblyName = "SyntheticImplementation", Patches = [new PatchBinding { Target = "System.Int32 Example.Calculator::Compute()", Implementation = "System.Int32 Patch.Replacement::Compute()" }] };
        var output = Path.Combine(fixture.DirectoryPath, "output.dll"); new BodyGrafter().Graft(prepared, manifest, implementation, output, Path.Combine(fixture.DirectoryPath, "process.cfg"));
        // conv.i4 truncates finite fractions toward zero and keeps the low 32 bits of Int64.
        Assert.Equal(42, Assembly.Load(File.ReadAllBytes(output)).GetType("Example.Calculator")!.GetMethod("Compute")!.Invoke(null, null));
        using var grafted = AssemblyDefinition.ReadAssembly(output);
        var literalOperand = grafted.MainModule.GetType("Example.Calculator").Methods.Single(method => method.Name == "Compute").Body.Instructions[0].Operand;
        if (kind == "long") Assert.Equal(0x10000002AL, Assert.IsType<long>(literalOperand));
        else if (kind == "float") Assert.Equal(42.75f, Assert.IsType<float>(literalOperand));
        else Assert.Equal(42.75d, Assert.IsType<double>(literalOperand));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DiagnosticLocationsFallBackToTheFirstVisiblePointAndHandleHiddenOnlySymbols(bool hiddenOnly)
    {
        using var fixture = new SyntheticTargetFixture();
        fixture.Change(module => SyntheticTargetFixture.Class(module, "Example", "Native"));
        var prepared = fixture.Prepare(); var implementation = Path.Combine(fixture.DirectoryPath, "implementation.dll");
        using (var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("SyntheticImplementation", new Version(1, 0)), "SyntheticImplementation", ModuleKind.Dll))
        {
            var module = assembly.MainModule; var root = SyntheticTargetFixture.Method(SyntheticTargetFixture.Class(module, "Patch", "Replacement"), "Compute", true, module.TypeSystem.Int32); root.Body.Instructions.Clear();
            var scope = AssemblyNameReference.Parse(prepared.TargetAssemblyIdentity); module.AssemblyReferences.Add(scope);
            var call = Instruction.Create(OpCodes.Call, new MethodReference("Missing", module.TypeSystem.Int32, new TypeReference("Example", "Native", module, scope)));
            var ret = Instruction.Create(OpCodes.Ret); root.Body.Instructions.Add(call); root.Body.Instructions.Add(ret);
            var document = new Document("Oracle.cs");
            root.DebugInformation.SequencePoints.Add(new SequencePoint(ret, document) { StartLine = hiddenOnly ? 0xFEEFEE : 42, EndLine = hiddenOnly ? 0xFEEFEE : 42, StartColumn = 1, EndColumn = 10 });
            assembly.Write(implementation, new WriterParameters { WriteSymbols = true, SymbolWriterProvider = new PortablePdbWriterProvider() });
        }
        var manifest = new PatchManifest { AssemblyName = "SyntheticImplementation", Patches = [new PatchBinding { Target = "System.Int32 Example.Calculator::Compute()", Implementation = "System.Int32 Patch.Replacement::Compute()" }] };
        var error = Assert.Throws<InvalidDataException>(() => new BodyGrafter().Graft(prepared, manifest, implementation, Path.Combine(fixture.DirectoryPath, "output.dll"), Path.Combine(fixture.DirectoryPath, "process.cfg")));
        Assert.Contains("Unable to rebind target method", error.Message);
        if (hiddenOnly) Assert.DoesNotContain("Oracle.cs:", error.Message);
        else Assert.Contains("Oracle.cs:42", error.Message);
    }

    [Theory]
    [InlineData("extern")]
    [InlineData("generic-owner")]
    public void RejectsImplementationHelpersThatCannotBeTranslatedIntoOrdinaryPatchMethods(string kind)
    {
        using var fixture = new SyntheticTargetFixture();
        var source = kind == "extern" ? """
            namespace Patch {public static class Replacement {
              [System.Runtime.InteropServices.DllImport("unavailable-native-fixture")]
              static extern int Read();
              public static int Compute()=>Read();
            }}
            """ : """
            namespace Patch {
              static class Helper<T> {public static int Read()=>42;}
              public static class Replacement {public static int Compute()=>Helper<int>.Read();}
            }
            """;
        var manifest = CompilerPipelineTests.WriteSource(fixture, source);
        var error = Assert.Throws<NotSupportedException>(() => CompilerPipelineTests.CompileAndGraft(fixture, CompiledGraftingTests.Prepare(fixture), manifest));
        Assert.Contains(kind == "extern" ? "helper has no body" : "Generic implementation helpers", error.Message);
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, "grafted.dll")));
    }

    [Fact]
    public void RejectsTargetOwnedMultidimensionalArrayConstructorsWithAnExplicitDiagnostic()
    {
        using var fixture = new SyntheticTargetFixture();
        var manifest = CompilerPipelineTests.WriteSource(fixture, "namespace Patch {public static class Replacement {public static int Compute()=>new Example.Calculator[6,7].Length;}}");
        Assert.Contains("Members on array types are not supported", Assert.Throws<NotSupportedException>(() =>
            CompilerPipelineTests.CompileAndGraft(fixture, CompiledGraftingTests.Prepare(fixture), manifest)).Message);
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, "grafted.dll")));
    }

    [Theory]
    [InlineData("method-flat")]
    [InlineData("method-graph")]
    [InlineData("field")]
    [InlineData("type")]
    public void UnresolvableImplementationOwnedReferencesAreRejectedBeforeArtifactsAreWritten(string referenceKind)
    {
        using var fixture = new SyntheticTargetFixture(); var prepared = fixture.Prepare(); var implementation = Path.Combine(fixture.DirectoryPath, "SyntheticImplementation.dll");
        using (var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("SyntheticImplementation", new Version(1, 0)), "SyntheticImplementation", ModuleKind.Dll))
        {
            var module = assembly.MainModule; var owner = SyntheticTargetFixture.Class(module, "Patch", "Replacement");
            var root = SyntheticTargetFixture.Method(owner, "Compute", true, module.TypeSystem.Int32); root.Body.Instructions.Clear();
            if (referenceKind == "method-graph")
            {
                var field = new FieldDefinition("State", FieldAttributes.Public | FieldAttributes.Static, module.TypeSystem.Int32); owner.Fields.Add(field);
                root.Body.Instructions.Add(Instruction.Create(OpCodes.Ldsfld, field)); root.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
            }
            if (referenceKind.StartsWith("method")) root.Body.Instructions.Add(Instruction.Create(OpCodes.Call, new MethodReference("Missing", module.TypeSystem.Int32, owner)));
            else
            {
                if (referenceKind == "field") root.Body.Instructions.Add(Instruction.Create(OpCodes.Ldsfld, new FieldReference("Missing", module.TypeSystem.Int32, owner)));
                else
                {
                    var scope = AssemblyNameReference.Parse(module.Assembly.Name.FullName);
                    module.AssemblyReferences.Add(scope);
                    root.Body.Instructions.Add(Instruction.Create(OpCodes.Ldtoken, new TypeReference("Patch", "Missing", module, scope)));
                }
                root.Body.Instructions.Add(Instruction.Create(OpCodes.Pop)); root.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, 42));
            }
            root.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); assembly.Write(implementation);
        }
        var manifest = new PatchManifest { AssemblyName = "SyntheticImplementation", Patches = [new PatchBinding { Target = "System.Int32 Example.Calculator::Compute()", Implementation = "System.Int32 Patch.Replacement::Compute()" }] };
        var output = Path.Combine(fixture.DirectoryPath, "output.dll"); var config = Path.Combine(fixture.DirectoryPath, "process.cfg");
        var error = Record.Exception(() => new BodyGrafter().Graft(prepared, manifest, implementation, output, config));
        Assert.NotNull(error);
        if (referenceKind.StartsWith("method")) Assert.IsType<InvalidDataException>(error);
        else Assert.IsType<NotSupportedException>(error);
        Assert.Contains("Missing", error.Message); Assert.False(File.Exists(output)); Assert.False(File.Exists(config));
    }

    [Fact]
    public void RejectsBindingTextThatCannotDistinguishStaticAndInstanceMethodSignatures()
    {
        using var fixture = new SyntheticTargetFixture();
        fixture.Change(module => SyntheticTargetFixture.Method(module.GetType("Example.Calculator"), "Compute", false, module.TypeSystem.Int32));
        // The CLI signatures differ by HASTHIS even though the tool's textual selector is the
        // same. Reflection independently confirms both metadata methods are loadable.
        var target = Assembly.Load(File.ReadAllBytes(fixture.TargetPath)).GetType("Example.Calculator")!;
        var methods = target.GetMethods().Where(method => method.Name == "Compute").ToArray();
        Assert.Equal(2, methods.Length); Assert.Contains(methods, method => method.IsStatic); Assert.Contains(methods, method => !method.IsStatic);
        var manifest = CompilerPipelineTests.WriteSource(fixture, "namespace Patch {public static class Replacement {public static int Compute()=>42;}}");
        Assert.Contains("Method is ambiguous", Assert.Throws<InvalidDataException>(() =>
            CompilerPipelineTests.CompileAndGraft(fixture, CompiledGraftingTests.Prepare(fixture), manifest)).Message);
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, "grafted.dll")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LongArgumentEncodingMapsBothExplicitSelfAndInstanceThis(bool instanceHelper)
    {
        using var fixture = new SyntheticTargetFixture();
        if (!instanceHelper) fixture.Change(module => { var method = module.GetType("Example.Calculator").Methods.Single(); method.IsStatic = false; method.HasThis = true; });
        var prepared = fixture.Prepare(); var implementation = Path.Combine(fixture.DirectoryPath, "implementation.dll");
        using (var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("SyntheticImplementation", new Version(1, 0)), "SyntheticImplementation", ModuleKind.Dll))
        {
            var module = assembly.MainModule; var owner = SyntheticTargetFixture.Class(module, "Patch", "Replacement");
            var root = SyntheticTargetFixture.Method(owner, "Compute", true, module.TypeSystem.Int32); root.Body.Instructions.Clear();
            if (instanceHelper)
            {
                var state = SyntheticTargetFixture.Class(module, "Patch", "State");
                var constructor = SyntheticTargetFixture.Method(state, ".ctor", false, module.TypeSystem.Void); constructor.Body.Instructions.Clear();
                constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Call, new MethodReference(".ctor", module.TypeSystem.Void, module.TypeSystem.Object) { HasThis = true })); constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                var read = SyntheticTargetFixture.Method(state, "Read", false, module.TypeSystem.Int32); read.Body.Instructions.Clear();
                read.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg, read.Body.ThisParameter)); read.Body.Instructions.Add(Instruction.Create(OpCodes.Pop)); read.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, 42)); read.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                root.Body.Instructions.Add(Instruction.Create(OpCodes.Newobj, constructor)); root.Body.Instructions.Add(Instruction.Create(OpCodes.Call, read));
            }
            else
            {
                var scope = AssemblyNameReference.Parse(prepared.TargetAssemblyIdentity); module.AssemblyReferences.Add(scope);
                root.Parameters.Add(new ParameterDefinition(new TypeReference("Example", "Calculator", module, scope)));
                root.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg, root.Parameters[0])); root.Body.Instructions.Add(Instruction.Create(OpCodes.Pop)); root.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, 42));
            }
            root.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); assembly.Write(implementation);
        }
        var manifest = new PatchManifest { AssemblyName = "SyntheticImplementation", Patches = [new PatchBinding { Target = "System.Int32 Example.Calculator::Compute()", Implementation = instanceHelper ? "System.Int32 Patch.Replacement::Compute()" : "System.Int32 Patch.Replacement::Compute(Example.Calculator)" }] };
        var output = Path.Combine(fixture.DirectoryPath, "output.dll"); new BodyGrafter().Graft(prepared, manifest, implementation, output, Path.Combine(fixture.DirectoryPath, "process.cfg"));
        var target = Assembly.Load(File.ReadAllBytes(output)).GetType("Example.Calculator")!;
        var self = instanceHelper ? null : System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(target);
        Assert.Equal(42, target.GetMethod("Compute")!.Invoke(self, null));
    }

    [Fact]
    public void AbiDiagnosticsPreserveTheInputDirectionOfAParameter()
    {
        using var fixture = new SyntheticTargetFixture();
        fixture.Change(module => module.GetType("Example.Calculator").Methods.Single().Parameters.Add(
            new ParameterDefinition("value", ParameterAttributes.In, module.TypeSystem.Int32)));
        var manifest = CompilerPipelineTests.WriteSource(fixture,
            "namespace Patch {public static class Replacement {public static int Compute(int value)=>42;}}");
        manifest.Patches[0].Target = "System.Int32 Example.Calculator::Compute(System.Int32)";
        manifest.Patches[0].Implementation = "System.Int32 Patch.Replacement::Compute(System.Int32)";
        var error = Assert.Throws<InvalidDataException>(() => CompilerPipelineTests.CompileAndGraft(fixture, CompiledGraftingTests.Prepare(fixture), manifest));
        Assert.Contains("Parameter 0 mismatch", error.Message); Assert.Contains("in System.Int32", error.Message);
        Assert.Contains("PatchImpl.cs:", error.Message);
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, "grafted.dll")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MemberRebindingDistinguishesSameNamedTypesFromDifferentAssemblies(bool field)
    {
        using var fixture = new SyntheticTargetFixture();
        const string firstName = "SignatureDependencyA", secondName = "SignatureDependencyB";
        foreach (var name in new[] { firstName, secondName })
        {
            using var dependency = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name, new Version(1, 0)), name, ModuleKind.Dll);
            SyntheticTargetFixture.Class(dependency.MainModule, "Shared", "Payload");
            dependency.Write(Path.Combine(fixture.DirectoryPath, name + ".dll"));
        }
        fixture.Change(module =>
        {
            var native = SyntheticTargetFixture.Class(module, "Example", "Native");
            foreach (var name in new[] { firstName, secondName })
            {
                var scope = new AssemblyNameReference(name, new Version(1, 0)); module.AssemblyReferences.Add(scope);
                var parameter = new TypeReference("Shared", "Payload", module, scope);
                if (field) native.Fields.Add(new FieldDefinition("Value", FieldAttributes.Public | FieldAttributes.Static, parameter));
                else SyntheticTargetFixture.Method(native, "Read", true, module.TypeSystem.Int32, parameter);
            }
        });
        var prepared = fixture.Prepare(); var implementation = Path.Combine(fixture.DirectoryPath, "implementation.dll");
        using (var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("SyntheticImplementation", new Version(1, 0)), "SyntheticImplementation", ModuleKind.Dll))
        {
            var module = assembly.MainModule; var root = SyntheticTargetFixture.Method(SyntheticTargetFixture.Class(module, "Patch", "Replacement"), "Compute", true, module.TypeSystem.Int32); root.Body.Instructions.Clear();
            var targetScope = AssemblyNameReference.Parse(prepared.TargetAssemblyIdentity); module.AssemblyReferences.Add(targetScope);
            var dependencyScope = new AssemblyNameReference(firstName, new Version(1, 0)); module.AssemblyReferences.Add(dependencyScope);
            var native = new TypeReference("Example", "Native", module, targetScope);
            var payload = new TypeReference("Shared", "Payload", module, dependencyScope);
            if (field)
            {
                root.Body.Instructions.Add(Instruction.Create(OpCodes.Ldsfld, new FieldReference("Value", payload, native)));
                root.Body.Instructions.Add(Instruction.Create(OpCodes.Pop)); root.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, 42));
            }
            else
            {
                var read = new MethodReference("Read", module.TypeSystem.Int32, native); read.Parameters.Add(new ParameterDefinition(payload));
                root.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull)); root.Body.Instructions.Add(Instruction.Create(OpCodes.Call, read));
            }
            root.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); assembly.Write(implementation);
        }
        var manifest = new PatchManifest { AssemblyName = "SyntheticImplementation", Patches = [new PatchBinding { Target = "System.Int32 Example.Calculator::Compute()", Implementation = "System.Int32 Patch.Replacement::Compute()" }] };
        var output = Path.Combine(fixture.DirectoryPath, "output.dll");
        new BodyGrafter().Graft(prepared, manifest, implementation, output, Path.Combine(fixture.DirectoryPath, "process.cfg"));
        using var result = AssemblyDefinition.ReadAssembly(output);
        var instructions = result.MainModule.GetType("Example.Calculator").Methods.Single().Body.Instructions;
        var signature = field
            ? Assert.Single(instructions.Select(instruction => instruction.Operand).OfType<FieldReference>()).FieldType
            : Assert.Single(Assert.Single(instructions.Select(instruction => instruction.Operand).OfType<MethodReference>()).Parameters).ParameterType;
        // CLI type identity includes its resolution scope. Namespace/name equality alone must
        // not conflate these two independently declared types and reject a valid reference.
        Assert.Equal("Shared.Payload", signature.FullName);
        Assert.Equal(new AssemblyNameReference(firstName, new Version(1, 0)).FullName, Assert.IsType<AssemblyNameReference>(signature.Scope).FullName);
    }

    [Fact]
    public void OrdinaryObjectConstructionInsideAnOwnedGraphDoesNotRequireADelegateWrapper()
    {
        using var fixture = new SyntheticTargetFixture();
        var manifest = CompilerPipelineTests.WriteSource(fixture, """
            namespace Patch {
              class State {public object Value=new object();}
              public static class Replacement {public static int Compute()=>new State().Value==null?0:42;}
            }
            """);
        var (_, output) = CompilerPipelineTests.CompileAndGraft(fixture, CompiledGraftingTests.Prepare(fixture), manifest);
        Assert.Equal(42, Assembly.Load(File.ReadAllBytes(output)).GetType("Example.Calculator")!.GetMethod("Compute")!.Invoke(null, null));
    }
}
