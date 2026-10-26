using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Runtime.Loader;

namespace IFixPatch.Core.Tests;

public sealed class StackBalanceTests
{
    [Fact]
    public void BodylessAndEmptyMethodsHaveNoReachableStackOperations()
    {
        using var module = ModuleDefinition.CreateModule("Synthetic", ModuleKind.Dll);
        var method = Method(module, module.TypeSystem.Void);
        Assert.Empty(StackBalanceCheck.Check(method));
        method.Body = null;
        Assert.Empty(StackBalanceCheck.Check(method));
    }

    [Fact]
    public void CallsUseParameterReceiverConstructorAndReturnSignatures()
    {
        using var module = ModuleDefinition.CreateModule("Synthetic", ModuleKind.Dll);
        var method = Method(module, module.TypeSystem.Int32);
        var owner = new TypeReference("Example", "Owner", module, module.TypeSystem.CoreLibrary);
        var ctor = new MethodReference(".ctor", module.TypeSystem.Void, owner) { HasThis = true };
        ctor.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
        var read = new MethodReference("Read", module.TypeSystem.Int32, owner) { HasThis = true };
        read.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
        var sink = new MethodReference("Sink", module.TypeSystem.Void, owner) { HasThis = false };
        sink.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
        Add(method, Instruction.Create(OpCodes.Ldc_I4_1), Instruction.Create(OpCodes.Newobj, ctor),
            Instruction.Create(OpCodes.Ldc_I4_2), Instruction.Create(OpCodes.Callvirt, read),
            Instruction.Create(OpCodes.Dup), Instruction.Create(OpCodes.Call, sink), Instruction.Create(OpCodes.Ret));

        Assert.Empty(StackBalanceCheck.Check(method));
    }

    [Fact]
    public void IndirectCallsConsumeTheFunctionPointerInAdditionToSignatureArguments()
    {
        using var module = ModuleDefinition.CreateModule("Synthetic", ModuleKind.Dll);
        var method = Method(module, module.TypeSystem.Int32);
        var owner = new TypeDefinition("Example", "Target", TypeAttributes.Public, module.TypeSystem.Object);
        module.Types.Add(owner);
        var target = new MethodDefinition("Identity", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32);
        target.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
        Add(target, Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ret));
        owner.Methods.Add(target);
        var site = new CallSite(module.TypeSystem.Int32);
        site.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
        Add(method, Instruction.Create(OpCodes.Ldc_I4_1), Instruction.Create(OpCodes.Ldftn, target),
            Instruction.Create(OpCodes.Calli, site), Instruction.Create(OpCodes.Ret));

