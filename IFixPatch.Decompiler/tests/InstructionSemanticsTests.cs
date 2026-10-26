using IFixPatch.Core;
using System.Reflection;

namespace IFixPatch.Decompiler.Tests;

public sealed class InstructionSemanticsTests
{
    // Expected results are CIL/VM integer arithmetic, including signed versus unsigned comparisons.
    [Theory]
    [InlineData("Add", 7, 3, 10)]
    [InlineData("Sub", 7, 3, 4)]
    [InlineData("Mul", 7, 3, 21)]
    [InlineData("Div", -7, 3, -2)]
    [InlineData("Div_Un", -1, 2, 2147483647)]
    [InlineData("Rem", -7, 3, -1)]
    [InlineData("Rem_Un", -1, 2, 1)]
    [InlineData("And", 6, 3, 2)]
    [InlineData("Or", 6, 3, 7)]
    [InlineData("Xor", 6, 3, 5)]
    [InlineData("Shl", 3, 2, 12)]
    [InlineData("Shr", -8, 2, -2)]
    [InlineData("Shr_Un", -8, 2, 1073741822)]
    [InlineData("Add_Ovf", 7, 3, 10)]
    [InlineData("Add_Ovf_Un", 7, 3, 10)]
    [InlineData("Sub_Ovf", 7, 3, 4)]
    [InlineData("Sub_Ovf_Un", 7, 3, 4)]
    [InlineData("Mul_Ovf", 7, 3, 21)]
    [InlineData("Mul_Ovf_Un", 7, 3, 21)]
    [InlineData("Ceq", 7, 7, 1)]
    [InlineData("Ceq", 7, 3, 0)]
    [InlineData("Cgt", -1, 1, 0)]
    [InlineData("Cgt_Un", -1, 1, 1)]
    [InlineData("Clt", -1, 1, 1)]
    [InlineData("Clt_Un", -1, 1, 0)]
    public void BinaryInstructionsExecuteWithVmIntegerSemantics(string opcode, int a, int b, int expected)
    {
        using var f = new DecompilerFixture();
        var p = f.Patch();
        f.Method(p, f.Op("StackSpace", 2), f.Op("Ldarg"), f.Op("Ldarg", 1), f.Op(opcode), f.Op("Ret", 1));
        f.Fix(p, 0, "Binary", 1, 1);
        var (report, owner, _) = f.Decompile(p);
        Assert.Empty(report.Problems);
        Assert.Equal(expected, owner.GetMethod("Binary__m0")!.Invoke(null, [a, b]));
    }

    [Theory]
    [InlineData("Add_Ovf", int.MaxValue, 1, typeof(OverflowException))]
    [InlineData("Add_Ovf_Un", -1, 1, typeof(OverflowException))]
    [InlineData("Sub_Ovf", int.MinValue, 1, typeof(OverflowException))]
    [InlineData("Sub_Ovf_Un", 0, 1, typeof(OverflowException))]
    [InlineData("Mul_Ovf", int.MaxValue, 2, typeof(OverflowException))]
    [InlineData("Mul_Ovf_Un", -1, 2, typeof(OverflowException))]
    [InlineData("Div", 5, 0, typeof(DivideByZeroException))]
    public void ArithmeticErrorsArePreserved(string opcode, int a, int b, Type exceptionType)
    {
        using var f = new DecompilerFixture();
        var p = f.Patch();
        f.Method(p, f.Op("StackSpace", 2), f.Op("Ldarg"), f.Op("Ldarg", 1), f.Op(opcode), f.Op("Ret", 1));
        f.Fix(p, 0, "Binary", 1, 1);
        var (report, owner, _) = f.Decompile(p);
        Assert.Empty(report.Problems);
        var error = Assert.Throws<TargetInvocationException>(() => owner.GetMethod("Binary__m0")!.Invoke(null, [a, b]));
        Assert.IsType(exceptionType, error.InnerException);
    }

