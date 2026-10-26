using IFixPatch.Core;
using IFixPatchDecompiler;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Runtime.Loader;

namespace IFixPatch.Decompiler.Tests;

public sealed class TranspilerContractTests
{
    [Fact]
    public void ClosedGenericFixPreservesItsArgumentAndReturnType()
    {
        using var fixture = new DecompilerFixture();
        RewriteTarget(fixture, module =>
        {
            var owner = module.Types.Single(type => type.Name == "Calculator");
            var method = new MethodDefinition("Identity", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
            owner.Methods.Add(method);
            var argument = new GenericParameter("T", method); method.GenericParameters.Add(argument);
            method.ReturnType = argument; method.Parameters.Add(new ParameterDefinition(argument));
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        });
        var patch = fixture.Patch();
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldarg"), fixture.Op("Ret", 1));
        fixture.Fix(patch, 0, "Identity", 1);
        patch.FixList[0].Method.IsGenericInstance = true;
        patch.FixList[0].Method.GenericArgTypeIds = [1];
        patch.FixList[0].Method.Params = [new() { IsGeneric = true, GenericName = "T" }];
        // This verifies the analysis-layer serialized signature, not that upstream emits generic fix entries.
        var (report, owner, _) = fixture.Decompile(patch);
        Assert.Empty(report.Problems);
        var identity = owner.GetMethod("Identity__m0")!;
        Assert.Equal(typeof(int), identity.ReturnType);
        Assert.Equal(typeof(int), Assert.Single(identity.GetParameters()).ParameterType);
        Assert.Equal(42, identity.Invoke(null, [42]));
        Assert.Equal(-7, identity.Invoke(null, [-7]));
    }

    [Fact]
    public void IntegerBeforeObjectConstructorRemainsAnOrdinaryDiscardedLiteral()
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch();
        patch.ExternMethods.Add(new() { DeclaringTypeId = 2, Name = ".ctor" });
        fixture.Method(patch, fixture.Op("StackSpace", 2), fixture.Op("Ldc_I4", 1), fixture.Op("Newobj"),
            fixture.Op("Pop"), fixture.Op("Ret", 1));
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldc_I4", 42), fixture.Op("Ret", 1));
        fixture.Fix(patch, 0, "ReadA");
        var (report, owner, _) = fixture.Decompile(patch);
        Assert.Empty(report.Problems);
        Assert.Equal(1, owner.GetMethod("ReadA__m0")!.Invoke(null, null));
    }

