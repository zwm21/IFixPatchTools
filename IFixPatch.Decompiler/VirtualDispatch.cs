using IFixPatch.Core;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IFixPatchDecompiler;

public sealed partial class ILTranspiler
{
    private readonly Dictionary<(int Slot, int Arguments, bool Pointer, int Receiver), MethodDefinition> _dispatchers = [];
    private readonly Dictionary<(MethodDefinition Method, TypeDefinition Receiver), MethodDefinition> _receiverMethods = [];

    public void CompleteVirtualDispatch()
    {
        foreach (var ((source, receiver), target) in _receiverMethods)
        {
            if (source.ReturnType.FullName == source.DeclaringType.FullName || source.Body.Variables.Any(variable => variable.VariableType.FullName == source.DeclaringType.FullName))
                throw new InvalidDataException("Shared VM methods returning or storing their concrete receiver type need additional type recovery.");
            target.Body = new MethodBody(target) { InitLocals = source.Body.InitLocals, MaxStackSize = source.Body.MaxStackSize };
            var variables = source.Body.Variables.ToDictionary(variable => variable, variable => new VariableDefinition(variable.VariableType));
            foreach (var variable in source.Body.Variables) target.Body.Variables.Add(variables[variable]);
            var instructions = source.Body.Instructions.ToDictionary(instruction => instruction, _ => Instruction.Create(OpCodes.Nop));
            foreach (var instruction in source.Body.Instructions)
            {
                var clone = instructions[instruction]; clone.OpCode = instruction.OpCode;
                clone.Operand = instruction.Operand switch
                {
                    Instruction branch => instructions[branch],
                    Instruction[] branches => branches.Select(branch => instructions[branch]).ToArray(),
                    VariableDefinition variable => variables[variable],
                    ParameterDefinition parameter => parameter.Index < 0 ? target.Body.ThisParameter : target.Parameters[parameter.Index],
                    FieldDefinition field when field.DeclaringType == source.DeclaringType && !field.IsStatic => ReceiverField(field),
                    MethodReference method when method.HasThis && method.DeclaringType.FullName == source.DeclaringType.FullName =>
                        throw new InvalidDataException("Shared VM method with concrete receiver calls needs additional receiver recovery."),
                    _ => instruction.Operand,
                };
                target.Body.Instructions.Add(clone);
            }
            foreach (var handler in source.Body.ExceptionHandlers)
                target.Body.ExceptionHandlers.Add(new ExceptionHandler(handler.HandlerType)
                {
                    CatchType = handler.CatchType,
                    TryStart = handler.TryStart is null ? null : instructions[handler.TryStart], TryEnd = handler.TryEnd is null ? null : instructions[handler.TryEnd],
                    HandlerStart = handler.HandlerStart is null ? null : instructions[handler.HandlerStart], HandlerEnd = handler.HandlerEnd is null ? null : instructions[handler.HandlerEnd],
                    FilterStart = handler.FilterStart is null ? null : instructions[handler.FilterStart],
                });
            FieldDefinition ReceiverField(FieldDefinition field)
            {
                var index = source.DeclaringType.Fields.IndexOf(field);
                if (index < 0 || index >= receiver.Fields.Count || receiver.Fields[index].FieldType.FullName != field.FieldType.FullName)
                    throw new InvalidDataException("Shared VM field layout has incompatible inferred types.");
                return receiver.Fields[index];
            }
        }
    }

