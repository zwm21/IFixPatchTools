using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

internal static class CoreCompatibilityGuard
{
    [ModuleInitializer]
    internal static void Validate()
    {
        var instruction = typeof(IFix.Core.Instruction);
        var fields = instruction.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
        if (Environment.GetEnvironmentVariable("IFIX_CORE_DIAGNOSTICS") == "1")
        {
            Console.Error.WriteLine("IFix.Core runtime assembly: " + instruction.Assembly.Location);
            Console.Error.WriteLine("Instruction fields: " + string.Join(", ", fields.Select(f => f.FieldType.FullName + " " + f.Name)));
        }
        if (fields.All(field => field.Name != "Code") || fields.All(field => field.Name != "Operand"))
            throw new MissingFieldException(
                "The selected IFix.Core.dll does not expose Instruction.Code and Instruction.Operand at runtime: " +
                instruction.Assembly.Location);
    }
}
