namespace IFixPatch.Core.Tests;

internal static class PatchTestData
{
    private static readonly string[] Names =
    [
        "StackSpace", "Ldc_I4", "Ldc_I8", "Ldc_R8", "Ret", "Pop", "Br", "Switch",
        "Ldstr", "Call", "CallExtern", "Ldfld", "Ldsfld", "Ldloc", "Newanon", "Callvirtvirt",
        "Leave", "Nop", "Ldarg", "Ldvirtftn2", "Endfinally",
    ];
    public static OpCodeMap Map() => OpCodeMap.FromPairs(Names.Select((name, index) => (100 + index * 7, name)));
    public static Instr Op(string name, int operand = 0) => new() { Code = 100 + Array.IndexOf(Names, name) * 7, Operand = operand };
    public static PatchFile Patch() => new()
    {
        Magic = 0x123456789ABCDEF0,
        FullyParsed = true,
        InterfaceBridgeType = "Example.Bridge, Synthetic",
        WrapperManagerType = "Example.Manager, Synthetic",
        IdMapSuffix = ", Synthetic",
        ExternTypes = ["Example.Calculator, Synthetic", "System.Int32, mscorlib"],
        Methods = [new() { Id = 0, Code = [Op("StackSpace", 1), Op("Ldc_I4", 42), Op("Ret", 1)] }],
        FixList = [new() { MethodId = 0, Method = new() { DeclaringTypeId = 0, Name = "Value" } }],
    };
}
