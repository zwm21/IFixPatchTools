using System.Globalization;
using System.Text;
using Mono.Cecil;

namespace IFixPatch.SourceBuilder.Core;

public sealed class AdapterCoreSourceGenerator
{
    public void Generate(
        string sourcePath,
        IEnumerable<string> referenceDirectories,
        string outputPath,
        PrepareReport report)
    {
        using var source = CecilModel.ReadAssembly(sourcePath, referenceDirectories);
        var code = source.MainModule.FindType("IFix.Core.Code");
        var instruction = source.MainModule.FindType("IFix.Core.Instruction");
        var exceptionHandlerType = source.MainModule.FindType("IFix.Core.ExceptionHandlerType");
        var magicField = instruction.Fields.Single(field => field.Name == "INSTRUCTION_FORMAT_MAGIC");
        if (!magicField.HasConstant || magicField.Constant is null)
            throw new InvalidDataException("IFix.Core.Instruction.INSTRUCTION_FORMAT_MAGIC has no constant value.");
        var magic = Convert.ToUInt64(magicField.Constant);
        var opcodeFields = code.Fields.Where(field => field.IsLiteral && field.HasConstant).ToList();
        var handlerFields = exceptionHandlerType.Fields.Where(field => field.IsLiteral && field.HasConstant).ToList();
        if (opcodeFields.Count != 181)
            throw new InvalidDataException($"Expected 181 IFix opcodes, found {opcodeFields.Count}.");
        if (opcodeFields.Select(field => Convert.ToInt32(field.Constant)).Distinct().Count() != opcodeFields.Count)
            throw new InvalidDataException("IFix opcode values are not unique.");

        var text = new StringBuilder();
        text.AppendLine("// Generated from the profile IFix.Core metadata. Do not edit.");
        text.AppendLine("namespace IFix.Core");
        text.AppendLine("{");
        WriteEnum(text, "Code", opcodeFields);
        text.AppendLine("    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]");
        text.AppendLine("    public struct Instruction");
        text.AppendLine("    {");
        text.Append("        public const ulong INSTRUCTION_FORMAT_MAGIC = ")
            .Append(magic.ToString(CultureInfo.InvariantCulture)).AppendLine("UL;");
        text.AppendLine("        public Code Code;");
        text.AppendLine("        public int Operand;");
        text.AppendLine("    }");
        WriteEnum(text, "ExceptionHandlerType", handlerFields);
        text.AppendLine("    public sealed class ExceptionHandler");
        text.AppendLine("    {");
        text.AppendLine("        public System.Type CatchType;");
        text.AppendLine("        public int CatchTypeId;");
        text.AppendLine("        public int HandlerEnd;");
        text.AppendLine("        public int HandlerStart;");
        text.AppendLine("        public ExceptionHandlerType HandlerType;");
        text.AppendLine("        public int TryEnd;");
        text.AppendLine("        public int TryStart;");
        text.AppendLine("    }");
        text.AppendLine("}");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        File.WriteAllText(outputPath, text.ToString(), new UTF8Encoding(false));
        report.AdapterOpcodeCount = opcodeFields.Count;
        report.AdapterMagic = magic;
    }

    private static void WriteEnum(StringBuilder text, string name, IReadOnlyList<FieldDefinition> fields)
    {
        text.Append("    public enum ").AppendLine(name);
        text.AppendLine("    {");
        foreach (var field in fields)
        {
            text.Append("        ").Append(field.Name).Append(" = ")
                .Append(Convert.ToInt32(field.Constant).ToString(CultureInfo.InvariantCulture)).AppendLine(",");
        }
        text.AppendLine("    }");
    }
}