    [Fact]
    public void UnresolvedConstructorAfterAnIntegerProducesAResolutionDiagnostic()
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch();
        patch.ExternTypes.Add("Missing.Target, MissingAssembly");
        patch.ExternMethods.Add(new() { DeclaringTypeId = 5, Name = ".ctor", Params = [new() { TypeId = 1 }] });
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldc_I4", 1), fixture.Op("Newobj", 1 << 16), fixture.Op("Ret", 1));
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldc_I4", 42), fixture.Op("Ret", 1));
        fixture.Fix(patch, 0, "ObjectValue");
        Assert.Contains(Report(fixture, patch).Problems, problem => problem.Contains("Newobj") && problem.Contains("extern#0"));
    }

    [Theory]
    [InlineData(255)]
    [InlineData(256)]
    public void UnknownReceiversWithLargeAnonymousFieldIndexesAlwaysReportDegradation(int field)
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch();
        patch.ExternMethods.Add(new() { DeclaringTypeId = 2, Name = ".ctor" });
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldarg"), fixture.Op("Ldfld", ~field), fixture.Op("Ret", 1));
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldarg"), fixture.Op("CallExtern", 1 << 16), fixture.Op("Ret"));
        patch.AnonStoreys.Add(new() { CtorId = 1, FieldTypes = Enumerable.Repeat(0, field + 1).ToList() });
        fixture.Fix(patch, 0, "GetField", 2);
        var report = Report(fixture, patch);
        Assert.Contains(report.Problems, problem => problem.Contains("闭包字段 [" + field + "]"));
        // No runtime equivalence is claimed for an opaque receiver or its bounded analysis holder.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownReferenceStoresDoNotEraseTheConcreteClosureFieldType(bool unassignedLocal)
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch(); patch.InternStrings.Add("retained type");
        patch.ExternMethods.Add(new() { DeclaringTypeId = 2, Name = ".ctor" });
        fixture.Method(patch, fixture.Op("StackSpace", (2 << 16) | 2), fixture.Op("Newanon"), fixture.Op("Stloc"),
            fixture.Op("Ldloc"), fixture.Op("Ldstr"), fixture.Op("Stfld", -1), fixture.Op("Ldloc"),
            fixture.Op(unassignedLocal ? "Ldloc" : "Ldnull", unassignedLocal ? 1 : 0), fixture.Op("Stfld", -1),
            fixture.Op("Ldloc"), fixture.Op("Ldfld", -1), fixture.Op("Ret", 1));
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldarg"), fixture.Op("CallExtern", 1 << 16), fixture.Op("Ret"));
        patch.AnonStoreys.Add(new() { CtorId = 1, FieldTypes = [-2] });
        fixture.Fix(patch, 0, "TextValue");
        var (report, owner, _) = fixture.Decompile(patch);
        Assert.Empty(report.Problems);
        Assert.Null(owner.GetMethod("TextValue__m0")!.Invoke(null, null));
        using var image = new MemoryStream(report.AssemblyImage); using var assembly = AssemblyDefinition.ReadAssembly(image);
        var field = Assert.Single(assembly.MainModule.Types.Single(type => type.Name == "__Storey0").Fields);
        Assert.Equal("System.String", field.FieldType.FullName);
    }

    [Theory]
    [InlineData("Ldarg")]
    [InlineData("Ldarga")]
    [InlineData("Starg")]
    public void ArgumentAccessBeyondAnAuthoritativeSignatureIsDiagnosed(string opcode)
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch();
        var code = new List<Instr> { fixture.Op("StackSpace", 1) };
        if (opcode == "Starg") code.Add(fixture.Op("Ldnull"));
        code.Add(fixture.Op(opcode, 1));
        if (opcode != "Starg") code.Add(fixture.Op("Pop"));
        code.AddRange([fixture.Op("Ldc_I4", 42), fixture.Op("Ret", 1)]);
        fixture.Method(patch, [.. code]); fixture.Fix(patch, 0, "ReadA");
        Assert.Contains(Report(fixture, patch).Problems, problem => problem.Contains("参数索引超出方法签名: 1"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnterminatedBodiesAreReportedEvenWhenAnAnalysisReturnIsInserted(bool hasInstruction)
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch();
        var code = new List<Instr> { fixture.Op("StackSpace", 1) };
        if (hasInstruction) code.Add(fixture.Op("Nop"));
        fixture.Method(patch, [.. code]); fixture.Fix(patch, 0, "GetStaticField");
        Assert.Contains(Report(fixture, patch).Problems, problem => problem.Contains("缺少终止指令"));
    }

    [Fact]
    public void IncompatibleValueTypeAssignmentsAreDiagnosedInsteadOfClaimingBoxing()
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch();
        fixture.Method(patch, fixture.Op("StackSpace", (1 << 16) | 1), fixture.Op("Ldc_I4", 42), fixture.Op("Stloc"),
            fixture.Op("Ldc_R4", BitConverter.SingleToInt32Bits(1.25f)), fixture.Op("Stloc"), fixture.Op("Ret"));
        fixture.Fix(patch, 0, "SetStaticField", 1);
        Assert.Contains(Report(fixture, patch).Problems, problem => problem.Contains("不兼容的值类型") && problem.Contains("Int32") && problem.Contains("Single"));
    }

    [Fact]
    public void DuplicatingAnEmptyStackIsDiagnosed()
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch();
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Dup"), fixture.Op("Pop"), fixture.Op("Ret"));
        fixture.Fix(patch, 0, "SetStaticField", 1);
        Assert.Contains(Report(fixture, patch).Problems, problem => problem.Contains("Dup") && problem.Contains("为空"));
    }

    [Theory]
    [InlineData("array")]
    [InlineData("list")]
    [InlineData("byref")]
    [InlineData("pointer")]
    public void OpenGenericExternalParametersCannotBeMistakenForAClosedSignature(string shape)
    {
        using var fixture = new DecompilerFixture();
        var library = WriteLibrary(fixture, module =>
        {
            var owner = new TypeDefinition("Example", "Open`1", TypeAttributes.Public, module.TypeSystem.Object);
            module.Types.Add(owner); var argument = new GenericParameter("T", owner); owner.GenericParameters.Add(argument);
            TypeReference type = shape switch { "array" => new ArrayType(argument), "byref" => new ByReferenceType(argument), "pointer" => new PointerType(argument), _ => ListType(module, argument) };
            var method = new MethodDefinition("Read", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
            method.Parameters.Add(new ParameterDefinition(type)); owner.Methods.Add(method); method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        });
        var patch = fixture.Patch(); patch.ExternTypes.Add($"Example.Open`1, {library}");
        patch.ExternMethods.Add(new() { DeclaringTypeId = 5, Name = "Read", Params = [new() { TypeId = 1 }] });
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldc_I4", 42), fixture.Op("CallExtern", 1 << 16), fixture.Op("Ret"));
        fixture.Fix(patch, 0, "SetStaticField", 1);
        Assert.Contains(Report(fixture, patch).Problems, problem => problem.Contains("CallExtern") && problem.Contains("extern#0"));
    }

    [Fact]
    public void ExternalUnsafePointerReturnPreservesThePointeesStorage()
    {
        using var fixture = new DecompilerFixture();
        var library = WriteLibrary(fixture, module =>
        {
            var owner = new TypeDefinition("Example", "Pointers", TypeAttributes.Public, module.TypeSystem.Object); module.Types.Add(owner);
            var pointer = new PointerType(module.TypeSystem.Int32);
            var method = new MethodDefinition("Identity", MethodAttributes.Public | MethodAttributes.Static, pointer);
            method.Parameters.Add(new ParameterDefinition(pointer)); owner.Methods.Add(method);
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        }, load: true);
        var patch = fixture.Patch(); patch.ExternTypes.AddRange([$"Example.Pointers, {library}", "System.Int32*, mscorlib"]);
        patch.ExternMethods.Add(new() { DeclaringTypeId = 5, Name = "Identity", Params = [new() { TypeId = 6 }] });
        fixture.Method(patch, fixture.Op("StackSpace", (2 << 16) | 2), fixture.Op("Ldc_I4", 7), fixture.Op("Stloc"),
            fixture.Op("Ldloca"), fixture.Op("CallExtern", 1 << 16), fixture.Op("Stloc", 1), fixture.Op("Ldloc", 1),
            fixture.Op("Ldc_I4", 42), fixture.Op("Stind_I4"), fixture.Op("Ldloc"), fixture.Op("Ret", 1));
        fixture.Fix(patch, 0, "ReadA");
        var (report, owner, _) = fixture.Decompile(patch);
        Assert.Empty(report.Problems);
        Assert.Equal(42, owner.GetMethod("ReadA__m0")!.Invoke(null, null));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void UnsupportedExceptionHandlersProduceAnExplicitDiagnostic(int kind)
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch();
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Nop"), fixture.Op("Leave", 4),
            fixture.Op("Endfinally"), fixture.Op("Ret"));
        patch.Methods[0].Handlers.Add(new() { HandlerType = kind, TryStart = 1, TryEnd = 3, HandlerStart = 3, HandlerEnd = 4, CatchTypeId = -1 });
        fixture.Fix(patch, 0, "SetStaticField", 1);
        Assert.Contains(Report(fixture, patch).Problems, problem => problem.Contains("不支持的 VM 异常处理类型: " + kind));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExceptionRegionEntryCannotReferToTheNonemittedStackPrologue(bool tryEntry)
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch();
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Nop"), fixture.Op("Leave", 4), fixture.Op("Endfinally"), fixture.Op("Ret"));
        patch.Methods[0].Handlers.Add(new()
        {
            HandlerType = 2,
            TryStart = tryEntry ? 0 : 1,
            TryEnd = 3,
            HandlerStart = tryEntry ? 3 : 0,
            HandlerEnd = 4,
            CatchTypeId = -1
        });
        fixture.Fix(patch, 0, "SetStaticField", 1);
        Assert.Contains(Report(fixture, patch).Problems, problem => problem.Contains("异常块边界越界"));
    }

    [Theory]
    [InlineData("Cpobj")]
    [InlineData("Mkrefany")]
    [InlineData("Refanyval")]
    [InlineData("Endfilter")]
    [InlineData("Tail")]
    [InlineData("Unaligned")]
    public void UnsupportedOpcodeShapesProduceDiagnosticsRatherThanASuccessClaim(string mnemonic)
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch();
        var map = OpCodeMap.FromPairs(new[] { (1, "StackSpace"), (2, mnemonic), (3, "Ret") });
        fixture.Method(patch, new() { Code = 1, Operand = 2 }, new() { Code = 2, Operand = 1 }, new() { Code = 3 });
        fixture.Fix(patch, 0, "SetStaticField", 1);
        PatchValidation.RequireValid(patch, map);
        using var source = new DummyDllSource(fixture.DirectoryPath);
        var report = PatchDecompiler.Run(patch, map, source);
        Assert.Contains(report.Problems, problem => problem.Contains(mnemonic) && (problem.Contains("不支持") || problem.Contains("需要操作数")));
    }

    [Fact]
    public void MissingVirtualFunctionPointerReceiverIsDiagnosed()
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.VirtualPatch(); patch.FixList.Clear(); fixture.Fix(patch, 0, "Pointer", 1);
        patch.Methods[0].Code = [fixture.Op("StackSpace", 2), fixture.Op("Ldc_I4"), fixture.Op("Ldvirtftn2", DecompilerFixture.Slot), fixture.Op("Pop"), fixture.Op("Ldnull"), fixture.Op("Ret", 1)];
        Assert.Contains(Report(fixture, patch).Problems, problem => problem.Contains("Ldvirtftn2 缺少接收者"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnrelatedInterfaceObservationsStillPreserveRuntimeObjectIdentity(bool reverse)
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch();
        patch.ExternTypes.AddRange(["System.ICloneable, mscorlib", "System.IComparable, mscorlib"]);
        patch.InternStrings.AddRange(["first", "second"]);
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldstr"), fixture.Op("Castclass", reverse ? 6 : 5),
            fixture.Op("Call", (1 << 16) | 1), fixture.Op("Pop"), fixture.Op("Ldstr", 1), fixture.Op("Castclass", reverse ? 5 : 6),
            fixture.Op("Call", (1 << 16) | 1), fixture.Op("Ret", 1));
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldarg"), fixture.Op("Ret", 1));
        fixture.Fix(patch, 0, "ObjectValue");
        var (report, owner, _) = fixture.Decompile(patch);
        Assert.Empty(report.Problems);
        Assert.Equal("second", owner.GetMethod("ObjectValue__m0")!.Invoke(null, null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpenExternalReturnTypesProduceAMissingGenericContextDiagnostic(bool returnsOpenOwner)
    {
        using var fixture = new DecompilerFixture();
        var library = WriteLibrary(fixture, module =>
        {
            var owner = new TypeDefinition("Example", "Open`1", TypeAttributes.Public, module.TypeSystem.Object);
            module.Types.Add(owner); var argument = new GenericParameter("T", owner); owner.GenericParameters.Add(argument);
            var method = new MethodDefinition("Read", MethodAttributes.Public | MethodAttributes.Static, returnsOpenOwner ? owner : argument); owner.Methods.Add(method);
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull)); method.Body.Instructions.Add(Instruction.Create(OpCodes.Throw));
        });
        var patch = fixture.Patch(); patch.ExternTypes.Add($"Example.Open`1, {library}");
        patch.ExternMethods.Add(new() { DeclaringTypeId = 5, Name = "Read" });
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("CallExtern"), fixture.Op("Ret", 1));
        fixture.Fix(patch, 0, "ObjectValue");
        // The open provider is incomplete call-site metadata. This test only requires a diagnostic.
        Assert.Contains(Report(fixture, patch).Problems, problem => problem.Contains("缺少泛型实参"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IncompatibleValueTypeObservationsAreReportedWithoutInventingABoxingContract(bool missingEnumValue)
    {
        using var fixture = new DecompilerFixture();
        var library = WriteLibrary(fixture, module =>
        {
            var value = new TypeDefinition("Example", "Value", TypeAttributes.Public | TypeAttributes.Sealed,
                new TypeReference("System", missingEnumValue ? "Enum" : "ValueType", module, module.TypeSystem.CoreLibrary));
            module.Types.Add(value);
            if (!missingEnumValue) value.Fields.Add(new FieldDefinition("Number", FieldAttributes.Public, module.TypeSystem.Int32));
            var api = new TypeDefinition("Example", "Api", TypeAttributes.Public, module.TypeSystem.Object); module.Types.Add(api);
            var method = new MethodDefinition("Read", MethodAttributes.Public | MethodAttributes.Static, value); api.Methods.Add(method);
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull)); method.Body.Instructions.Add(Instruction.Create(OpCodes.Throw));
        });
        var patch = fixture.Patch(); patch.ExternTypes.Add($"Example.Api, {library}");
        patch.ExternMethods.Add(new() { DeclaringTypeId = 5, Name = "Read" });
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("CallExtern"), fixture.Op("Call", (1 << 16) | 1), fixture.Op("Pop"),
            fixture.Op("Ldc_I4", 42), fixture.Op("Call", (1 << 16) | 1), fixture.Op("Ret", 1));
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldarg"), fixture.Op("Ret", 1));
        fixture.Fix(patch, 0, "ObjectValue");
        var report = Report(fixture, patch);
        Assert.Contains(report.Problems, problem => problem.Contains(missingEnumValue ? "枚举缺少底层值字段" : "不兼容的值类型"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DelegateReceiversLoadedFromNonzeroArgumentsOrNativeStaticsPreserveTheirValue(bool nativeStatic)
    {
        using var fixture = new DecompilerFixture();
        RewriteTarget(fixture, module =>
        {
            var owner = module.Types.Single(type => type.Name == "Calculator");
            var result = new GenericInstanceType(new TypeReference("System", "Func`1", module, module.TypeSystem.CoreLibrary)); result.GenericArguments.Add(module.TypeSystem.Int32);
            var method = new MethodDefinition("Create", MethodAttributes.Public | MethodAttributes.Static, result);
            method.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32)); method.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
            owner.Methods.Add(method); method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull)); method.Body.Instructions.Add(Instruction.Create(OpCodes.Throw));
        });
        var patch = fixture.Patch(); patch.ExternTypes.Add("System.String, mscorlib");
        patch.Fields.Add(new() { DeclaringTypeId = 5, Name = "Empty" });
        patch.ExternMethods.Add(new() { DeclaringTypeId = 3, Name = ".ctor", Params = [new() { TypeId = 2 }, new() { TypeId = 4 }] });
        patch.ExternMethods.Add(new() { DeclaringTypeId = 5, Name = "get_Length" });
        fixture.Method(patch, fixture.Op("StackSpace", 2), fixture.Op(nativeStatic ? "Ldsfld" : "Ldarg", nativeStatic ? 0 : 1),
            fixture.Op("Ldc_I4", 1), fixture.Op("Newobj", 2 << 16), fixture.Op("Ret", 1));
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldarg"), fixture.Op("CallExtern", (1 << 16) | 1), fixture.Op("Ret", 1));
        fixture.Fix(patch, 0, "Create", 1, 5);
        var (report, owner, _) = fixture.Decompile(patch);
        Assert.Empty(report.Problems);
        var callback = Assert.IsType<Func<int>>(owner.GetMethod("Create__m0")!.Invoke(null, [42, "captured"]));
        Assert.Equal(nativeStatic ? 0 : 8, callback());
    }

    [Fact]
    public void IncompatibleSerializedStaticTypeIsPreservedAndDiagnosed()
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.VirtualPatch(sharedImplementation: true);
        patch.StaticFields.Add(new() { FieldTypeId = 1, CctorId = -1 });
        patch.Methods[0].Code = [fixture.Op("StackSpace", 1), fixture.Op("Newanon"), fixture.Op("Stsfld", -1), fixture.Op("Ldc_I4", 42), fixture.Op("Ret", 1)];
        patch.FixList.Clear(); fixture.Fix(patch, 0, "ReadA");
        var report = Report(fixture, patch);
        Assert.Contains(report.Problems, problem => problem.Contains("静态字段声明类型与匿名对象不兼容"));
        using var image = new MemoryStream(report.AssemblyImage); using var assembly = AssemblyDefinition.ReadAssembly(image);
        Assert.Equal("System.Int32", Assert.Single(assembly.MainModule.Types.Single(type => type.Name == "__VMStatics").Fields).FieldType.FullName);
    }

    [Fact]
    public void AFieldIndexValidOnlyForAnotherReceiverProducesADegradationDiagnostic()
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.VirtualPatch(sharedImplementation: true);
        patch.AnonStoreys[0].FieldTypes.Clear();
        patch.Methods[1].Code = [fixture.Op("StackSpace", 1), fixture.Op("Ldarg"), fixture.Op("CallExtern", 1 << 16), fixture.Op("Ret")];
        patch.Methods[0].Code = [fixture.Op("StackSpace", 1), fixture.Op("Newanon"), fixture.Op("Ldfld", -1), fixture.Op("Ret", 1)];
        patch.FixList.Clear(); fixture.Fix(patch, 0, "ReadA");
        Assert.Contains(Report(fixture, patch).Problems, problem => problem.Contains("接收者推不出所属闭包") && problem.Contains("[0]"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IncompleteReferenceHierarchiesProduceAResolutionDiagnosticInEitherOrder(bool reverse)
    {
        using var fixture = new DecompilerFixture();
        var library = WriteLibrary(fixture, module =>
        {
            foreach (var missingBase in new[] { false, true })
            {
                var owner = new TypeDefinition("Example", missingBase ? "Incomplete" : "Complete", TypeAttributes.Public,
                    missingBase ? new TypeReference("Missing", "Base", module, module) : module.TypeSystem.Object);
                module.Types.Add(owner);
                var method = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, module.TypeSystem.Void);
                owner.Methods.Add(method); method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull)); method.Body.Instructions.Add(Instruction.Create(OpCodes.Throw));
            }
        });
        var patch = fixture.Patch(); patch.ExternTypes.AddRange([$"Example.Complete, {library}", $"Example.Incomplete, {library}"]);
        patch.ExternMethods.Add(new() { DeclaringTypeId = 5, Name = ".ctor" }); patch.ExternMethods.Add(new() { DeclaringTypeId = 6, Name = ".ctor" });
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Newobj", reverse ? 1 : 0), fixture.Op("Call", (1 << 16) | 1), fixture.Op("Pop"),
            fixture.Op("Newobj", reverse ? 0 : 1), fixture.Op("Call", (1 << 16) | 1), fixture.Op("Ret", 1));
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldarg"), fixture.Op("Ret", 1)); fixture.Fix(patch, 0, "ObjectValue");
        Assert.Contains(Report(fixture, patch).Problems, problem => problem.Contains("无法解析引用类型的基类链"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FixedMethodsSelectTheExactParameterTypesAndGenericArity(bool competingGeneric)
    {
        using var fixture = new DecompilerFixture();
        RewriteTarget(fixture, module =>
        {
            var owner = module.Types.Single(type => type.Name == "Calculator");
            var competing = new MethodDefinition("Overloaded", MethodAttributes.Public | MethodAttributes.Static,
                competingGeneric ? module.TypeSystem.Int32 : module.TypeSystem.String);
            owner.Methods.Add(competing);
            if (competingGeneric) competing.GenericParameters.Add(new GenericParameter("T", competing));
            competing.Parameters.Add(new ParameterDefinition(competingGeneric ? module.TypeSystem.Int32 : module.TypeSystem.String));
            competing.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); competing.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            var selected = new MethodDefinition("Overloaded", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32);
            owner.Methods.Add(selected); selected.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
            selected.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); selected.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        });
        var patch = fixture.Patch();
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldarg"), fixture.Op("Ret", 1));
        fixture.Fix(patch, 0, "Overloaded", 1);
        var (report, owner, _) = fixture.Decompile(patch);
        Assert.Empty(report.Problems);
        var method = owner.GetMethod("Overloaded__m0")!;
        Assert.Equal(typeof(int), method.ReturnType);
        Assert.Equal(42, method.Invoke(null, [42]));
    }

    [Fact]
    public void InstanceFixKeepsItsOriginalReceiverAndReadsThatObjectsField()
    {
        using var fixture = new DecompilerFixture();
        var library = WriteLibrary(fixture, module =>
        {
            var owner = new TypeDefinition("Example", "Counter", TypeAttributes.Public, module.TypeSystem.Object); module.Types.Add(owner);
            var field = new FieldDefinition("Number", FieldAttributes.Public, module.TypeSystem.Int32); owner.Fields.Add(field);
            var constructor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, module.TypeSystem.Void);
            owner.Methods.Add(constructor); var il = constructor.Body.GetILProcessor(); il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, new MethodReference(".ctor", module.TypeSystem.Void, module.TypeSystem.Object) { HasThis = true }); il.Emit(OpCodes.Ret);
            var read = new MethodDefinition("Read", MethodAttributes.Public, module.TypeSystem.Int32); owner.Methods.Add(read);
            il = read.Body.GetILProcessor(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, field); il.Emit(OpCodes.Ret);
        }, load: true);
        var patch = fixture.Patch(); patch.ExternTypes.Add($"Example.Counter, {library}"); patch.Fields.Add(new() { DeclaringTypeId = 5, Name = "Number" });
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldarg"), fixture.Op("Ldfld"), fixture.Op("Ret", 1));
        patch.FixList.Add(new() { MethodId = 0, Method = new() { DeclaringTypeId = 5, Name = "Read" } });
        var report = Report(fixture, patch); Assert.Empty(report.Problems);
        var runtime = AssemblyLoadContext.Default.Assemblies.Single(assembly => assembly.GetName().Name == library);
        var counter = Activator.CreateInstance(runtime.GetType("Example.Counter")!)!; counter.GetType().GetField("Number")!.SetValue(counter, 42);
        var generated = System.Reflection.Assembly.Load(report.AssemblyImage);
        Assert.Equal(42, generated.GetType("Example.Counter__Patch")!.GetMethod("Read__m0")!.Invoke(null, [counter]));
    }

    [Fact]
    public void UnclosedGenericFixParameterProducesADiagnostic()
    {
        using var fixture = new DecompilerFixture();
        var library = WriteLibrary(fixture, module =>
        {
            var owner = new TypeDefinition("Example", "Open`1", TypeAttributes.Public, module.TypeSystem.Object); module.Types.Add(owner);
            var argument = new GenericParameter("T", owner); owner.GenericParameters.Add(argument);
            var method = new MethodDefinition("Read", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
            method.GenericParameters.Add(new GenericParameter("U", method));
            method.Parameters.Add(new ParameterDefinition(argument)); owner.Methods.Add(method); method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        });
        var patch = fixture.Patch(); patch.ExternTypes.Add($"Example.Open`1, {library}");
        fixture.Method(patch, fixture.Op("StackSpace"), fixture.Op("Ret"));
        patch.FixList.Add(new() { MethodId = 0, Method = new() { DeclaringTypeId = 5, Name = "Read", IsGenericInstance = true, GenericArgTypeIds = [1], Params = [new() { IsGeneric = true, GenericName = "T" }] } });
        Assert.Contains(Report(fixture, patch).Problems, problem => problem.Contains("修复方法签名缺少泛型实参"));
    }

    [Fact]
    public void DelegateBindingWithTooManyTargetArgumentsProducesADiagnostic()
    {
        using var fixture = new DecompilerFixture();
        var patch = fixture.Patch();
        patch.ExternMethods.Add(new() { DeclaringTypeId = 3, Name = ".ctor", Params = [new() { TypeId = 2 }, new() { TypeId = 4 }] });
        fixture.Method(patch, fixture.Op("StackSpace", 2), fixture.Op("Ldnull"), fixture.Op("Ldc_I4", 1), fixture.Op("Newobj", 2 << 16), fixture.Op("Ret", 1));
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldarg", 2), fixture.Op("Ret", 1));
        fixture.Fix(patch, 0, "ObjectValue");
        Assert.Contains(Report(fixture, patch).Problems, problem => problem.Contains("委托目标签名与 Invoke 参数不兼容"));
    }

    [Fact]
    public void StrippedDelegateMetadataWithoutInvokeProducesADiagnostic()
    {
        using var fixture = new DecompilerFixture();
        var library = WriteLibrary(fixture, module =>
        {
            var owner = new TypeDefinition("Example", "StrippedDelegate", TypeAttributes.Public | TypeAttributes.Sealed,
                new TypeReference("System", "MulticastDelegate", module, module.TypeSystem.CoreLibrary)); module.Types.Add(owner);
            var constructor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                module.TypeSystem.Void)
            { ImplAttributes = MethodImplAttributes.Runtime };
            constructor.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object)); constructor.Parameters.Add(new ParameterDefinition(module.TypeSystem.IntPtr));
            owner.Methods.Add(constructor);
        });
        var patch = fixture.Patch(); patch.ExternTypes.Add($"Example.StrippedDelegate, {library}");
        patch.ExternMethods.Add(new() { DeclaringTypeId = 5, Name = ".ctor", Params = [new() { TypeId = 2 }, new() { TypeId = 4 }] });
        fixture.Method(patch, fixture.Op("StackSpace", 2), fixture.Op("Ldnull"), fixture.Op("Ldc_I4", 1), fixture.Op("Newobj", 2 << 16), fixture.Op("Ret", 1));
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldc_I4", 42), fixture.Op("Ret", 1)); fixture.Fix(patch, 0, "ObjectValue");
        Assert.Contains(Report(fixture, patch).Problems, problem => problem.Contains("委托元数据缺少 Invoke 方法"));
    }

    [Fact]
    public void MissingValueTypeDefinitionsProduceAResolutionDiagnostic()
    {
        using var fixture = new DecompilerFixture();
        var library = WriteLibrary(fixture, module =>
        {
            var owner = new TypeDefinition("Example", "Api", TypeAttributes.Public, module.TypeSystem.Object); module.Types.Add(owner);
            var result = new TypeReference("Missing", "Value", module, module, true);
            var read = new MethodDefinition("Read", MethodAttributes.Public | MethodAttributes.Static, result); owner.Methods.Add(read);
            read.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull)); read.Body.Instructions.Add(Instruction.Create(OpCodes.Throw));
        });
        var patch = fixture.Patch(); patch.ExternTypes.Add($"Example.Api, {library}");
        patch.ExternMethods.Add(new() { DeclaringTypeId = 5, Name = "Read" });
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("CallExtern"), fixture.Op("Call", (1 << 16) | 1), fixture.Op("Pop"),
            fixture.Op("Ldc_I4", 42), fixture.Op("Call", (1 << 16) | 1), fixture.Op("Ret", 1));
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldarg"), fixture.Op("Ret", 1)); fixture.Fix(patch, 0, "ObjectValue");
        Assert.Contains(Report(fixture, patch).Problems, problem => problem.Contains("无法解析值类型"));
    }

    [Theory]
    [InlineData("scalar", 11)]
    [InlineData("array", 22)]
    [InlineData("list", 33)]
    [InlineData("byref", 44)]
    [InlineData("pointer", 55)]
    [InlineData("matrix", 66)]
    [InlineData("pair", 77)]
    public void SerializedGenericParameterShapesSelectTheCorrectOverload(string shape, int expected)
    {
        using var fixture = new DecompilerFixture();
        var library = WriteLibrary(fixture, module =>
        {
            var owner = new TypeDefinition("Example", "Api", TypeAttributes.Public, module.TypeSystem.Object); module.Types.Add(owner);
            foreach (var (candidateShape, result) in new[] { ("scalar", 11), ("array", 22), ("list", 33), ("byref", 44), ("pointer", 55), ("matrix", 66), ("pair", 77) })
            {
                var method = new MethodDefinition("Read", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32); owner.Methods.Add(method);
                var argument = new GenericParameter("T", method); method.GenericParameters.Add(argument);
                TypeReference parameter = candidateShape switch
                {
                    "array" => new ArrayType(argument),
                    "list" => ListType(module, argument),
                    "byref" => new ByReferenceType(argument),
                    "pointer" => new PointerType(argument),
                    "matrix" => new ArrayType(argument, 2),
                    "pair" => PairType(module, argument),
                    _ => argument
                };
                method.Parameters.Add(new ParameterDefinition(parameter));
                var il = method.Body.GetILProcessor(); il.Emit(OpCodes.Ldc_I4, result); il.Emit(OpCodes.Ret);
            }
        }, load: true);
        var patch = fixture.Patch(); patch.ExternTypes.Add($"Example.Api, {library}");
        patch.ExternMethods.Add(new()
        {
            DeclaringTypeId = 5,
            Name = "Read",
            IsGenericInstance = true,
            GenericArgTypeIds = [1],
            Params = [new() { IsGeneric = true, GenericName = shape switch { "array" => "T[]", "list" => "System.Collections.Generic.List`1[T]", "byref" => "T&", "pointer" => "T*", "matrix" => "T[,]", "pair" => "System.Tuple`2[T,System.Int32]", _ => "T" } }]
        });
        var code = new List<Instr> { fixture.Op("StackSpace", (1 << 16) | 2) };
        if (shape == "array") code.AddRange([fixture.Op("Ldc_I4", 1), fixture.Op("Newarr", 1)]);
        else if (shape is "list" or "matrix" or "pair") code.Add(fixture.Op("Ldnull"));
        else if (shape is "byref" or "pointer") code.AddRange([fixture.Op("Ldc_I4", 7), fixture.Op("Stloc"), fixture.Op("Ldloca")]);
        else code.Add(fixture.Op("Ldc_I4", 7));
        code.AddRange([fixture.Op("CallExtern", 1 << 16), fixture.Op("Ret", 1)]);
        fixture.Method(patch, [.. code]); fixture.Fix(patch, 0, "ReadA");
        var (report, owner, _) = fixture.Decompile(patch); Assert.Empty(report.Problems);
        Assert.Equal(expected, owner.GetMethod("ReadA__m0")!.Invoke(null, null));
    }

    [Fact]
    public void ClosedClassGenericOverloadsThatCollapseToTheSameSignatureAreDiagnosedAsAmbiguous()
    {
        using var fixture = new DecompilerFixture();
        var library = WriteLibrary(fixture, module =>
        {
            var owner = new TypeDefinition("Example", "Open`1", TypeAttributes.Public, module.TypeSystem.Object); module.Types.Add(owner);
            var argument = new GenericParameter("T", owner); owner.GenericParameters.Add(argument);
            foreach (var parameter in new TypeReference[] { argument, module.TypeSystem.Int32 })
            {
                var method = new MethodDefinition("Read", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32);
                method.Parameters.Add(new ParameterDefinition(parameter)); owner.Methods.Add(method);
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_1)); method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            }
        });
        var patch = fixture.Patch(); patch.ExternTypes.Add($"Example.Open`1[[System.Int32, mscorlib]], {library}");
        patch.ExternMethods.Add(new() { DeclaringTypeId = 5, Name = "Read", Params = [new() { TypeId = 1 }] });
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldc_I4", 42), fixture.Op("CallExtern", 1 << 16), fixture.Op("Ret", 1)); fixture.Fix(patch, 0, "ReadA");
        Assert.Contains(Report(fixture, patch).Problems, problem => problem.Contains("外部方法签名存在歧义"));
    }

    [Fact]
    public void MissingExternalParameterMetadataProducesAResolutionDiagnostic()
    {
        using var fixture = new DecompilerFixture();
        var library = WriteLibrary(fixture, module =>
        {
            var owner = new TypeDefinition("Example", "Api", TypeAttributes.Public, module.TypeSystem.Object); module.Types.Add(owner);
            var method = new MethodDefinition("Read", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
            method.Parameters.Add(new ParameterDefinition(new TypeReference("Missing", "Parameter", module, new AssemblyNameReference("MissingAssembly", new Version(1, 0)))));
            owner.Methods.Add(method); method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        });
        var patch = fixture.Patch(); patch.ExternTypes.AddRange([$"Example.Api, {library}", "Missing.Parameter, MissingAssembly"]);
        patch.ExternMethods.Add(new() { DeclaringTypeId = 5, Name = "Read", Params = [new() { TypeId = 6 }] });
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldnull"), fixture.Op("CallExtern", 1 << 16), fixture.Op("Ret"));
        fixture.Fix(patch, 0, "SetStaticField", 1);
        Assert.Contains(Report(fixture, patch).Problems, problem => problem.Contains("无法解析外部方法参数类型"));
    }

    [Fact]
    public void TruncatedClassGenericArgumentsProduceADiagnosticInsteadOfAnOpenSignature()
    {
        using var fixture = new DecompilerFixture();
        var library = WriteLibrary(fixture, module =>
        {
            var owner = new TypeDefinition("Example", "Open`2", TypeAttributes.Public, module.TypeSystem.Object); module.Types.Add(owner);
            owner.GenericParameters.Add(new GenericParameter("T", owner)); var missingArgument = new GenericParameter("U", owner); owner.GenericParameters.Add(missingArgument);
            var method = new MethodDefinition("Read", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
            method.Parameters.Add(new ParameterDefinition(missingArgument)); owner.Methods.Add(method); method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        });
        var patch = fixture.Patch(); patch.ExternTypes.Add($"Example.Open`2[[System.Int32, mscorlib]], {library}");
        patch.ExternMethods.Add(new() { DeclaringTypeId = 5, Name = "Read", Params = [new() { TypeId = 1 }] });
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldc_I4", 42), fixture.Op("CallExtern", 1 << 16), fixture.Op("Ret")); fixture.Fix(patch, 0, "SetStaticField", 1);
        Assert.Contains(Report(fixture, patch).Problems, problem => problem.Contains("缺少泛型实参"));
    }

    [Fact]
    public void ADelegateCreatedInsideItsClosureConstructorBindsArgZeroToThatClosure()
    {
        using var fixture = new DecompilerFixture(); var patch = fixture.Patch();
        patch.ExternMethods.Add(new() { DeclaringTypeId = 2, Name = ".ctor" });
        patch.ExternMethods.Add(new() { DeclaringTypeId = 3, Name = ".ctor", Params = [new() { TypeId = 2 }, new() { TypeId = 4 }] });
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Newanon"), fixture.Op("Ldfld", -2), fixture.Op("Ret", 1));
        fixture.Method(patch, fixture.Op("StackSpace", 3), fixture.Op("Ldarg"), fixture.Op("CallExtern", 1 << 16),
            fixture.Op("Ldarg"), fixture.Op("Ldc_I4", 42), fixture.Op("Stfld", -1), fixture.Op("Ldarg"), fixture.Op("Ldarg"),
            fixture.Op("Ldc_I4", 2), fixture.Op("Newobj", (2 << 16) | 1), fixture.Op("Stfld", -2), fixture.Op("Ret"));
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldarg"), fixture.Op("Ldfld", -1), fixture.Op("Ret", 1));
        patch.AnonStoreys.Add(new() { CtorId = 1, FieldTypes = [2, -2] }); fixture.Fix(patch, 0, "ObjectValue");
        var (report, owner, _) = fixture.Decompile(patch); Assert.Empty(report.Problems);
        var callback = Assert.IsType<Func<int>>(owner.GetMethod("ObjectValue__m0")!.Invoke(null, null)); Assert.Equal(42, callback());
    }

    [Fact]
    public void CompositeMethodParameterNamesCloseNestedClassArgumentsBeforeMatching()
    {
        using var fixture = new DecompilerFixture();
        var library = WriteLibrary(fixture, module =>
        {
            var owner = new TypeDefinition("Example", "Box`1", TypeAttributes.Public, module.TypeSystem.Object); module.Types.Add(owner);
            var classArgument = new GenericParameter("T", owner); owner.GenericParameters.Add(classArgument);
            var method = new MethodDefinition("Read", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32); owner.Methods.Add(method);
            var methodArgument = new GenericParameter("U", method); method.GenericParameters.Add(methodArgument);
            var parameter = new GenericInstanceType(new TypeReference("System", "Tuple`2", module, module.TypeSystem.CoreLibrary));
            parameter.GenericArguments.Add(classArgument); parameter.GenericArguments.Add(methodArgument); method.Parameters.Add(new ParameterDefinition(parameter));
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, 77)); method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        }, load: true);
        var patch = fixture.Patch(); patch.ExternTypes.Add($"Example.Box`1[[System.Collections.Generic.List`1[[System.Int32, mscorlib]], mscorlib]], {library}");
        patch.ExternMethods.Add(new()
        {
            DeclaringTypeId = 5,
            Name = "Read",
            IsGenericInstance = true,
            GenericArgTypeIds = [1],
            Params = [new() { IsGeneric = true, GenericName = "System.Tuple`2[System.Collections.Generic.List`1[System.Int32],U]" }]
        });
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldnull"), fixture.Op("CallExtern", 1 << 16), fixture.Op("Ret", 1)); fixture.Fix(patch, 0, "ReadA");
        var (report, owner, _) = fixture.Decompile(patch); Assert.Empty(report.Problems);
        Assert.Equal(77, owner.GetMethod("ReadA__m0")!.Invoke(null, null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParameterTypesWithTheSameNameInDifferentAssembliesCannotBindAnExternalMethod(bool nested)
    {
        using var fixture = new DecompilerFixture();
        var expectedLibrary = WriteLibrary(fixture, module => module.Types.Add(new TypeDefinition("Shared", "Payload", TypeAttributes.Public, module.TypeSystem.Object)));
        var otherLibrary = WriteLibrary(fixture, module => module.Types.Add(new TypeDefinition("Shared", "Payload", TypeAttributes.Public, module.TypeSystem.Object)));
        var apiLibrary = WriteLibrary(fixture, module =>
        {
            var owner = new TypeDefinition("Example", "Api", TypeAttributes.Public, module.TypeSystem.Object); module.Types.Add(owner);
            TypeReference parameter = new TypeReference("Shared", "Payload", module, new AssemblyNameReference(otherLibrary, new Version(1, 0)));
            if (nested) parameter = ListType(module, parameter);
            var method = new MethodDefinition("Read", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
            method.Parameters.Add(new ParameterDefinition(parameter)); owner.Methods.Add(method); method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        });
        var patch = fixture.Patch(); patch.ExternTypes.Add($"Example.Api, {apiLibrary}");
        patch.ExternTypes.Add(nested ? $"System.Collections.Generic.List`1[[Shared.Payload, {expectedLibrary}]], mscorlib" : $"Shared.Payload, {expectedLibrary}");
        patch.ExternMethods.Add(new() { DeclaringTypeId = 5, Name = "Read", Params = [new() { TypeId = 6 }] });
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldnull"), fixture.Op("CallExtern", 1 << 16), fixture.Op("Ret")); fixture.Fix(patch, 0, "SetStaticField", 1);
        Assert.Contains(Report(fixture, patch).Problems, problem => problem.Contains("CallExtern") && problem.Contains("extern#0"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReusingALocalForDifferentReferenceTypesNeedsNoBoxingDiagnostic(bool stringLast)
    {
        using var fixture = new DecompilerFixture(); var patch = fixture.Patch();
        patch.ExternTypes.Add("System.Text.StringBuilder, mscorlib"); patch.InternStrings.Add("reference value");
        patch.ExternMethods.Add(new() { DeclaringTypeId = 5, Name = ".ctor" });
        var text = new[] { fixture.Op("Ldstr"), fixture.Op("Stloc") }; var builder = new[] { fixture.Op("Newobj"), fixture.Op("Stloc") };
        var code = new List<Instr> { fixture.Op("StackSpace", (1 << 16) | 1) }; code.AddRange(stringLast ? builder : text); code.AddRange(stringLast ? text : builder);
        code.AddRange([fixture.Op("Ldloc"), fixture.Op("Ret", 1)]); fixture.Method(patch, [.. code]); fixture.Fix(patch, 0, "ObjectValue");
        var (report, owner, _) = fixture.Decompile(patch); Assert.Empty(report.Problems);
        var result = owner.GetMethod("ObjectValue__m0")!.Invoke(null, null);
        if (stringLast) Assert.Equal("reference value", result); else Assert.Empty(Assert.IsType<System.Text.StringBuilder>(result).ToString());
    }

    private static GenericInstanceType PairType(ModuleDefinition module, TypeReference element)
    {
        var pair = new GenericInstanceType(new TypeReference("System", "Tuple`2", module, module.TypeSystem.CoreLibrary));
        pair.GenericArguments.Add(element); pair.GenericArguments.Add(module.TypeSystem.Int32); return pair;
    }

    private static GenericInstanceType ListType(ModuleDefinition module, TypeReference element)
    {
        var list = new GenericInstanceType(new TypeReference("System.Collections.Generic", "List`1", module, module.TypeSystem.CoreLibrary));
        list.GenericArguments.Add(element); return list;
    }

    private static DecompileReport Report(DecompilerFixture fixture, PatchFile patch)
    {
        PatchValidation.RequireValid(patch, fixture.Map);
        using var source = new DummyDllSource(fixture.DirectoryPath);
        return PatchDecompiler.Run(patch, fixture.Map, source);
    }

    private static void RewriteTarget(DecompilerFixture fixture, Action<ModuleDefinition> edit)
    {
        var path = Path.Combine(fixture.DirectoryPath, "SyntheticTarget.dll");
        using var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { InMemory = true });
        edit(assembly.MainModule); assembly.Write(path);
    }

    private static string WriteLibrary(DecompilerFixture fixture, Action<ModuleDefinition> edit, bool load = false)
    {
        var name = "TranspilerContracts" + Guid.NewGuid().ToString("N"); var path = Path.Combine(fixture.DirectoryPath, name + ".dll");
        using (var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name, new Version(1, 0)), name, ModuleKind.Dll))
        { edit(assembly.MainModule); assembly.Write(path); }
        if (load) AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        return name;
    }
}
