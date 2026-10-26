using System.Text;
using System.Text.RegularExpressions;

namespace IFixPatch.Core.IR;

public static partial class PatchTextAssembly
{
    public static string Disassemble(PatchFile p, OpCodeMap map)
    {
        var dis = new Disassembler(p, map);
        var sb = new StringBuilder();
        sb.AppendLine($".meta magic 0x{p.Magic:X16} bridge \"{Escape(p.InterfaceBridgeType)}\" wrapper \"{Escape(p.WrapperManagerType)}\" idmap \"{Escape(p.IdMapSuffix)}\"");
        if (!map.IsEmpty)
        {
            sb.AppendLine($".opcode_map {map.Count}");
            foreach (var kv in map.Enumerate().OrderBy(k => k.Value))
                sb.AppendLine($"  {kv.Value} = {kv.Key}");
        }
        else
        {
            sb.AppendLine($".opcode_map 0");
        }
        sb.AppendLine($".extern_types {p.ExternTypes.Count}");
        for (int i = 0; i < p.ExternTypes.Count; i++) sb.AppendLine($"  #{i} \"{Escape(p.ExternTypes[i])}\"");
        sb.AppendLine($".intern_strings {p.InternStrings.Count}");
        for (int i = 0; i < p.InternStrings.Count; i++) sb.AppendLine($"  #{i} \"{Disassembler.Escape(p.InternStrings[i])}\"");
        // 文本仅替换方法体，其余侧表由 assemble 的 --base 提供。
        foreach (var m in p.Methods.Where(m => !m.IsEmpty))
        {
            sb.AppendLine($".method #{m.Id} code {m.Code.Count} eh {m.Handlers.Count}");
            sb.Append(dis.Format(m));
        }
        return sb.ToString();
    }

    public static PatchFile Assemble(string text, PatchFile basePatch, OpCodeMap map)
    {
        var nameToCode = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!map.IsEmpty)
            foreach (var kv in map.Enumerate()) nameToCode[kv.Value] = kv.Key;
        var lines = text.Split('\n');
        var parsedMap = new Dictionary<string, int>(StringComparer.Ordinal);
        bool inOpcode = false;
        foreach (var raw in lines)
        {
            var t = raw.Trim();
            if (t.StartsWith(".opcode_map"))
            {
                inOpcode = true;
                continue;
            }
            if (inOpcode)
            {
                if (string.IsNullOrWhiteSpace(t) || t.StartsWith(".")) { inOpcode = false; if (t.StartsWith(".")) { /* fall through to handle this line as directive */ } else continue; }
                else
                {
                    var m = MyRegex().Match(t);
                    if (m.Success)
                    {
                        int val = int.Parse(m.Groups[1].Value);
                        string name = m.Groups[2].Value;
                        parsedMap[name] = val;
                        continue;
                    }
                    m = Regex.Match(t, @"^\s*(\w+)\s*=\s*(\d+)\s*$");
                    if (m.Success)
                    {
                        string name = m.Groups[1].Value;
                        int val = int.Parse(m.Groups[2].Value);
                        parsedMap[name] = val;
                        continue;
                    }
                    inOpcode = false;
                }
            }
        }
        if (parsedMap.Count > 0) nameToCode = parsedMap;

        var methodBlocks = new List<(int id, string block)>();
        int idx = text.IndexOf(".method");
        while (idx >= 0)
        {
            int next = text.IndexOf(".method", idx + 1);
            string block = next >= 0 ? text[idx..next] : text[idx..];
            var m = Regex.Match(block, @"\.method\s+#(\d+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var id))
                methodBlocks.Add((id, block));
            if (next < 0) break;
            idx = next;
        }

        var result = ClonePatch(basePatch); // copy base, replace methods

        foreach (var (id, block) in methodBlocks)
        {
            var parsed = ParseMethod(block, nameToCode, basePatch);
            while (result.Methods.Count <= id)
                result.Methods.Add(new IFixMethod { Id = result.Methods.Count });
            result.Methods[id].Code = parsed.Code;
            result.Methods[id].Handlers = parsed.Handlers;
        }
        return result;
    }

