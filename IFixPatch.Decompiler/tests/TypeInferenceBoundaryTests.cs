using IFixPatch.Core;
using IFixPatchDecompiler;
using Mono.Cecil;

namespace IFixPatch.Decompiler.Tests;

public sealed class TypeInferenceBoundaryTests
{
    [Theory]
    [InlineData("Value_With_Underscore")]
    [InlineData("Value$CompilerName")]
    public void MetadataMethodNamesRemainCallableAfterBeingRenderedAsSourceIdentifiers(string name)
    {
        using var fixture = new DecompilerFixture();
        var path = Path.Combine(fixture.DirectoryPath, "SyntheticTarget.dll");
        using (var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { InMemory = true }))
        {
            assembly.MainModule.GetType("Example.Calculator").Methods.Single(method => method.Name == "Value").Name = name;
            assembly.Write(path);
        }
        var patch = fixture.Patch();
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldarg"), fixture.Op("Ret", 1));
        fixture.Fix(patch, 0, name, 1);
        var (report, owner, _) = fixture.Decompile(patch);
        Assert.Empty(report.Problems);
        var method = Assert.Single(owner.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly));
        Assert.Equal(42, method.Invoke(null, [42]));
        Assert.DoesNotContain('$', method.Name);
    }

    [Fact]
    public void InterfaceSeedingDoesNotConfuseThisWithAnUnusedIntegerParameter()
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch(); patch.ExternTypes.Add("Example.IReader, SyntheticTarget");
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldarg"), fixture.Op("Pop"), fixture.Op("Ret"));
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldarg"), fixture.Op("Pop"), fixture.Op("Ldc_I4", 42), fixture.Op("Ret", 1));
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldarg"), fixture.Op("Pop"), fixture.Op("Ret"));
        patch.AnonStoreys.Add(new AnonStorey { CtorId = 0, HasInterfaces = true, Interfaces = [new() { TypeId = 5, MethodIds = [1, 2] }] });
        PatchValidation.RequireValid(patch, fixture.Map);
        using var source = new DummyDllSource(fixture.DirectoryPath);
        using var module = ModuleDefinition.CreateModule("IncompleteArityInference", new ModuleParameters { Kind = ModuleKind.Dll, AssemblyResolver = source.Resolver });
        var signatures = new InternalSignatures(patch, fixture.Map);
        var transpiler = new ILTranspiler(patch, fixture.Map, source, module) { SeedAnonStoreys = true, InternalReturnsValue = id => signatures.ReturnsValue[id] };
        transpiler.BuildStoreyBindings();
        var (returns, arguments) = transpiler.AnalyzeInternalTypes(signatures, _ => null);
        Assert.Equal("System.Int32", returns[1]!.FullName);
        // The sole read argument is this. The unread Read(int) parameter gives no license to
        // assign its integer type to the receiver when body analysis underestimates arity.
        Assert.DoesNotContain(arguments[1], type => type?.FullName == "System.Int32");
    }

    [Theory]
    [InlineData("Castclass", true)]
    [InlineData("Castclass", false)]
    [InlineData("Isinst", true)]
    [InlineData("Isinst", false)]
    public void NegativeTypeOperandsDistinguishTheSelectedAnonymousType(string operation, bool matchingType)
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.VirtualPatch(sharedImplementation: true);
        patch.Methods[0].Code = [fixture.Op("StackSpace", 1), fixture.Op("Newanon"), fixture.Op(operation, matchingType ? -1 : -2), fixture.Op("Ret", 1)];
        patch.FixList.Clear(); fixture.Fix(patch, 0, "ObjectValue");
        var (report, owner, _) = fixture.Decompile(patch);
        Assert.Empty(report.Problems);
        var method = owner.GetMethod("ObjectValue__m0")!;
        if (operation == "Castclass" && !matchingType)
        {
            var error = Assert.Throws<System.Reflection.TargetInvocationException>(() => method.Invoke(null, null));
            Assert.IsType<InvalidCastException>(error.InnerException);
        }
        else if (matchingType) Assert.NotNull(method.Invoke(null, null));
        else Assert.Null(method.Invoke(null, null));
    }

    [Fact]
    public void MissingOriginalMethodIsReportedAsALossOfReturnTypeInformation()
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch();
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldnull"), fixture.Op("Ret", 1));
        fixture.Fix(patch, 0, "NotInTheMetadata", 1);
        PatchValidation.RequireValid(patch, fixture.Map);
        using var source = new DummyDllSource(fixture.DirectoryPath);
        var report = PatchDecompiler.Run(patch, fixture.Map, source);
        Assert.Contains(report.Problems, problem => problem.Contains("找不到原方法") && problem.Contains("object"));
        using var stream = new MemoryStream(report.AssemblyImage);
        using var assembly = AssemblyDefinition.ReadAssembly(stream);
        var method = assembly.MainModule.Types.SelectMany(type => type.Methods).Single(method => method.Name.StartsWith("NotInTheMetadata__", StringComparison.Ordinal));
        Assert.Equal("System.Object", method.ReturnType.FullName);
        // Missing metadata cannot establish equivalence with the absent original method.
    }

    [Fact]
    public void CatchAllHandlerCatchesAThrownManagedException()
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch();
        patch.ExternTypes.Add("System.InvalidOperationException, mscorlib");
        patch.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 5, Name = ".ctor" });
        fixture.Method(patch, fixture.Op("StackSpace", (1 << 16) | 1), fixture.Op("Newobj"), fixture.Op("Throw"), fixture.Op("Pop"),
            fixture.Op("Ldc_I4", 42), fixture.Op("Stloc"), fixture.Op("Leave", 7), fixture.Op("Ldloc"), fixture.Op("Ret", 1));
        patch.Methods[0].Handlers.Add(new Eh { HandlerType = 0, CatchTypeId = -1, TryStart = 1, TryEnd = 3, HandlerStart = 3, HandlerEnd = 7 });
        fixture.Fix(patch, 0, "ReadA");
        var (report, owner, _) = fixture.Decompile(patch);
        Assert.Empty(report.Problems);
        Assert.Equal(42, owner.GetMethod("ReadA__m0")!.Invoke(null, null));
    }

    [Theory]
    [InlineData("Stfld")]
    [InlineData("Ldflda")]
    public void OpaqueClosureReceiverKeepsAnExplicitDiagnosticForStoreAndAddressOperations(string operation)
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch();
        patch.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 2, Name = ".ctor" });
        var code = new List<Instr> { fixture.Op("StackSpace", 2), fixture.Op("Ldarg") };
        if (operation == "Stfld") code.Add(fixture.Op("Ldc_I4", 42));
        code.Add(fixture.Op(operation, -1));
        if (operation == "Ldflda") code.Add(fixture.Op("Pop"));
        code.Add(fixture.Op("Ret"));
        fixture.Method(patch, [.. code]);
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldarg"), fixture.Op("CallExtern", 1 << 16), fixture.Op("Ret"));
        patch.AnonStoreys.Add(new AnonStorey { CtorId = 1, FieldTypes = [0] });
        fixture.Fix(patch, 0, "SetField", 2, 1);
        PatchValidation.RequireValid(patch, fixture.Map);
        using var source = new DummyDllSource(fixture.DirectoryPath);
        var report = PatchDecompiler.Run(patch, fixture.Map, source);
        Assert.Contains(report.Problems, problem => problem.Contains(operation) && problem.Contains("接收者") && problem.Contains("闭包"));
        Assert.Empty(report.Balance);
        // A holder is an analysis fallback, not an executable implementation of opaque VM storage.
    }

    [Fact]
    public void AddedSideTableFieldAddressIsExplicitlyReportedAsUnsupported()
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch();
        patch.Fields.Add(new FieldEntry { IsNew = true, DeclaringTypeId = 0, Name = "Added", FieldTypeId = 1, CctorId = -1 });
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldarg"), fixture.Op("Ldflda"), fixture.Op("Pop"), fixture.Op("Ret"));
        fixture.Fix(patch, 0, "SetField", 2, 1);
        PatchValidation.RequireValid(patch, fixture.Map);
        using var source = new DummyDllSource(fixture.DirectoryPath);
        var report = PatchDecompiler.Run(patch, fixture.Map, source);
        Assert.Contains(report.Problems, problem => problem.Contains("Ldflda") && problem.Contains("新增字段不支持取地址"));
        Assert.Empty(report.Balance);
    }
}