    [Theory]
    [InlineData("Neg", 7, -7)]
    [InlineData("Not", 7, -8)]
    [InlineData("Conv_I1", 255, -1)]
    [InlineData("Conv_U1", -1, 255)]
    [InlineData("Conv_I2", 65535, -1)]
    [InlineData("Conv_U2", -1, 65535)]
    [InlineData("Conv_I4", 7, 7)]
    [InlineData("Conv_U4", -1, -1)]
    [InlineData("Conv_Ovf_I4", 7, 7)]
    [InlineData("Conv_Ovf_U4", 7, 7)]
    public void UnaryOperationsPreserveIntegerWidthAndSign(string opcode, int input, int expected)
    {
        using var f = new DecompilerFixture();
        var p = f.Patch();
        f.Method(p, f.Op("StackSpace", 1), f.Op("Ldarg"), f.Op(opcode), f.Op("Ret", 1));
        f.Fix(p, 0, "Value", 1);
        var (report, owner, _) = f.Decompile(p);
        Assert.Empty(report.Problems);
        Assert.Equal(expected, owner.GetMethod("Value__m0")!.Invoke(null, [input]));
    }

    [Theory]
    [InlineData("Beq", 5, 5, 1)]
    [InlineData("Beq", 4, 5, 0)]
    [InlineData("Bne_Un", 5, 5, 0)]
    [InlineData("Bne_Un", 4, 5, 1)]
    [InlineData("Bge", -1, 1, 0)]
    [InlineData("Bge", 1, 1, 1)]
    [InlineData("Bgt", 1, 1, 0)]
    [InlineData("Bgt", 2, 1, 1)]
    [InlineData("Ble", -1, 1, 1)]
    [InlineData("Ble", 2, 1, 0)]
    [InlineData("Blt", 1, 1, 0)]
    [InlineData("Blt", -1, 1, 1)]
    [InlineData("Bge_Un", -1, 1, 1)]
    [InlineData("Bge_Un", 0, 1, 0)]
    [InlineData("Bgt_Un", -1, 1, 1)]
    [InlineData("Bgt_Un", 1, 1, 0)]
    [InlineData("Ble_Un", -1, 1, 0)]
    [InlineData("Ble_Un", 1, 1, 1)]
    [InlineData("Blt_Un", -1, 1, 0)]
    [InlineData("Blt_Un", 0, 1, 1)]
    public void ConditionalBranchesUseRelativeInstructionTargets(string opcode, int a, int b, int expected)
    {
        using var f = new DecompilerFixture();
        var p = f.Patch();
        f.Method(p, f.Op("StackSpace", 2), f.Op("Ldarg"), f.Op("Ldarg", 1), f.Op(opcode, 3),
            f.Op("Ldc_I4"), f.Op("Ret", 1), f.Op("Ldc_I4", 1), f.Op("Ret", 1));
        f.Fix(p, 0, "Binary", 1, 1);
        var (report, owner, _) = f.Decompile(p);
        Assert.Empty(report.Problems);
        Assert.Equal(expected, owner.GetMethod("Binary__m0")!.Invoke(null, [a, b]));
    }

    [Theory]
    [InlineData(-1, 99)]
    [InlineData(0, 10)]
    [InlineData(1, 20)]
    [InlineData(2, 30)]
    [InlineData(3, 99)]
    public void SwitchDecodesPackedTargetPairsAndFallsThroughOutsideRange(int input, int expected)
    {
        using var f = new DecompilerFixture();
        var p = f.Patch();
        f.Method(p, f.Op("StackSpace", 1), f.Op("Ldarg"), f.Op("Switch", 3),
            new Instr { Code = 5, Operand = 7 }, new Instr { Code = 9 },
            f.Op("Ldc_I4", 99), f.Op("Ret", 1), f.Op("Ldc_I4", 10), f.Op("Ret", 1),
            f.Op("Ldc_I4", 20), f.Op("Ret", 1), f.Op("Ldc_I4", 30), f.Op("Ret", 1));
        f.Fix(p, 0, "Value", 1);
        var (report, owner, _) = f.Decompile(p);
        Assert.Empty(report.Problems);
        Assert.Equal(expected, owner.GetMethod("Value__m0")!.Invoke(null, [input]));
    }

