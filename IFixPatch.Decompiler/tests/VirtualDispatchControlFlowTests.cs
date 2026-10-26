using IFixPatch.Core;

namespace IFixPatch.Decompiler.Tests;

public sealed class VirtualDispatchControlFlowTests
{
    [Theory]
    [InlineData(1, false, 11)]
    [InlineData(0, false, 22)]
    [InlineData(1, true, 0)]
    [InlineData(0, true, 1)]
    public void SharedVirtualBodyPreservesLocalsSwitchesAndBranchTargetsForEachReceiver(int selectFirst, bool exerciseCases, int expected)
    {
        using var f = new DecompilerFixture();
        var p = f.VirtualPatch(sharedImplementation: true);
        if (exerciseCases)
        {
            p.Methods[1].Code[4] = f.Op("Ldc_I4", 0);
            p.Methods[2].Code[4] = f.Op("Ldc_I4", 1);
        }
        p.Methods[3].Code = [f.Op("StackSpace", (1 << 16) | 1), f.Op("Ldarg"), f.Op("Ldfld", -1), f.Op("Stloc"),
            f.Op("Ldloc"), f.Op("Switch", 2), new Instr { Code = 3, Operand = 5 }, f.Op("Br", 5),
            f.Op("Ldloc"), f.Op("Br", 4), f.Op("Ldloc"), f.Op("Br", 2), f.Op("Ldloc"), f.Op("Ret", 1)];
        var (report, owner, _) = f.Decompile(p);
        Assert.Empty(report.Problems);
        Assert.Equal(expected, owner.GetMethod("Value__m0")!.Invoke(null, [selectFirst]));
        var function = Assert.IsType<Func<int>>(owner.GetMethod("Pointer__m5")!.Invoke(null, [selectFirst]));
        Assert.Equal(expected, function());
    }

    [Theory]
    [InlineData(1, 11)]
    [InlineData(0, 22)]
    public void SharedVirtualBodyPreservesExceptionRegionsAndFinallyEffects(int selectFirst, int captured)
    {
        using var f = new DecompilerFixture();
        var p = f.VirtualPatch(sharedImplementation: true);
        p.Methods[3].Code = [f.Op("StackSpace", (1 << 16) | 2), f.Op("Ldarg"), f.Op("Ldfld", -1), f.Op("Stloc"),
            f.Op("Leave", 10), f.Op("Ldloc"), f.Op("Ldc_I4", 1), f.Op("Add"), f.Op("Stloc"), f.Op("Endfinally"), f.Op("Ldloc"), f.Op("Ret", 1)];
        p.Methods[3].Handlers.Add(new Eh { HandlerType = 2, CatchTypeId = -1, TryStart = 1, TryEnd = 5, HandlerStart = 5, HandlerEnd = 10 });
        var (report, owner, _) = f.Decompile(p);
        Assert.Empty(report.Problems);
        Assert.Equal(captured + 1, owner.GetMethod("Value__m0")!.Invoke(null, [selectFirst]));
        var function = Assert.IsType<Func<int>>(owner.GetMethod("Pointer__m5")!.Invoke(null, [selectFirst]));
        Assert.Equal(captured + 1, function());
    }
}