    private static PatchFile ClonePatch(PatchFile p)
    {
        var n = new PatchFile
        {
            Magic = p.Magic,
            InterfaceBridgeType = p.InterfaceBridgeType,
            WrapperManagerType = p.WrapperManagerType,
            IdMapSuffix = p.IdMapSuffix,
            FullyParsed = true,
        };
        n.ExternTypes.AddRange(p.ExternTypes);
        n.InternStrings.AddRange(p.InternStrings);
        n.NewClasses.AddRange(p.NewClasses);
        foreach (var em in p.ExternMethods) n.ExternMethods.Add(CloneSig(em));
        foreach (var f in p.Fields) n.Fields.Add(new FieldEntry { IsNew = f.IsNew, DeclaringTypeId = f.DeclaringTypeId, Name = f.Name, FieldTypeId = f.FieldTypeId, CctorId = f.CctorId });
        foreach (var sf in p.StaticFields) n.StaticFields.Add(new StaticFieldEntry { FieldTypeId = sf.FieldTypeId, CctorId = sf.CctorId });
        foreach (var a in p.AnonStoreys)
        {
            var na = new AnonStorey { FieldTypes = [.. a.FieldTypes], CtorId = a.CtorId, CtorParamNum = a.CtorParamNum, HasInterfaces = a.HasInterfaces, VTable = [.. a.VTable] };
            foreach (var it in a.Interfaces) na.Interfaces.Add(new AnonStorey.ItfSlots { TypeId = it.TypeId, MethodIds = [.. it.MethodIds] });
            n.AnonStoreys.Add(na);
        }
        foreach (var fx in p.FixList) n.FixList.Add(new FixEntry { Method = CloneSig(fx.Method), MethodId = fx.MethodId });
        foreach (var m in p.Methods)
        {
            var nm = new IFixMethod { Id = m.Id };
            nm.Code.AddRange(m.Code.Select(ins => new Instr { Code = ins.Code, Operand = ins.Operand }));
            nm.Handlers.AddRange(m.Handlers.Select(eh => new Eh { HandlerType = eh.HandlerType, CatchTypeId = eh.CatchTypeId, TryStart = eh.TryStart, TryEnd = eh.TryEnd, HandlerStart = eh.HandlerStart, HandlerEnd = eh.HandlerEnd }));
            n.Methods.Add(nm);
        }
        return n;
    }
    private static ExternMethodSig CloneSig(ExternMethodSig s)
    {
        var n = new ExternMethodSig { IsGenericInstance = s.IsGenericInstance, DeclaringTypeId = s.DeclaringTypeId, Name = s.Name, GenericArgTypeIds = [.. s.GenericArgTypeIds] };
        foreach (var pr in s.Params) n.Params.Add(new ExternMethodSig.Param { IsGeneric = pr.IsGeneric, GenericName = pr.GenericName, TypeId = pr.TypeId });
        return n;
    }

    private sealed class ParsedMethod
    {
        public List<Instr> Code = [];
        public List<Eh> Handlers = [];
    }

