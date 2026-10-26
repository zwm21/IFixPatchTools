namespace IFixPatch.Core;

/// <summary>Checks side-table references and executable instruction boundaries without executing a patch.</summary>
public static class PatchValidation
{
    public static List<string> Validate(PatchFile patch, OpCodeMap? map = null)
    {
        var errors = new List<string>();
        void Index(int index, int count, string label)
        {
            if (index < 0 || index >= count) errors.Add($"{label}: index {index} outside [0, {count})");
        }
        void Method(int index, string label, bool optional = false, bool nativeInitializer = false)
        {
            if (optional && (index == -1 || nativeInitializer && index == -2)) return;
            Index(index, patch.Methods.Count, label);
            if (index >= 0 && index < patch.Methods.Count && patch.Methods[index].IsEmpty)
                errors.Add($"{label}: method#{index} has no body");
        }
        void Signature(ExternMethodSig signature, string label)
        {
            Index(signature.DeclaringTypeId, patch.ExternTypes.Count, label + " declaring type");
            if (string.IsNullOrWhiteSpace(signature.Name)) errors.Add(label + ": empty method name");
            if (!signature.IsGenericInstance && signature.GenericArgTypeIds.Count != 0)
                errors.Add(label + ": generic arguments on a non-generic signature");
            foreach (var type in signature.GenericArgTypeIds) Index(type, patch.ExternTypes.Count, label + " generic argument");
            foreach (var parameter in signature.Params)
                if (parameter.IsGeneric)
                {
                    if (!signature.IsGenericInstance || string.IsNullOrWhiteSpace(parameter.GenericName))
                        errors.Add(label + ": invalid generic parameter");
                }
                else Index(parameter.TypeId, patch.ExternTypes.Count, label + " parameter type");
        }
        if (!patch.FullyParsed) errors.Add("Patch was not fully parsed: " + patch.ParseNote);
        if (patch.TrailingBytes != 0) errors.Add($"Patch has {patch.TrailingBytes} trailing bytes");
        if (patch.Methods.Count > 65536 || patch.ExternMethods.Count > 65536)
            errors.Add("Method table exceeds the 16-bit call index limit");
        for (var i = 0; i < patch.ExternTypes.Count; i++)
            if (string.IsNullOrWhiteSpace(patch.ExternTypes[i])) errors.Add($"externTypes[{i}] is empty");
        for (var i = 0; i < patch.ExternMethods.Count; i++) Signature(patch.ExternMethods[i], $"externMethods[{i}]");
        var bindings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fix in patch.FixList)
        {
            Signature(fix.Method, "fixList");
            Method(fix.MethodId, "fixList");
            var key = System.Text.Json.JsonSerializer.Serialize(new
            {
                fix.Method.DeclaringTypeId, fix.Method.Name, fix.Method.IsGenericInstance,
                Arguments = fix.Method.GenericArgTypeIds,
                Parameters = fix.Method.Params.Select(parameter => (parameter.IsGeneric, parameter.GenericName, parameter.TypeId)).ToArray(),
            }, new System.Text.Json.JsonSerializerOptions { IncludeFields = true });
            if (!bindings.Add(key)) errors.Add("Duplicate fixList binding: " + fix.Method.Name);
        }
        foreach (var field in patch.Fields)
        {
            Index(field.DeclaringTypeId, patch.ExternTypes.Count, "field declaring type");
            if (string.IsNullOrWhiteSpace(field.Name)) errors.Add("Empty field name");
            if (field.IsNew)
            {
                Index(field.FieldTypeId, patch.ExternTypes.Count, "new field type");
                Method(field.CctorId, "new field initializer", true, true);
            }
        }
        foreach (var field in patch.StaticFields)
        {
            Index(field.FieldTypeId, patch.ExternTypes.Count, "VM static field type");
            Method(field.CctorId, "VM static initializer", true, true);
        }
        foreach (var storey in patch.AnonStoreys)
        {
            Method(storey.CtorId, "anonymous constructor");
            if (storey.CtorParamNum < 0) errors.Add("Negative anonymous constructor argument count");
            foreach (var type in storey.FieldTypes)
                if (type > 0) Index(type - 1, patch.ExternTypes.Count, "anonymous field type");
                else if (type is not (0 or -2)) errors.Add($"Invalid anonymous field encoding {type}");
            foreach (var itf in storey.Interfaces)
            {
                Index(itf.TypeId, patch.ExternTypes.Count, "anonymous interface type");
                foreach (var id in itf.MethodIds) Method(id, "anonymous interface slot", true);
            }
            foreach (var id in storey.VTable) Method(id, "anonymous vtable slot", true);
        }
        if (patch.NewClasses.Any(string.IsNullOrWhiteSpace) || patch.NewClasses.Distinct().Count() != patch.NewClasses.Count)
            errors.Add("Empty or duplicate new class name");
        for (var id = 0; id < patch.Methods.Count; id++)
        {
            var method = patch.Methods[id];
            if (method.Id != id) errors.Add($"Method ids must be dense and ordered: slot {id} contains {method.Id}");
            foreach (var handler in method.Handlers)
            {
                if (handler.TryStart < 0 || handler.TryEnd <= handler.TryStart || handler.TryEnd > method.Code.Count ||
                    handler.HandlerStart < 0 || handler.HandlerStart >= method.Code.Count ||
                    handler.HandlerEnd != -1 && (handler.HandlerEnd <= handler.HandlerStart || handler.HandlerEnd > method.Code.Count))
                    errors.Add($"method#{id}: invalid exception region");
                if (handler.CatchTypeId != -1) Index(handler.CatchTypeId, patch.ExternTypes.Count, "catch type");
            }
            if (map is null || map.IsEmpty || method.IsEmpty) continue;
            var starts = new HashSet<int>();
            var decoded = new List<(int Position, string Name, int Operand)>();
            for (var at = 0; at < method.Code.Count;)
            {
                var instruction = method.Code[at];
                starts.Add(at);
                if (!map.TryName(instruction.Code, out var name))
                {
                    errors.Add($"method#{id}[{at}]: unknown opcode {instruction.Code}");
                    break;
                }
                decoded.Add((at, name, instruction.Operand));
                long length = name is "Ldc_I8" or "Ldc_R8" ? 2 : name == "Switch" ? 1L + ((long)instruction.Operand + 1) / 2 : 1;
                if (name == "Switch" && instruction.Operand < 0 || length < 1 || length > method.Code.Count - at)
                {
                    errors.Add($"method#{id}[{at}]: truncated or invalid {name} borrowed slots");
                    break;
                }
                at += (int)length;
            }
            if (decoded.Count != 0 && decoded[0].Name != "StackSpace") errors.Add($"method#{id}: missing initial StackSpace");
            void Target(long target, string label, bool end = false)
            {
                if (!(end && target == method.Code.Count) && (target < 0 || target > int.MaxValue || !starts.Contains((int)target)))
                    errors.Add($"method#{id} {label}: target {target} is not an instruction boundary");
            }
            void BranchTarget(long target, string label)
            {
                Target(target, label);
                if (target == 0) errors.Add($"method#{id} {label}: cannot execute the StackSpace prologue");
            }
            foreach (var (at, name, operand) in decoded)
            {
                if (name is "Br" or "Brtrue" or "Brfalse" or "Beq" or "Bge" or "Bgt" or "Ble" or "Blt" or "Bne_Un" or "Bge_Un" or "Bgt_Un" or "Ble_Un" or "Blt_Un") BranchTarget((long)at + operand, name);
                else if (name == "Leave") BranchTarget(operand, name);
                else if (name == "Switch" && operand >= 0 && 1L + ((long)operand + 1) / 2 <= method.Code.Count - at)
                    for (var n = 0; n < operand; n++)
                    {
                        var slot = method.Code[at + 1 + n / 2];
                        BranchTarget((long)at + (n % 2 == 0 ? slot.Code : slot.Operand), "Switch");
                    }
                else if (name is "Call" or "Callvirt") Method(operand & 0xffff, $"method#{id} {name}");
                else if (name == "Callvirtvirt" && !patch.AnonStoreys.Any(storey => (operand & 0xffff) < storey.VTable.Count))
                    errors.Add($"method#{id}: vtable slot {operand & 0xffff} does not exist");
                else if (name is "CallExtern" or "Newobj") Index(operand & 0xffff, patch.ExternMethods.Count, name);
                else if (name is "Ldftn" or "Ldvirtftn") Index(operand, patch.ExternMethods.Count, name);
                else if (name == "Ldvirtftn2" && !patch.AnonStoreys.Any(storey => (operand & 0xffff) < storey.VTable.Count)) errors.Add($"method#{id}: missing virtual function slot");
                else if (name == "Ldstr") Index(operand, patch.InternStrings.Count, name);
                else if (name == "Newanon") Index(operand, patch.AnonStoreys.Count, name);
                else if (name is "Ldfld" or "Stfld" or "Ldflda" or "Ldsfld" or "Stsfld" or "Ldsflda")
                {
                    if (operand >= 0) Index(operand, patch.Fields.Count, name);
                    else if (name is "Ldsfld" or "Stsfld" or "Ldsflda") Index(~operand, patch.StaticFields.Count, name);
                    else if (!patch.AnonStoreys.Any(storey => ~operand < storey.FieldTypes.Count)) errors.Add($"method#{id}: anonymous field {~operand} does not exist");
                }
                else if (TypeOperands.Contains(name))
                {
                    if (operand < 0 && name is "Castclass" or "Isinst" or "Box" or "Unbox" or "Unbox_Any") Index(~operand, patch.AnonStoreys.Count, name);
                    else Index(operand, patch.ExternTypes.Count, name);
                }
                if (name == "Ret" && operand is not (0 or 1)) errors.Add($"method#{id}: invalid return flag {operand}");
                if (name is "Ldarg" or "Ldarga" or "Starg" && operand < 0)
                    errors.Add($"method#{id}: argument index {operand} must be nonnegative");
                if ((name is "Ldloc" or "Stloc" or "Ldloca") && decoded.Count > 0 && decoded[0].Name == "StackSpace")
                    Index(operand, (int)((uint)decoded[0].Operand >> 16), name);
            }
            foreach (var eh in method.Handlers)
            {
                if (eh.HandlerType is not (0 or 1 or 2 or 4)) errors.Add($"method#{id}: unknown exception handler kind");
                Target(eh.TryStart, "try start"); Target(eh.TryEnd, "try end", true);
                Target(eh.HandlerStart, "handler start");
                if (eh.HandlerEnd != -1) Target(eh.HandlerEnd, "handler end", true);
                if (eh.TryEnd <= eh.TryStart || eh.HandlerEnd != -1 && eh.HandlerEnd <= eh.HandlerStart) errors.Add($"method#{id}: empty or inverted exception region");
                if (eh.CatchTypeId != -1) Index(eh.CatchTypeId, patch.ExternTypes.Count, "catch type");
            }
        }
        return errors;
    }

    private static readonly HashSet<string> TypeOperands = ["Ldtype", "Ldtoken", "Box", "Unbox", "Unbox_Any", "Castclass", "Isinst", "Newarr", "Initobj", "Ldobj", "Stobj", "Cpobj", "Constrained", "Sizeof", "Mkrefany", "Refanyval", "Ldelem_Any", "Stelem_Any", "Ldelema"];

    public static void RequireValid(PatchFile patch, OpCodeMap? map = null)
    {
        var errors = Validate(patch, map);
        if (errors.Count != 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
    }
}
