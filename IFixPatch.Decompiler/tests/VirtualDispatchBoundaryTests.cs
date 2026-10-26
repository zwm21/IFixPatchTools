using IFixPatch.Core;
using IFixPatchDecompiler;
using Mono.Cecil;
using System.Reflection;

namespace IFixPatch.Decompiler.Tests;

public sealed class VirtualDispatchBoundaryTests
{
    [Theory]
    [InlineData("return")]
    [InlineData("local")]
    public void SharedMethodsThatExposeAConcreteReceiverTypeFailExplicitly(string exposure)
    {
        using var f = new DecompilerFixture();
        var p = f.VirtualPatch(sharedImplementation: true);
        if (exposure == "return")
        {
            RewriteTarget(f, assembly => assembly.MainModule.GetType("Example.Calculator").Methods.Single(method => method.Name == "Value").ReturnType = assembly.MainModule.TypeSystem.Object);
            p.Methods[3].Code = [f.Op("StackSpace", 1), f.Op("Ldarg"), f.Op("Ret", 1)];
        }
        else
            p.Methods[3].Code = [f.Op("StackSpace", (1 << 16) | 1), f.Op("Ldarg"), f.Op("Stloc"), f.Op("Ldloc"), f.Op("Ldfld", -1), f.Op("Ret", 1)];
        DisablePointer(f, p);
        // A valid VM method may return/store its own receiver. The current translator explicitly
        // declines receiver-specific type recovery; producing a falsely executable clone is unsafe.
        var error = Assert.Throws<InvalidDataException>(() => f.Decompile(p));
        Assert.Contains("concrete receiver type", error.Message);
    }

    [Fact]
    public void SharedReceiverBodiesWithIncompatibleFieldStorageFailExplicitly()
    {
        using var f = new DecompilerFixture();
        var p = f.VirtualPatch(sharedImplementation: true);
        p.ExternTypes.Add("System.Int64, mscorlib");
        p.AnonStoreys[0].FieldTypes[0] = 2; // extern type 1 + 1: Int32
        p.AnonStoreys[1].FieldTypes[0] = 6; // extern type 5 + 1: Int64
        p.Methods[2].Code = [f.Op("StackSpace", 3), f.Op("Ldarg"), f.Op("CallExtern", 1 << 16),
            f.Op("Ldarg"), f.Op("Ldc_I8", 22), new Instr(), f.Op("Stfld", -1), f.Op("Ret")];
        // Merely read and discard the field: this is valid for either VM field type. A shared CIL
        // body nevertheless cannot reuse an Int32 FieldRef against the second receiver's Int64 field.
        p.Methods[3].Code = [f.Op("StackSpace", 1), f.Op("Ldarg"), f.Op("Ldfld", -1), f.Op("Pop"), f.Op("Ldc_I4", 42), f.Op("Ret", 1)];
        DisablePointer(f, p);
        var error = Assert.Throws<InvalidDataException>(() => f.Decompile(p));
        Assert.Contains("incompatible inferred types", error.Message);
    }

    [Fact]
    public void AVirtualSlotWithoutAnyImplementationIsRejectedBeforeEmittingACallableDispatcher()
    {
        using var f = new DecompilerFixture();
        var p = f.VirtualPatch();
        foreach (var receiver in p.AnonStoreys) receiver.VTable[DecompilerFixture.Slot] = -1;
        DisablePointer(f, p);
        var error = Assert.Throws<InvalidDataException>(() => f.Decompile(p));
        Assert.Contains("vtable slot", error.Message);
        Assert.Contains("no target", error.Message);
    }

    [Fact]
    public void FixesDeclaredInTheGlobalNamespacePreserveTheirCallableSignature()
    {
        using var f = new DecompilerFixture();
        RewriteTarget(f, assembly => assembly.MainModule.GetType("Example.Calculator").Namespace = "");
        var p = f.Patch(); p.ExternTypes[0] = "Calculator, SyntheticTarget";
        f.Method(p, f.Op("StackSpace", 2), f.Op("Ldarg"), f.Op("Ldc_I4", 2), f.Op("Mul"), f.Op("Ret", 1));
        f.Fix(p, 0, "Value", 1);
        PatchValidation.RequireValid(p, f.Map);
        using var source = new DummyDllSource(f.DirectoryPath);
        var report = PatchDecompiler.Run(p, f.Map, source);
        Assert.Empty(report.Problems); Assert.Empty(report.Balance);
        var assembly = Assembly.Load(report.AssemblyImage);
        var owner = assembly.GetType("Calculator__Patch", throwOnError: true)!;
        var value = owner.GetMethod("Value__m0")!;
        Assert.Equal(typeof(int), value.ReturnType);
        Assert.Equal(typeof(int), Assert.Single(value.GetParameters()).ParameterType);
        Assert.Equal(42, value.Invoke(null, [21]));
        Assert.Equal(-10, value.Invoke(null, [-5]));
    }

