using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace IFixPatch.Core.IR;

/// <summary>Editable method bodies. Metadata and side tables remain owned by the base patch.</summary>
public static class PatchTextAssembly
{
    public static string Disassemble(PatchFile patch, OpCodeMap map)
    {
        var text = new StringBuilder();
        text.AppendLine($".meta magic 0x{patch.Magic:X16} bridge \"{Escape(patch.InterfaceBridgeType)}\" wrapper \"{Escape(patch.WrapperManagerType)}\" idmap \"{Escape(patch.IdMapSuffix)}\"");
        text.AppendLine($".opcode_map {map.Count}");
        foreach (var item in map.Enumerate().OrderBy(item => item.Value)) text.AppendLine($"  {item.Value} = {item.Key}");
        text.AppendLine($".extern_types {patch.ExternTypes.Count}");
        for (var i = 0; i < patch.ExternTypes.Count; i++) text.AppendLine($"  #{i} \"{Escape(patch.ExternTypes[i])}\"");
        text.AppendLine($".intern_strings {patch.InternStrings.Count}");
        for (var i = 0; i < patch.InternStrings.Count; i++) text.AppendLine($"  #{i} \"{Escape(patch.InternStrings[i])}\"");
        var disassembler = new Disassembler(patch, map);
        foreach (var method in patch.Methods.Where(method => !method.IsEmpty))
        {
            text.AppendLine($".method #{method.Id} code {method.Code.Count} eh {method.Handlers.Count}");
            text.Append(disassembler.Format(method));
        }
        return text.ToString();
    }

    public static PatchFile Assemble(string text, PatchFile basePatch, OpCodeMap map) => Assemble(text, basePatch, map, out _);

    public static PatchFile Assemble(string text, PatchFile basePatch, OpCodeMap map, out OpCodeMap effectiveMap)
    {
        if (!basePatch.FullyParsed || basePatch.TrailingBytes != 0) throw new InvalidDataException("The base patch must be fully parsed with no trailing bytes.");
        var lines = text.Split('\n').Select(line => line.Trim()).Where(line => line.Length != 0 && !line.StartsWith(';')).ToArray();
        foreach (var line in lines.Where(line => line.StartsWith('.')))
            if (!new[] { ".meta ", ".opcode_map ", ".extern_types ", ".intern_strings ", ".method ", ".eh " }.Any(line.StartsWith))
                throw new InvalidDataException("Unknown directive: " + line);
        foreach (var prefix in new[] { ".meta ", ".opcode_map ", ".extern_types ", ".intern_strings " })
            if (lines.Count(line => line.StartsWith(prefix, StringComparison.Ordinal)) > 1) throw new InvalidDataException("Duplicate section: " + prefix);
        var pairs = map.Enumerate().ToDictionary(item => item.Value, item => item.Key, StringComparer.Ordinal);
        var mapHeader = Array.FindIndex(lines, line => line.StartsWith(".opcode_map ", StringComparison.Ordinal));
        if (mapHeader >= 0)
        {
            pairs.Clear();
            var count = Integer(lines[mapHeader][12..]);
            if (count < 0 || count > lines.Length - mapHeader - 1) throw new InvalidDataException("Invalid opcode map size.");
            for (var i = 0; i < count; i++)
            {
                var match = Regex.Match(lines[mapHeader + i + 1], @"^(?:(\d+)\s*=\s*(\w+)|(\w+)\s*=\s*(\d+))$");
                if (!match.Success) throw new InvalidDataException("Malformed opcode map entry.");
                var name = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
                var code = Integer(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[4].Value);
                if (!pairs.TryAdd(name, code)) throw new InvalidDataException("Duplicate opcode name: " + name);
            }
        }
        effectiveMap = OpCodeMap.FromPairs(pairs.Select(item => (item.Value, item.Key)));
        if (!map.IsEmpty && effectiveMap.Enumerate().Any(item =>
                map.TryName(item.Key, out var name) && name != item.Value ||
                map.Enumerate().Any(original => original.Value == item.Value && original.Key != item.Key)))
            throw new InvalidDataException("Text opcode map conflicts with the supplied target metadata.");
        var validation = effectiveMap.Validate();
        if (validation.Unknown.Count != 0 || validation.ValueConflicts.Count != 0 || validation.NameConflicts.Count != 0)
            throw new InvalidDataException("Invalid opcode map.");
        var expected = Disassemble(basePatch, effectiveMap).Split('\n').Select(line => line.Trim()).Where(line => line.Length != 0).ToArray();
        // These printed sections are informational; reject edits rather than silently discarding them.
        foreach (var prefix in new[] { ".meta ", ".extern_types ", ".intern_strings " })
        {
            string[] Section(string[] source)
            {
                var start = Array.FindIndex(source, line => line.StartsWith(prefix, StringComparison.Ordinal));
                if (start < 0) return [];
                var end = start + 1;
                while (end < source.Length && !source[end].StartsWith('.')) end++;
                return source[start..end];
            }
            var section = Section(lines);
            if (section.Length != 0 && !section.SequenceEqual(Section(expected)))
                throw new InvalidDataException($"{prefix.Trim()} is read-only in IL text; edit IR to change side tables.");
        }
        for (var at = 0; at < lines.Length && !lines[at].StartsWith(".method ", StringComparison.Ordinal); at++)
        {
            if (lines[at].StartsWith(".meta ", StringComparison.Ordinal)) continue;
            var prefix = new[] { ".opcode_map ", ".extern_types ", ".intern_strings " }
                .FirstOrDefault(prefix => lines[at].StartsWith(prefix, StringComparison.Ordinal));
            if (prefix is null) throw new InvalidDataException("Unexpected text outside a method: " + lines[at]);
            var count = Integer(lines[at][prefix.Length..]);
            if (count < 0 || count > lines.Length - at - 1) throw new InvalidDataException("Invalid section size: " + prefix);
            at += count;
        }
        var document = PatchDocument.FromPatchFile(basePatch, map);
        var result = document.ToPatchFile(out _);
        var seen = new HashSet<int>();
        for (var at = 0; at < lines.Length; at++)
        {
            if (!lines[at].StartsWith(".method", StringComparison.Ordinal)) continue;
            var header = Regex.Match(lines[at], @"^\.method\s+#(\d+)(?:\s+code\s+\d+\s+eh\s+\d+)?$");
            if (!header.Success) throw new InvalidDataException("Malformed method header: " + lines[at]);
            var id = Integer(header.Groups[1].Value);
            if (id >= result.Methods.Count || !seen.Add(id)) throw new InvalidDataException($"Unknown or duplicate method#{id}.");
            var end = at + 1;
            while (end < lines.Length && !lines[end].StartsWith(".method", StringComparison.Ordinal)) end++;
            result.Methods[id] = ParseMethod(id, lines[(at + 1)..end], pairs, basePatch);
            at = end - 1;
        }
        PatchValidation.RequireValid(result, effectiveMap);
        return result;
    }