        Assert.Empty(StackBalanceCheck.Check(method));
        owner.Methods.Add(method);
        using var stream = new MemoryStream();
        module.Write(stream);
        stream.Position = 0;
        var context = new AssemblyLoadContext("SyntheticCalli", isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(stream);
            Assert.Equal(1, assembly.GetType("Example.Target")!.GetMethod("Example")!.Invoke(null, null));
        }
        finally { context.Unload(); }
    }

    [Fact]
    public void IndirectCallsRequireAFunctionPointerBeyondTheirDeclaredArguments()
    {
        using var module = ModuleDefinition.CreateModule("Synthetic", ModuleKind.Dll);
        var method = Method(module, module.TypeSystem.Int32);
        var signature = new CallSite(module.TypeSystem.Int32);
        signature.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
        Add(method, Instruction.Create(OpCodes.Ldc_I4_1), Instruction.Create(OpCodes.Calli, signature), Instruction.Create(OpCodes.Ret));

        var issue = Assert.Single(StackBalanceCheck.Check(method));
        Assert.Equal("underflow", issue.Kind);
        Assert.Contains("calli", issue.Detail);
    }

    [Theory]
    [InlineData(Code.Call)]
    [InlineData(Code.Callvirt)]
    [InlineData(Code.Calli)]
    [InlineData(Code.Newobj)]
    public void CallInstructionsRejectSignaturesOfTheWrongOperandKind(Code code)
    {
        using var module = ModuleDefinition.CreateModule("Synthetic", ModuleKind.Dll);
        var method = Method(module, module.TypeSystem.Void);
        var reference = new MethodReference("Target", module.TypeSystem.Void, module.TypeSystem.Object);
        var call = Instruction.Create(OpCodes.Call, reference);
        call.OpCode = CallOpCode(code);
        call.Operand = code == Code.Calli ? reference : new CallSite(module.TypeSystem.Void);
        Add(method, call, Instruction.Create(OpCodes.Ret));

        var issue = Assert.Single(StackBalanceCheck.Check(method));
        Assert.Equal("invalid-operand", issue.Kind);
        Assert.Contains(call.OpCode.Name, issue.Detail);
    }

    [Theory]
    [InlineData(Code.Call, false)]
    [InlineData(Code.Call, true)]
    [InlineData(Code.Callvirt, false)]
    [InlineData(Code.Callvirt, true)]
    [InlineData(Code.Calli, false)]
    [InlineData(Code.Calli, true)]
    [InlineData(Code.Newobj, false)]
    [InlineData(Code.Newobj, true)]
    public void CallsWithoutSignaturesCannotBeReportedAsBalanced(Code code, bool unrelatedOperand)
    {
        using var module = ModuleDefinition.CreateModule("Synthetic", ModuleKind.Dll);
        var method = Method(module, module.TypeSystem.Void);
        var call = Instruction.Create(OpCodes.Call, new MethodReference("Target", module.TypeSystem.Void, module.TypeSystem.Object));
        call.OpCode = CallOpCode(code);
        // Cecil permits editing operands; malformed emitted IL must produce a diagnostic.
        call.Operand = unrelatedOperand ? "not a method signature" : null;
        Add(method, call, Instruction.Create(OpCodes.Ret));

        var issue = Assert.Single(StackBalanceCheck.Check(method));

        Assert.Equal("invalid-operand", issue.Kind);
        Assert.Contains(call.OpCode.Name, issue.Detail);
        Assert.Contains("signature", issue.Detail);
        Assert.Contains(issue.Context, line => line.StartsWith(">>") && line.Contains(call.OpCode.Name));
    }

    [Fact]
    public void SwitchPathsAndLoopMergeWithEqualDepth()
    {
        using var module = ModuleDefinition.CreateModule("Synthetic", ModuleKind.Dll);
        var method = Method(module, module.TypeSystem.Void);
        var loop = Instruction.Create(OpCodes.Ldc_I4_0);
        var done = Instruction.Create(OpCodes.Ret);
        Add(method, loop, Instruction.Create(OpCodes.Switch, new[] { done, done }),
            Instruction.Create(OpCodes.Ldc_I4_0), Instruction.Create(OpCodes.Brtrue, loop),
            Instruction.Create(OpCodes.Br, done), done);

        Assert.Empty(StackBalanceCheck.Check(method));
    }

    [Theory]
    [InlineData(ExceptionHandlerType.Catch)]
    [InlineData(ExceptionHandlerType.Filter)]
    [InlineData(ExceptionHandlerType.Finally)]
    [InlineData(ExceptionHandlerType.Fault)]
    public void ExceptionEntryDepthMatchesCilRules(ExceptionHandlerType kind)
    {
        using var module = ModuleDefinition.CreateModule("Synthetic", ModuleKind.Dll);
        var method = Method(module, module.TypeSystem.Void);
        var start = Instruction.Create(OpCodes.Nop);
        var handler = Instruction.Create(kind is ExceptionHandlerType.Catch or ExceptionHandlerType.Filter ? OpCodes.Pop : OpCodes.Nop);
        var end = Instruction.Create(OpCodes.Ret);
        Add(method, start, Instruction.Create(OpCodes.Leave, end));
        Instruction? filter = null;
        if (kind == ExceptionHandlerType.Filter)
        {
            filter = Instruction.Create(OpCodes.Pop);
            Add(method, filter, Instruction.Create(OpCodes.Ldc_I4_1), Instruction.Create(OpCodes.Endfilter));
        }
        Add(method, handler, kind is ExceptionHandlerType.Catch or ExceptionHandlerType.Filter
            ? Instruction.Create(OpCodes.Leave, end) : Instruction.Create(OpCodes.Endfinally), end);
        method.Body.ExceptionHandlers.Add(new ExceptionHandler(kind)
        {
            TryStart = start,
            TryEnd = filter ?? handler,
            FilterStart = filter,
            HandlerStart = handler,
            HandlerEnd = end,
            CatchType = kind == ExceptionHandlerType.Catch ? module.ImportReference(typeof(Exception)) : null
        });

        Assert.Empty(StackBalanceCheck.Check(method));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LeaveClearsEvaluationStack(bool shortForm)
    {
        using var module = ModuleDefinition.CreateModule("Synthetic", ModuleKind.Dll);
        var method = Method(module, module.TypeSystem.Void);
        var start = Instruction.Create(OpCodes.Ldc_I4_1);
        var finallyStart = Instruction.Create(OpCodes.Endfinally);
        var done = Instruction.Create(OpCodes.Ret);
        Add(method, start, Instruction.Create(shortForm ? OpCodes.Leave_S : OpCodes.Leave, done), finallyStart, done);
        method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally)
        {
            TryStart = start,
            TryEnd = finallyStart,
            HandlerStart = finallyStart,
            HandlerEnd = done
        });

        Assert.Empty(StackBalanceCheck.Check(method));
    }

    [Fact]
    public void ThrowEndsThePathAndUnreachableInvalidInstructionDoesNotCauseIssue()
    {
        using var module = ModuleDefinition.CreateModule("Synthetic", ModuleKind.Dll);
        var method = Method(module, module.TypeSystem.Void);
        Add(method, Instruction.Create(OpCodes.Ldnull), Instruction.Create(OpCodes.Throw), Instruction.Create(OpCodes.Pop));

        Assert.Empty(StackBalanceCheck.Check(method));
    }

    [Fact]
    public void ArrayAndIndirectStoreInstructionsConsumeTheirCilOperands()
    {
        using var module = ModuleDefinition.CreateModule("Synthetic", ModuleKind.Dll);
        var method = Method(module, module.TypeSystem.Int32);
        var local = new VariableDefinition(module.TypeSystem.Int32);
        method.Body.Variables.Add(local);
        Add(method, Instruction.Create(OpCodes.Ldloca, local), Instruction.Create(OpCodes.Ldc_I4_1), Instruction.Create(OpCodes.Stind_I4),
            Instruction.Create(OpCodes.Ldc_I4_1), Instruction.Create(OpCodes.Newarr, module.TypeSystem.Int32),
            Instruction.Create(OpCodes.Dup), Instruction.Create(OpCodes.Ldc_I4_0), Instruction.Create(OpCodes.Ldc_I4_2), Instruction.Create(OpCodes.Stelem_I4),
            Instruction.Create(OpCodes.Ldc_I4_0), Instruction.Create(OpCodes.Ldelem_I4),
            Instruction.Create(OpCodes.Ldloc, local), Instruction.Create(OpCodes.Add), Instruction.Create(OpCodes.Ret));

        Assert.Empty(StackBalanceCheck.Check(method));
    }

    [Fact]
    public void UnderflowDiagnosticIdentifiesOperationAndSurroundingInstructions()
    {
        using var module = ModuleDefinition.CreateModule("Synthetic", ModuleKind.Dll);
        var method = Method(module, module.TypeSystem.Void);
        Add(method, Instruction.Create(OpCodes.Nop), Instruction.Create(OpCodes.Add), Instruction.Create(OpCodes.Ret));

        var issue = Assert.Single(StackBalanceCheck.Check(method));

        Assert.Equal("underflow", issue.Kind);
        Assert.Equal("Example", issue.Method);
        Assert.Contains("add", issue.Detail);
        Assert.Contains(issue.Context, line => line.StartsWith(">>") && line.Contains("add"));
        Assert.Contains(issue.Context, line => line.Contains("nop"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReturnRejectsExtraValuesBeyondTheDeclaredResult(bool nonVoid)
    {
        using var module = ModuleDefinition.CreateModule("Synthetic", ModuleKind.Dll);
        var method = Method(module, nonVoid ? module.TypeSystem.Int32 : module.TypeSystem.Void);
        Add(method, Instruction.Create(OpCodes.Ldc_I4_1));
        if (nonVoid) Add(method, Instruction.Create(OpCodes.Ldc_I4_2));
        Add(method, Instruction.Create(OpCodes.Ret));

        Assert.Equal("ret-mismatch", Assert.Single(StackBalanceCheck.Check(method)).Kind);
    }

    [Fact]
    public void MergeWithUnequalIncomingDepthIsReported()
    {
        using var module = ModuleDefinition.CreateModule("Synthetic", ModuleKind.Dll);
        var method = Method(module, module.TypeSystem.Void);
        var merge = Instruction.Create(OpCodes.Ret);
        Add(method, Instruction.Create(OpCodes.Ldc_I4_0), Instruction.Create(OpCodes.Brtrue, merge),
            Instruction.Create(OpCodes.Ldc_I4_1), Instruction.Create(OpCodes.Br, merge), merge);

        Assert.Contains(StackBalanceCheck.Check(method), issue => issue.Kind == "inconsistent");
    }

    private static OpCode CallOpCode(Code code) => code switch
    {
        Code.Call => OpCodes.Call,
        Code.Callvirt => OpCodes.Callvirt,
        Code.Calli => OpCodes.Calli,
        Code.Newobj => OpCodes.Newobj,
        _ => throw new ArgumentOutOfRangeException(nameof(code)),
    };

    private static MethodDefinition Method(ModuleDefinition module, TypeReference returnType)
        => new("Example", MethodAttributes.Public | MethodAttributes.Static, returnType);

    private static void Add(MethodDefinition method, params Instruction[] instructions)
    {
        foreach (var instruction in instructions) method.Body.Instructions.Add(instruction);
    }
}