    [Theory]
    [InlineData("Ldloca", "Stobj", "Ldobj")]
    [InlineData("Ldloca", "Stind_I4", "Ldind_I4")]
    [InlineData("Ldarga", "Stind_I4", "Ldind_I4")]
    public void AddressWritesAreReadBackFromTheSameStorage(string address, string store, string load)
    {
        using var f = new DecompilerFixture();
        var p = f.Patch();
        f.Method(p, f.Op("StackSpace", (1 << 16) | 2), f.Op("Ldc_I4", 3), f.Op("Stloc"),
            f.Op(address), f.Op("Ldc_I4", 41), f.Op(store, store == "Stobj" ? 1 : 0),
            f.Op(address), f.Op(load, load == "Ldobj" ? 1 : 0), f.Op("Ret", 1));
        f.Fix(p, 0, "Value", 1);
        var (report, owner, _) = f.Decompile(p);
        Assert.Empty(report.Problems);
        Assert.Equal(41, owner.GetMethod("Value__m0")!.Invoke(null, [7]));
    }

    [Fact]
    public void InitobjDeclaresAndZeroesALocalBeforeReadingIt()
    {
        using var f = new DecompilerFixture();
        var p = f.Patch();
        f.Method(p, f.Op("StackSpace", (1 << 16) | 1), f.Op("Ldloca"), f.Op("Initobj", 1), f.Op("Ldloc"), f.Op("Ret", 1));
        f.Fix(p, 0, "ReadA");
        var (report, owner, _) = f.Decompile(p);
        Assert.Empty(report.Problems);
        Assert.Equal(0, owner.GetMethod("ReadA__m0")!.Invoke(null, null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArrayStoreLoadAndLengthExecuteOnTheAllocatedArray(bool typed)
    {
        using var f = new DecompilerFixture();
        var p = f.Patch();
        f.Method(p, f.Op("StackSpace", (1 << 16) | 3), f.Op("Ldc_I4", 3), f.Op("Newarr", 1), f.Op("Stloc"),
            f.Op("Ldloc"), f.Op("Ldc_I4", 1), f.Op("Ldc_I4", 39), f.Op(typed ? "Stelem_Any" : "Stelem_I4", typed ? 1 : 0),
            f.Op("Ldloc"), f.Op("Ldc_I4", 1), f.Op(typed ? "Ldelem_Any" : "Ldelem_I4", typed ? 1 : 0),
            f.Op("Ldloc"), f.Op("Ldlen"), f.Op("Conv_I4"), f.Op("Add"), f.Op("Ret", 1));
        f.Fix(p, 0, "ReadA");
        var (report, owner, _) = f.Decompile(p);
        Assert.Empty(report.Problems);
        Assert.Equal(42, owner.GetMethod("ReadA__m0")!.Invoke(null, null));
    }

    [Theory]
    [InlineData("Call")]
    [InlineData("Callvirt")]
    public void InternalCallsPropagateArgumentAndReturnTypes(string opcode)
    {
        using var f = new DecompilerFixture();
        var p = f.Patch();
        f.Method(p, f.Op("StackSpace", (1 << 16) | 1), f.Op("Ldarg"), f.Op(opcode, (1 << 16) | 1), f.Op("Stloc"), f.Op("Ldloc"), f.Op("Ret", 1));
        f.Method(p, f.Op("StackSpace", 2), f.Op("Ldarg"), f.Op("Ldc_I4", 2), f.Op("Mul"), f.Op("Ret", 1));
        f.Fix(p, 0, "Value", 1);
        var (report, owner, _) = f.Decompile(p);
        Assert.Empty(report.Problems);
        Assert.Equal(42, owner.GetMethod("Value__m0")!.Invoke(null, [21]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BoxedIntegerCanBeUnboxedByValueOrAddress(bool address)
    {
        using var f = new DecompilerFixture();
        var p = f.Patch();
        var instructions = new List<Instr> { f.Op("StackSpace", 1), f.Op("Ldarg"), f.Op("Box", 1), f.Op(address ? "Unbox" : "Unbox_Any", 1) };
        if (address) instructions.Add(f.Op("Ldind_I4"));
        instructions.Add(f.Op("Ret", 1));
        f.Method(p, instructions.ToArray()); f.Fix(p, 0, "Value", 1);
        var (report, owner, _) = f.Decompile(p);
        Assert.Empty(report.Problems);
        Assert.Equal(42, owner.GetMethod("Value__m0")!.Invoke(null, [42]));
    }

    [Fact]
    public void FinallyRunsBeforeLeaveReachesItsAbsoluteTarget()
    {
        using var f = new DecompilerFixture();
        var p = f.Patch();
        f.Method(p, f.Op("StackSpace", (1 << 16) | 1), f.Op("Ldc_I4", 10), f.Op("Stloc"), f.Op("Leave", 7),
            f.Op("Ldc_I4", 42), f.Op("Stloc"), f.Op("Endfinally"), f.Op("Ldloc"), f.Op("Ret", 1));
        p.Methods[0].Handlers.Add(new Eh { HandlerType = 2, CatchTypeId = -1, TryStart = 1, TryEnd = 4, HandlerStart = 4, HandlerEnd = 7 });
        f.Fix(p, 0, "ReadA");
        var (report, owner, _) = f.Decompile(p);
        Assert.Empty(report.Problems);
        Assert.Equal(42, owner.GetMethod("ReadA__m0")!.Invoke(null, null));
    }

    [Fact]
    public void ExternalCallResolvesAnOverloadByItsParameterType()
    {
        using var f = new DecompilerFixture();
        var p = f.Patch();
        p.ExternTypes.Add("System.Math, mscorlib");
        p.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 5, Name = "Abs", Params = [new() { TypeId = 1 }] });
        f.Method(p, f.Op("StackSpace", (1 << 16) | 1), f.Op("Ldarg"), f.Op("CallExtern", 1 << 16), f.Op("Stloc"), f.Op("Ldloc"), f.Op("Ret", 1));
        f.Fix(p, 0, "Value", 1);
        var (report, owner, _) = f.Decompile(p);
        Assert.Empty(report.Problems);
        Assert.Equal(42, owner.GetMethod("Value__m0")!.Invoke(null, [-42]));
    }
    [Theory]
    [InlineData("Ldc_I8", "LongValue")]
    [InlineData("Ldc_R8", "DoubleValue")]
    [InlineData("Ldc_R4", "FloatValue")]
    public void WideAndFloatingConstantsPreserveTheirEncodedValue(string opcode, string methodName)
    {
        using var f = new DecompilerFixture();
        var p = f.Patch();
        long bits = opcode == "Ldc_I8" ? 0x12345678ABCDEF01 : BitConverter.DoubleToInt64Bits(-123.75);
        var code = new List<Instr> { f.Op("StackSpace", 1), f.Op(opcode, opcode == "Ldc_R4" ? BitConverter.SingleToInt32Bits(1.25f) : 0) };
        if (opcode != "Ldc_R4") code.Add(new Instr { Code = unchecked((int)bits), Operand = (int)(bits >> 32) });
        code.Add(f.Op("Ret", 1));
        f.Method(p, code.ToArray()); f.Fix(p, 0, methodName);
        var (report, owner, _) = f.Decompile(p);
        Assert.Empty(report.Problems);
        object expected = opcode == "Ldc_I8" ? (object)0x12345678ABCDEF01L : opcode == "Ldc_R8" ? (object)(-123.75) : 1.25f;
        Assert.Equal(expected, owner.GetMethod(methodName + "__m0")!.Invoke(null, null));
    }

    [Theory]
    [InlineData("Conv_I8", "LongValue", -1, -1L)]
    [InlineData("Conv_U8", "LongValue", -1, 4294967295L)]
    [InlineData("Conv_R4", "FloatValue", -7, -7f)]
    [InlineData("Conv_R8", "DoubleValue", -7, -7d)]
    [InlineData("Conv_R_Un", "DoubleValue", -1, 4294967295d)]
    public void NumericConversionsPreserveSignednessAndDestinationWidth(string opcode, string methodName, int input, object expected)
    {
        using var f = new DecompilerFixture();
        var p = f.Patch();
        f.Method(p, f.Op("StackSpace", (1 << 16) | 1), f.Op("Ldc_I4", input), f.Op(opcode), f.Op("Stloc"), f.Op("Ldloc"), f.Op("Ret", 1));
        f.Fix(p, 0, methodName);
        var (report, owner, _) = f.Decompile(p);
        Assert.Empty(report.Problems);
        Assert.Equal(expected, owner.GetMethod(methodName + "__m0")!.Invoke(null, null));
    }

    [Fact]
    public void ConstructedGenericCollectionCallsConcretizeNestedReturnAndParameterTypes()
    {
        using var f = new DecompilerFixture();
        var p = f.Patch();
        p.ExternTypes.Add("System.Collections.Generic.List`1[[System.Int32, mscorlib]], mscorlib");
        p.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 5, Name = ".ctor" });
        p.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 5, Name = "Add", Params = [new() { TypeId = 1 }] });
        p.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 5, Name = "ToArray" });
        f.Method(p, f.Op("StackSpace", (2 << 16) | 2), f.Op("Newobj"), f.Op("Stloc"), f.Op("Ldloc"),
            f.Op("Ldc_I4", 42), f.Op("CallExtern", (2 << 16) | 1), f.Op("Ldloc"), f.Op("CallExtern", (1 << 16) | 2),
            f.Op("Stloc", 1), f.Op("Ldloc", 1), f.Op("Ldc_I4"), f.Op("Ldelem_I4"), f.Op("Ret", 1));
        f.Fix(p, 0, "ReadA");
        var (report, owner, _) = f.Decompile(p);
        Assert.Empty(report.Problems);
        Assert.Equal(42, owner.GetMethod("ReadA__m0")!.Invoke(null, null));
    }

    [Fact]
    public void GenericMethodInstanceConcretizesItsOwnTypeParameter()
    {
        using var f = new DecompilerFixture();
        var p = f.Patch();
        p.ExternTypes.Add("System.Activator, mscorlib");
        p.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 5, Name = "CreateInstance", IsGenericInstance = true, GenericArgTypeIds = [1] });
        f.Method(p, f.Op("StackSpace", (1 << 16) | 1), f.Op("CallExtern"), f.Op("Stloc"), f.Op("Ldloc"), f.Op("Ret", 1));
        f.Fix(p, 0, "ReadA");
        var (report, owner, _) = f.Decompile(p);
        Assert.Empty(report.Problems);
        Assert.Equal(0, owner.GetMethod("ReadA__m0")!.Invoke(null, null));
    }

    [Fact]
    public void CatchHandlesTheThrownExceptionAndReturnsItsHandlerResult()
    {
        using var f = new DecompilerFixture();
        var p = f.Patch();
        p.ExternTypes.Add("System.InvalidOperationException, mscorlib");
        p.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 5, Name = ".ctor" });
        f.Method(p, f.Op("StackSpace", (1 << 16) | 1), f.Op("Newobj"), f.Op("Throw"), f.Op("Pop"),
            f.Op("Ldc_I4", 42), f.Op("Stloc"), f.Op("Leave", 7), f.Op("Ldloc"), f.Op("Ret", 1));
        p.Methods[0].Handlers.Add(new Eh { HandlerType = 0, CatchTypeId = 5, TryStart = 1, TryEnd = 3, HandlerStart = 3, HandlerEnd = 7 });
        f.Fix(p, 0, "ReadA");
        var (report, owner, _) = f.Decompile(p);
        Assert.Empty(report.Problems);
        Assert.Equal(42, owner.GetMethod("ReadA__m0")!.Invoke(null, null));
    }

    [Fact]
    public void LdtypeReturnsTheManagedTypeInsteadOfItsRuntimeHandle()
    {
        // InjectFix VM Ldtype pushes externTypes[id] as System.Type; Ldtoken is a distinct opcode.
        using var f = new DecompilerFixture();
        var p = f.Patch();
        f.Method(p, f.Op("StackSpace", 1), f.Op("Ldtype", 1), f.Op("Ret", 1));
        f.Fix(p, 0, "TypeValue");
        var (report, owner, _) = f.Decompile(p);
        Assert.Empty(report.Problems);
        Assert.Equal(typeof(int), owner.GetMethod("TypeValue__m0")!.Invoke(null, null));
    }

    [Theory]
    [InlineData("Conv_I")]
    [InlineData("Conv_U")]
    public void NativeIntegerRoundtripPreservesSmallPositiveValues(string opcode)
    {
        using var f = new DecompilerFixture();
        var p = f.Patch();
        f.Method(p, f.Op("StackSpace", (1 << 16) | 1), f.Op("Ldarg"), f.Op(opcode), f.Op("Stloc"), f.Op("Ldloc"), f.Op("Conv_I4"), f.Op("Ret", 1));
        f.Fix(p, 0, "Value", 1);
        var (report, owner, _) = f.Decompile(p);
        Assert.Empty(report.Problems);
        Assert.Equal(42, owner.GetMethod("Value__m0")!.Invoke(null, [42]));
    }

    [Fact]
    public void IsinstAndCastclassPreserveACompatibleStringReference()
    {
        using var f = new DecompilerFixture();
        var p = f.Patch();
        p.ExternTypes.Add("System.String, mscorlib"); p.InternStrings.Add("text");
        f.Method(p, f.Op("StackSpace", (1 << 16) | 1), f.Op("Ldstr"), f.Op("Isinst", 5), f.Op("Stloc"), f.Op("Ldloc"), f.Op("Castclass", 5), f.Op("Ret", 1));
        f.Fix(p, 0, "TextValue");
        var (report, owner, _) = f.Decompile(p);
        Assert.Empty(report.Problems);
        Assert.Equal("text", owner.GetMethod("TextValue__m0")!.Invoke(null, null));
    }

    [Fact]
    public void SizeofInt32IsFourBytes()
    {
        using var f = new DecompilerFixture();
        var p = f.Patch();
        f.Method(p, f.Op("StackSpace", 1), f.Op("Sizeof", 1), f.Op("Ret", 1)); f.Fix(p, 0, "ReadA");
        var (report, owner, _) = f.Decompile(p);
        Assert.Empty(report.Problems);
        Assert.Equal(sizeof(int), owner.GetMethod("ReadA__m0")!.Invoke(null, null));
    }

    [Fact]
    public void LdtokenRetainsARuntimeTypeHandleThroughLocalStorage()
    {
        // VM Ldtoken pushes Type.TypeHandle; unlike Ldtype, its value is a RuntimeTypeHandle struct.
        using var f = new DecompilerFixture();
        var p = f.Patch();
        f.Method(p, f.Op("StackSpace", (1 << 16) | 1), f.Op("Ldtoken", 1), f.Op("Stloc"), f.Op("Ldloc"), f.Op("Ret", 1));
        f.Fix(p, 0, "HandleValue");
        var (report, owner, _) = f.Decompile(p);
        Assert.Empty(report.Problems);
        Assert.Equal(typeof(int).TypeHandle, owner.GetMethod("HandleValue__m0")!.Invoke(null, null));
    }

}