    private sealed record Logical(string Name, string Operand, string Comment);

    private static IFixMethod ParseMethod(int id, string[] lines, Dictionary<string, int> codes, PatchFile patch)
    {
        var logical = new List<Logical>();
        var labels = new Dictionary<string, int>(StringComparer.Ordinal);
        var handlers = new List<string>();
        foreach (var line in lines)
        {
            if (line.StartsWith(".eh ", StringComparison.Ordinal)) { handlers.Add(line); continue; }
            var label = Regex.Match(line, @"^(L\d+):$");
            if (label.Success)
            {
                if (!labels.TryAdd(label.Groups[1].Value, logical.Count)) throw new InvalidDataException("Duplicate label: " + line);
                continue;
            }
            var match = Regex.Match(line, @"^(?:\d+\s+)?(code#-?\d+|\w+)(?:\s+([^\s;]+))?\s*(?:;\s*(.*))?$");
            if (!match.Success) throw new InvalidDataException("Malformed instruction: " + line);
            logical.Add(new Logical(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value));
        }
        var raw = new List<Instr>();
        var positions = new List<int>();
        foreach (var instruction in logical)
        {
            positions.Add(raw.Count);
            if (!codes.TryGetValue(instruction.Name, out var code))
            {
                if (!instruction.Name.StartsWith("code#", StringComparison.Ordinal) || !int.TryParse(instruction.Name[5..], out code))
                    throw new InvalidDataException("Unknown instruction: " + instruction.Name);
            }
            var operand = instruction.Operand.StartsWith('L') ? 0 : instruction.Operand.Length == 0 ? 0 : Integer(instruction.Operand);
            raw.Add(new Instr { Code = code, Operand = operand });
            if (instruction.Name is "Ldc_I8" or "Ldc_R8")
            {
                var encoded = Regex.Match(instruction.Comment, @"bits64=0x([0-9a-fA-F]{16})\b");
                long bits;
                if (encoded.Success) bits = unchecked((long)ulong.Parse(encoded.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                else
                {
                    var value = Regex.Match(instruction.Comment, @"^=\s*(\S+)");
                    if (!value.Success) throw new InvalidDataException(instruction.Name + " requires bits64 or a value annotation.");
                    bits = instruction.Name == "Ldc_I8" ? long.Parse(value.Groups[1].Value, CultureInfo.InvariantCulture)
                        : BitConverter.DoubleToInt64Bits(double.Parse(value.Groups[1].Value, CultureInfo.InvariantCulture));
                }
                raw.Add(new Instr { Code = (int)bits, Operand = (int)(bits >> 32) });
            }
            else if (instruction.Name == "Switch")
            {
                if (operand < 0 || operand > 2_000_000) throw new InvalidDataException("Invalid Switch count.");
                for (var slot = 0; slot < (operand + 1) / 2; slot++) raw.Add(new Instr());
            }
        }
        positions.Add(raw.Count);
        int Bound(string value, bool end = false)
        {
            if (value.StartsWith('L'))
            {
                if (!labels.TryGetValue(value, out var index)) throw new InvalidDataException("Undefined label: " + value);
                return positions[index];
            }
            if (end && value == "-1") return -1;
            return Integer(value);
        }
        for (var i = 0; i < logical.Count; i++)
        {
            var instruction = logical[i]; var position = positions[i];
            if (Branches.Contains(instruction.Name) || instruction.Name == "Leave")
            {
                var annotated = Regex.Match(instruction.Comment, @"(?:->|→)\s*(L\d+)");
                var target = instruction.Operand.StartsWith('L') ? instruction.Operand : annotated.Success ? annotated.Groups[1].Value : null;
                if (target is not null)
                {
                    var absolute = Bound(target);
                    raw[position] = new Instr { Code = raw[position].Code, Operand = instruction.Name == "Leave" ? absolute : absolute - position };
                }
            }
            else if (instruction.Name == "Switch")
            {
                var match = Regex.Match(instruction.Comment, @"(?:->|→)\s*\[([^\]]*)\]");
                var targets = match.Success ? match.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : [];
                var count = raw[position].Operand;
                if (targets.Length != count) throw new InvalidDataException("Switch target count does not match its operand.");
                for (var n = 0; n < count; n++)
                {
                    var index = position + 1 + n / 2; var slot = raw[index];
                    if (n % 2 == 0) slot.Code = Bound(targets[n]) - position; else slot.Operand = Bound(targets[n]) - position;
                    raw[index] = slot;
                }
                var padding = Regex.Match(instruction.Comment, @"padding=0x([0-9a-fA-F]{8})\b");
                if (count % 2 != 0 && padding.Success)
                {
                    var index = position + (count + 1) / 2; var slot = raw[index];
                    slot.Operand = unchecked((int)uint.Parse(padding.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)); raw[index] = slot;
                }
            }
        }
        var method = new IFixMethod { Id = id, Code = raw };
        foreach (var line in handlers)
        {
            var match = Regex.Match(line, @"^\.eh\s+(catch|filter|finally|fault)\s+try\[([^.]*)\.\.([^)]*)\)\s+handler\[([^.]*)\.\.([^)]*)\)(?:\s+catch\s+(.+))?$");
            if (!match.Success) throw new InvalidDataException("Malformed exception region: " + line);
            var catchType = -1;
            if (match.Groups[6].Success && match.Groups[6].Value != "object")
            {
                var type = match.Groups[6].Value;
                if (type.StartsWith("type#", StringComparison.Ordinal)) catchType = Integer(type[5..]);
                else
                {
                    var matches = patch.ExternTypes.Select((aqn, index) => (aqn, index)).Where(item => item.aqn == type || AqnName(item.aqn) == type).ToArray();
                    if (matches.Length != 1) throw new InvalidDataException("Unknown or ambiguous catch type: " + type);
                    catchType = matches[0].index;
                }
            }
            method.Handlers.Add(new Eh
            {
                HandlerType = match.Groups[1].Value switch { "catch" => 0, "filter" => 1, "finally" => 2, "fault" => 4, _ => throw new InvalidDataException() },
                CatchTypeId = catchType, TryStart = Bound(match.Groups[2].Value), TryEnd = Bound(match.Groups[3].Value, true),
                HandlerStart = Bound(match.Groups[4].Value), HandlerEnd = Bound(match.Groups[5].Value, true),
            });
        }
        return method;
    }

    private static int Integer(string value) => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? unchecked((int)uint.Parse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)) : int.Parse(value, CultureInfo.InvariantCulture);
    private static string AqnName(string value) { var depth = 0; for (var i = 0; i < value.Length; i++) { if (value[i] == '[') depth++; if (value[i] == ']') depth--; if (value[i] == ',' && depth == 0) return value[..i]; } return value; }
    private static string Escape(string value) => Disassembler.Escape(value);
    private static readonly HashSet<string> Branches = ["Br", "Brtrue", "Brfalse", "Beq", "Bge", "Bgt", "Ble", "Blt", "Bne_Un", "Bge_Un", "Bgt_Un", "Ble_Un", "Blt_Un"];
}