    private MethodDefinition BindReceiver(MethodDefinition method, TypeDefinition receiver)
    {
        if (method.DeclaringType == receiver) return method;
        if (_receiverMethods.TryGetValue((method, receiver), out var cached)) return cached;
        var clone = new MethodDefinition(method.Name + "__receiver", MethodAttributes.Public | MethodAttributes.HideBySig, method.ReturnType);
        foreach (var parameter in method.Parameters) clone.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, parameter.ParameterType));
        receiver.Methods.Add(clone); _receiverMethods.Add((method, receiver), clone);
        return clone;
    }

    private MethodDefinition VirtualDispatcher(int slot, int arguments, bool pointer, int receiver)
    {
        if (_dispatchers.TryGetValue((slot, arguments, pointer, receiver), out var cached)) return cached;
        var candidates = Storeys.Values.Where(type => (receiver < 0 || type.Index == receiver) && slot < _p.AnonStoreys[type.Index].VTable.Count)
            .Select(type => (Type: type.Type, Id: _p.AnonStoreys[type.Index].VTable[slot]))
            .Where(candidate => InternalStubs.ContainsKey(candidate.Id))
            .Select(candidate => (candidate.Type, Method: InternalStubs[candidate.Id])).ToArray();
        if (candidates.Length == 0) throw new InvalidDataException($"VM vtable slot {slot} has no target.");
        var prototype = candidates[0].Method;
        var result = pointer ? _intptr : prototype.ReturnType;
        if (candidates.Any(candidate => candidate.Method.ReturnType.FullName != prototype.ReturnType.FullName))
            throw new InvalidDataException($"Incompatible return signatures in VM slot {slot}.");
        if (candidates.Any(candidate => !candidate.Method.HasThis || candidate.Method.Parameters.Count != prototype.Parameters.Count ||
            !candidate.Method.Parameters.Zip(prototype.Parameters).All(pair => pair.First.ParameterType.FullName == pair.Second.ParameterType.FullName &&
                pair.First.IsIn == pair.Second.IsIn && pair.First.IsOut == pair.Second.IsOut)))
            throw new InvalidDataException($"Incompatible argument signatures in VM slot {slot}.");
        var owner = new TypeDefinition("IFixPatch", $"__VirtualSlot{slot}_{arguments}_{(pointer ? "Pointer" : "Call")}_Receiver{receiver + 1}",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, _object);
        _module.Types.Add(owner);
        var method = new MethodDefinition("Dispatch", MethodAttributes.Public | MethodAttributes.Static, result);
        owner.Methods.Add(method);
        method.Parameters.Add(new ParameterDefinition("receiver", ParameterAttributes.None, _object));
        if (!pointer)
        {
            if (!prototype.HasThis || prototype.Parameters.Count != arguments - 1) throw new InvalidDataException("Unknown VM virtual-call signature.");
            foreach (var parameter in prototype.Parameters) method.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
        }
        var il = method.Body.GetILProcessor();
        var receiverPresent = Instruction.Create(OpCodes.Nop);
        il.Append(Instruction.Create(OpCodes.Ldarg_0)); il.Append(Instruction.Create(OpCodes.Brtrue, receiverPresent));
        il.Append(Instruction.Create(OpCodes.Newobj, _module.ImportReference(typeof(NullReferenceException).GetConstructor(Type.EmptyTypes)!)));
        il.Append(Instruction.Create(OpCodes.Throw)); il.Append(receiverPresent);
        foreach (var (type, implementation) in candidates)
        {
            var target = BindReceiver(implementation, type);
            var next = Instruction.Create(OpCodes.Nop);
            il.Append(Instruction.Create(OpCodes.Ldarg_0)); il.Append(Instruction.Create(OpCodes.Isinst, type));
            il.Append(Instruction.Create(OpCodes.Brfalse, next));
            if (pointer) il.Append(Instruction.Create(OpCodes.Ldftn, target));
            else
            {
                il.Append(Instruction.Create(OpCodes.Ldarg_0)); il.Append(Instruction.Create(OpCodes.Castclass, type));
                foreach (var parameter in method.Parameters.Skip(1)) il.Append(Instruction.Create(OpCodes.Ldarg, parameter));
                il.Append(Instruction.Create(OpCodes.Call, target));
            }
            il.Append(Instruction.Create(OpCodes.Ret)); il.Append(next);
        }
        il.Append(Instruction.Create(OpCodes.Ldstr, $"No receiver implementation for VM vtable slot {slot}."));
        il.Append(Instruction.Create(OpCodes.Newobj, _module.ImportReference(typeof(InvalidOperationException).GetConstructor([typeof(string)])!)));
        il.Append(Instruction.Create(OpCodes.Throw));
        _dispatchers.Add((slot, arguments, pointer, receiver), method);
        return method;
    }
}