    private static ParsedMethod ParseMethod(string block, Dictionary<string, int> nameToCode, PatchFile basePatch)
    {
        var pm = new ParsedMethod();
        var lines = block.Split('\n');
        var ehList = new List<string>();
        var instrLines = new List<string>();
        foreach (var raw in lines)
        {
            var t = raw.Trim();
            if (t.StartsWith(".eh"))
                ehList.Add(t);
            else if (Regex.IsMatch(t, @"^L\d+:\s*$"))
            {
                instrLines.Add(raw); // keep for label handling
            }
            else if (Regex.IsMatch(t, @"^\s*\d+\s+\w+"))
            {
                instrLines.Add(raw);
            }
            else if (t.StartsWith(".method") || t.StartsWith(".meta") || t.StartsWith(".opcode") || t.StartsWith(".extern") || t.StartsWith(".intern") || string.IsNullOrWhiteSpace(t))
            {
                continue;
            }
            else if (t.Length == 0) continue;
            else
            {
                if (Regex.IsMatch(t, @"^\w+"))
                    instrLines.Add(raw);
            }
        }

        var labelToIndex = new Dictionary<string, int>();

        // 标签先指向逻辑指令，再映射到展开借用槽后的原始位置。
        var pendingLabels = new List<string>();
        var expandedInstructions = new List<(string mnemonic, string operandRaw, string annotation)>();

        foreach (var rawLine in instrLines)
        {
            var trimmed = rawLine.Trim();
            if (Regex.IsMatch(trimmed, @"^L(\d+):\s*$"))
            {
                var m = Regex.Match(trimmed, @"^L(\d+):");
                string lab = $"L{m.Groups[1].Value.PadLeft(4, '0')}";
                pendingLabels.Add(lab);
                continue;
            }

            string operandToken = "";
            string comment = "";
            string line2 = rawLine;
            int sc = line2.IndexOf(';');
            if (sc >= 0) { comment = line2[(sc + 1)..]; line2 = line2[..sc]; }
            var parts = line2.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            string mnemonic;
            if (parts[0].Length == 4 && int.TryParse(parts[0], out _) && parts.Length >= 2)
            {
                mnemonic = parts[1];
                if (parts.Length >= 3) operandToken = parts[2];
            }
            else
            {
                mnemonic = parts[0];
                if (parts.Length >= 2) operandToken = parts[1];
            }
            foreach (var lab in pendingLabels)
                labelToIndex[lab] = expandedInstructions.Count; // logical index, not raw yet
            pendingLabels.Clear();

            expandedInstructions.Add((mnemonic, operandToken, comment));
        }
        foreach (var lab in pendingLabels)
            labelToIndex[lab] = expandedInstructions.Count;

        // Ldc_I8/R8 和 Switch 会占用额外的 Instr 槽。
        var rawCode = new List<Instr>();
        var logicalToRaw = new Dictionary<int, int>();
        for (int li = 0; li < expandedInstructions.Count; li++)
        {
            logicalToRaw[li] = rawCode.Count;
            var (mn, opTok, comment) = expandedInstructions[li];
            int codeVal;
            if (mn.StartsWith("code#"))
            {
                int.TryParse(mn[5..], out codeVal);
            }
            else if (!nameToCode.TryGetValue(mn, out codeVal))
            {
                codeVal = 0;
            }
            int operand = 0;
            if (mn == "Ldc_I8" || mn == "Ldc_R8")
            {
                long bits = 0;
                var m = Regex.Match(comment, @"=\s*(-?\d+)");
                if (mn == "Ldc_R8")
                {
                    var md = Regex.Match(comment, @"=\s*([\-0-9\.eE\+]+)");
                    if (md.Success && double.TryParse(md.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
                        bits = BitConverter.DoubleToInt64Bits(d);
                    else if (int.TryParse(opTok, out var v)) bits = v;
                }
                else // Ldc_I8
                {
                    if (m.Success && long.TryParse(m.Groups[1].Value, out var lv)) bits = lv;
                    else if (int.TryParse(opTok, out var iv)) bits = iv;
                }
                rawCode.Add(new Instr { Code = codeVal, Operand = operand });
                int low = (int)(bits & 0xFFFFFFFF);
                int high = (int)(bits >> 32);
                rawCode.Add(new Instr { Code = low, Operand = high });
                continue;
            }
            else if (mn == "Switch")
            {
                if (!int.TryParse(opTok, out int n))
                {
                    var m = Regex.Match(comment, @"(\d+)\s*cases");
                    if (m.Success) int.TryParse(m.Groups[1].Value, out n);
                }
                rawCode.Add(new Instr { Code = codeVal, Operand = n });
                // 先为 Switch 的目标表预留槽位，最终偏移在标签解析后写入。
                int slots = (n + 1) / 2;
                for (int slot = 0; slot < slots; slot++)
                    rawCode.Add(new Instr());
                continue;
            }
            else if (IsBranch(mn))
            {
                bool isLabel = opTok.StartsWith("L");
                if (isLabel)
                {
                    operand = 0;
                }
                else if (int.TryParse(opTok, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var iv))
                {
                    operand = iv;
                }
                else if (!string.IsNullOrEmpty(opTok) && opTok.StartsWith("0x"))
                {
                    operand = Convert.ToInt32(opTok, 16);
                }
                rawCode.Add(new Instr { Code = codeVal, Operand = operand });
                continue;
            }
            else
            {
                if (!string.IsNullOrEmpty(opTok))
                {
                    if (opTok.StartsWith("L"))
                    {
                        operand = 0;
                    }
                    else if (int.TryParse(opTok, out var iv)) operand = iv;
                    else if (opTok.StartsWith("0x") && int.TryParse(opTok[2..], System.Globalization.NumberStyles.HexNumber, null, out var hv)) operand = hv;
                    else operand = 0;
                }
                else
                {
                    operand = 0;
                }
                rawCode.Add(new Instr { Code = codeVal, Operand = operand });
            }
        }

        // 所有槽位分配完成后，统一重算分支、Switch 和异常区域标签。
        var labelToRaw = new Dictionary<string, int>();
        foreach (var kv in labelToIndex)
            if (logicalToRaw.TryGetValue(kv.Value, out var rawIdx))
                labelToRaw[kv.Key] = rawIdx;
            else if (kv.Value == expandedInstructions.Count)
                labelToRaw[kv.Key] = rawCode.Count; // end

        for (int li = 0; li < expandedInstructions.Count; li++)
        {
            var (mn, opTok, comment) = expandedInstructions[li];
            int curRaw = logicalToRaw[li];
            if (mn == "Switch")
            {
                int n = rawCode[curRaw].Operand;
                var mt = Regex.Match(comment, @"(?:->|→)\s*\[(.*)\]");
                var targets = new List<string>();
                if (mt.Success)
                {
                    var inner = mt.Groups[1].Value;
                    foreach (var tok in inner.Split(',')) { var t2 = tok.Trim(); if (t2.Length > 0) targets.Add(t2); }
                }
                while (targets.Count < n) targets.Add($"L{curRaw:0000}"); // fallback
                int slots = (n + 1) / 2;
                for (int s = 0; s < slots; s++)
                {
                    int idx0 = s * 2;
                    int idx1 = idx0 + 1;
                    int rel0 = 0, rel1 = 0;
                    if (idx0 < targets.Count && labelToRaw.TryGetValue(targets[idx0], out var abs0))
                        rel0 = abs0 - curRaw;
                    if (idx1 < targets.Count && labelToRaw.TryGetValue(targets[idx1], out var abs1))
                        rel1 = abs1 - curRaw;
                    rawCode[curRaw + 1 + s] = new Instr { Code = rel0, Operand = rel1 };
                }
            }
            else if (IsBranch(mn) || mn == "Leave")
            {
                if (opTok.StartsWith("L") && labelToRaw.TryGetValue(opTok, out var abs))
                {
                    int rel = mn == "Leave" ? abs : abs - curRaw;
                    rawCode[curRaw] = new Instr { Code = rawCode[curRaw].Code, Operand = rel };
                }
                else
                {
                    var cm = Regex.Match(comment, @"(?:->|→)\s*L(\d+)");
                    if (cm.Success)
                    {
                        string lab = $"L{int.Parse(cm.Groups[1].Value):0000}";
                        if (labelToRaw.TryGetValue(lab, out var abs2))
                        {
                            int rel = mn == "Leave" ? abs2 : abs2 - curRaw;
                            rawCode[curRaw] = new Instr { Code = rawCode[curRaw].Code, Operand = rel };
                        }
                        else if (int.TryParse(cm.Groups[1].Value, out var absNum))
                        {
                            if (absNum >= 0 && absNum < rawCode.Count) { /* fallback */ }
                        }
                    }
                }
            }
            else if (mn == "Ldc_I8" || mn == "Ldc_R8")
            {
            }
        }

        pm.Code = rawCode;

        foreach (var ehLine in ehList)
        {
            var m = Regex.Match(ehLine, @"\.eh\s+(\w+)\s+try\[(?:L(\d+)|-1)\.\.(?:L(\d+)|-1)\)\s+handler\[(?:L(\d+)|-1)\.\.(?:L(\d+)|-1)\)(?:\s+catch\s+(.+))?");
            if (!m.Success)
            {
                m = Regex.Match(ehLine, @"\.eh\s+(\w+)\s+try\[([^\.]+)\.\.([^\]]+)\)\s+handler\[([^\.]+)\.\.([^\]]+)\)(?:\s+catch\s+(.+))?");
                if (!m.Success) continue;
            }
            string kind = m.Groups[1].Value;
            int tryS = ParseEhBound(m.Groups[2].Value, labelToRaw);
            int tryE = ParseEhBound(m.Groups[3].Value, labelToRaw);
            int handS = ParseEhBound(m.Groups[4].Value, labelToRaw);
            int handE = ParseEhBound(m.Groups[5].Value, labelToRaw);
            int handlerType = kind switch { "catch" => 0, "filter" => 1, "finally" => 2, "fault" => 4, _ => 0 };
            int catchId = -1;
            if (m.Groups.Count >= 7 && !string.IsNullOrWhiteSpace(m.Groups[6].Value))
            {
                var ct = m.Groups[6].Value.Trim();
                if (ct == "object") catchId = -1;
                else
                {
                    int found = -1;
                    for (int ti = 0; ti < basePatch.ExternTypes.Count; ti++)
                        if (PrettyMatch(basePatch.ExternTypes[ti], ct)) { found = ti; break; }
                    if (found >= 0) catchId = found;
                }
            }
            pm.Handlers.Add(new Eh { HandlerType = handlerType, CatchTypeId = catchId, TryStart = tryS, TryEnd = tryE, HandlerStart = handS, HandlerEnd = handE });
        }

        return pm;
    }

    private static int ParseEhBound(string token, Dictionary<string, int> labelToRaw)
    {
        token = token.Trim();
        if (token == "-1") return -1;
        var mm = Regex.Match(token, @"L(\d+)");
        if (mm.Success)
        {
            string lab = $"L{int.Parse(mm.Groups[1].Value):0000}";
            if (labelToRaw.TryGetValue(lab, out var v)) return v;
            return int.Parse(mm.Groups[1].Value);
        }
        if (int.TryParse(token, out var iv))
        {
            string lab = $"L{iv:0000}";
            if (labelToRaw.TryGetValue(lab, out var v2)) return v2;
            return iv;
        }
        return -1;
    }
    private static bool PrettyMatch(string aqn, string pretty)
    {
        string p = Pretty(aqn);
        return p == pretty || aqn == pretty;
    }
    private static string Pretty(string aqn)
    {
        int depth = 0;
        for (int i = 0; i < aqn.Length; i++) { char c = aqn[i]; if (c == '[') depth++; else if (c == ']') depth--; else if (c == ',' && depth == 0) return aqn[..i]; }
        return aqn;
    }

    private static bool IsBranch(string mn) => mn == "Br" || mn == "Brfalse" || mn == "Brtrue" || mn == "Beq" || mn == "Bge" || mn == "Bgt" || mn == "Ble" || mn == "Blt" || mn == "Bne_Un" || mn == "Bge_Un" || mn == "Bgt_Un" || mn == "Ble_Un" || mn == "Blt_Un";

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
    [GeneratedRegex(@"^\s*(\d+)\s*=\s*(\w+)\s*$")]
    private static partial Regex MyRegex();
}