    [Fact]
    public void SharedVirtualCatchBodyCanExtendToTheEndOfTheMethod()
    {
        using var f = new DecompilerFixture();
        var p = f.VirtualPatch(sharedImplementation: true);
        DisablePointer(f, p);
        p.ExternTypes.Add("System.InvalidOperationException, mscorlib");
        p.ExternTypes.Add("System.String, mscorlib");
        p.ExternTypes.Add("System.Exception, mscorlib");
        p.InternStrings.Add("shared-catch");
        p.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 5, Name = ".ctor", Params = [new() { TypeId = 6 }] });
        p.Methods[3].Code = [f.Op("StackSpace", 2), f.Op("Ldarg"), f.Op("Ldfld", -1), f.Op("Ldc_I4", 11), f.Op("Beq", 3),
            f.Op("Ldnull"), f.Op("Throw"), f.Op("Leave", 8), f.Op("Ldc_I4", 42), f.Op("Ret", 1),
            f.Op("Pop"), f.Op("Ldstr"), f.Op("Newobj", (1 << 16) | 2), f.Op("Throw")];
        p.Methods[3].Handlers.Add(new Eh { HandlerType = 0, CatchTypeId = 7, TryStart = 1, TryEnd = 8, HandlerStart = 10, HandlerEnd = -1 });
        var (report, owner, _) = f.Decompile(p);
        Assert.Empty(report.Problems);
        var value = owner.GetMethod("Value__m0")!;
        Assert.Equal(42, value.Invoke(null, [1]));
        var error = Assert.Throws<TargetInvocationException>(() => value.Invoke(null, [0]));
        // The try throws NullReferenceException. This distinct exception and message prove that
        // the second receiver's cloned catch region ran, rather than merely propagating the throw.
        Assert.Equal("shared-catch", Assert.IsType<InvalidOperationException>(error.InnerException).Message);
    }

    [Fact]
    public void SharedReceiverMethodsWithRecursiveInstanceCallsRequireExplicitReceiverRecovery()
    {
        using var f = new DecompilerFixture(); var p = f.VirtualPatch(sharedImplementation: true); DisablePointer(f, p);
        // Each receiver sets its capture to zero and recursively calls itself once, then returns 42.
        // A clone must not call a method still declared on the other concrete receiver class.
        p.Methods[3].Code = [f.Op("StackSpace", 2), f.Op("Ldarg"), f.Op("Ldfld", -1), f.Op("Brfalse", 7),
            f.Op("Ldarg"), f.Op("Ldc_I4"), f.Op("Stfld", -1), f.Op("Ldarg"), f.Op("Callvirt", (1 << 16) | 3), f.Op("Ret", 1), f.Op("Ldc_I4", 42), f.Op("Ret", 1)];
        var error = Assert.Throws<InvalidDataException>(() => f.Decompile(p));
        Assert.Contains("concrete receiver calls", error.Message);
    }

    [Fact]
    public void AVirtualCallCannotOmitAnArgumentReadByItsImplementation()
    {
        using var f = new DecompilerFixture(); var p = f.VirtualPatch(sharedImplementation: true); DisablePointer(f, p);
        // The caller supplies only this, whereas the method reads both this and argument 1.
        p.Methods[3].Code = [f.Op("StackSpace", 2), f.Op("Ldarg"), f.Op("Ldfld", -1), f.Op("Ldarg", 1), f.Op("Add"), f.Op("Ret", 1)];
        var error = Assert.Throws<InvalidDataException>(() => f.Decompile(p));
        Assert.Contains("virtual-call signature", error.Message);
    }

    [Fact]
    public void VirtualCandidatesWithEqualAritiesButDifferentParameterTypesAreRejected()
    {
        using var f = new DecompilerFixture(); var p = f.VirtualPatch(); DisablePointer(f, p);
        p.ExternTypes.Add("System.Func`2[[System.Int32, mscorlib],[System.Int32, mscorlib]], mscorlib");
        p.ExternTypes.Add("System.Func`2[[System.String, mscorlib],[System.Int32, mscorlib]], mscorlib");
        p.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 5, Name = ".ctor", Params = [new() { TypeId = 2 }, new() { TypeId = 4 }] });
        p.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 6, Name = ".ctor", Params = [new() { TypeId = 2 }, new() { TypeId = 4 }] });
        // The two delegate Invoke contracts independently fix equal-arity target arguments
        // to Int32 and String. One unqualified virtual dispatcher cannot safely expose both.
        f.Method(p, f.Op("StackSpace", 2), f.Op("Newanon"), f.Op("Ldc_I4", 3), f.Op("Newobj", (2 << 16) | 2), f.Op("Pop"), f.Op("Ret"));
        f.Method(p, f.Op("StackSpace", 2), f.Op("Newanon", 1), f.Op("Ldc_I4", 4), f.Op("Newobj", (2 << 16) | 3), f.Op("Pop"), f.Op("Ret"));
        p.Methods[0].Code = [f.Op("StackSpace", 2), f.Op("Ldarg"), f.Op("Brfalse", 3), f.Op("Newanon"), f.Op("Br", 2), f.Op("Newanon", 1),
            f.Op("Ldc_I4", 42), f.Op("Callvirtvirt", (2 << 16) | DecompilerFixture.Slot), f.Op("Ret", 1)];
        p.Methods[3].Code = [f.Op("StackSpace", 1), f.Op("Ldarg", 1), f.Op("Pop"), f.Op("Ldc_I4", 11), f.Op("Ret", 1)];
        p.Methods[4].Code = [f.Op("StackSpace", 1), f.Op("Ldarg", 1), f.Op("Pop"), f.Op("Ldc_I4", 22), f.Op("Ret", 1)];
        var error = Assert.Throws<InvalidDataException>(() => f.Decompile(p));
        Assert.Contains("Incompatible argument signatures", error.Message);
    }

    [Fact]
    public void SharedVirtualTryRegionCanEndAtTheMethodBoundaryWithAnEarlierHandler()
    {
        using var f = new DecompilerFixture(); var p = f.VirtualPatch(sharedImplementation: true); DisablePointer(f, p);
        p.ExternTypes.Add("System.InvalidOperationException, mscorlib"); p.ExternTypes.Add("System.String, mscorlib"); p.ExternTypes.Add("System.Exception, mscorlib");
        p.InternStrings.Add("earlier-catch"); p.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 5, Name = ".ctor", Params = [new() { TypeId = 6 }] });
        // Entry skips the handler. The try occupies the tail and leaves backward to the return
        // outside the region; it never uses ret inside try/catch.
        p.Methods[3].Code = [f.Op("StackSpace", 2), f.Op("Br", 7), f.Op("Ldc_I4", 42), f.Op("Ret", 1),
            f.Op("Pop"), f.Op("Ldstr"), f.Op("Newobj", (1 << 16) | 2), f.Op("Throw"),
            f.Op("Ldarg"), f.Op("Ldfld", -1), f.Op("Ldc_I4", 11), f.Op("Beq", 3), f.Op("Ldnull"), f.Op("Throw"), f.Op("Leave", 2)];
        p.Methods[3].Handlers.Add(new Eh { HandlerType = 0, CatchTypeId = 7, TryStart = 8, TryEnd = 15, HandlerStart = 4, HandlerEnd = 8 });
        var (report, owner, _) = f.Decompile(p); Assert.Empty(report.Problems);
        var value = owner.GetMethod("Value__m0")!; Assert.Equal(42, value.Invoke(null, [1]));
        var error = Assert.Throws<TargetInvocationException>(() => value.Invoke(null, [0]));
        Assert.Equal("earlier-catch", Assert.IsType<InvalidOperationException>(error.InnerException).Message);
    }

    private static void DisablePointer(DecompilerFixture f, PatchFile patch)
    {
        patch.Methods[5].Code = [f.Op("StackSpace"), f.Op("Ret")];
        patch.FixList.RemoveAt(1);
    }

    private static void RewriteTarget(DecompilerFixture f, Action<AssemblyDefinition> change)
    {
        var path = Path.Combine(f.DirectoryPath, "SyntheticTarget.dll");
        using var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { InMemory = true });
        change(assembly); assembly.Write(path);
    }
}
