using IFixPatch.Core;
using IFixPatchDecompiler;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Reflection;
using CecilMethodAttributes = Mono.Cecil.MethodAttributes;
using CecilTypeAttributes = Mono.Cecil.TypeAttributes;

namespace IFixPatch.Decompiler.Tests;

internal sealed class DecompilerFixture : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("ifix-decompiler-unit-").FullName;
    public string DirectoryPath => _directory;
    public OpCodeMap Map { get; }
    private readonly Dictionary<string, int> _codes;
    public const int Slot = 4;

    public DecompilerFixture()
    {
        string[] names = ["StackSpace", "Ldarg", "Brfalse", "Newanon", "Br", "Callvirtvirt", "Ret", "CallExtern",
            "Ldc_I4", "Ldvirtftn2", "Newobj", "Ldfld", "Stfld", "Ldsfld", "Stsfld", "Add", "Pop", "Ldnull",
            "Sub", "Mul", "Div", "Div_Un", "Rem", "Rem_Un", "And", "Or", "Xor", "Shl", "Shr", "Shr_Un",
            "Add_Ovf", "Add_Ovf_Un", "Sub_Ovf", "Sub_Ovf_Un", "Mul_Ovf", "Mul_Ovf_Un", "Neg", "Not",
            "Ceq", "Cgt", "Cgt_Un", "Clt", "Clt_Un", "Beq", "Bge", "Bgt", "Ble", "Blt", "Bne_Un", "Bge_Un", "Bgt_Un", "Ble_Un", "Blt_Un", "Brtrue",
            "Ldloc", "Stloc", "Ldloca", "Ldarga", "Starg", "Dup", "Newarr", "Ldlen", "Ldelem_I4", "Stelem_I4", "Ldelem_Any", "Stelem_Any", "Ldelema",
            "Ldobj", "Stobj", "Initobj", "Ldind_I4", "Stind_I4", "Box", "Unbox", "Unbox_Any", "Castclass", "Isinst", "Sizeof",
            "Conv_I1", "Conv_U1", "Conv_I2", "Conv_U2", "Conv_I4", "Conv_U4", "Conv_I8", "Conv_U8", "Conv_R4", "Conv_R8", "Conv_R_Un", "Conv_I", "Conv_U", "Conv_Ovf_I4", "Conv_Ovf_U4",
            "Ldc_I8", "Ldc_R4", "Ldc_R8", "Ldstr", "Call", "Callvirt", "Switch", "Leave", "Endfinally", "Throw", "Nop", "Ldflda", "Ldsflda", "Ldtype", "Ldtoken"];
        _codes = names.Select((name, index) => (name, Code: 100 + index)).ToDictionary(item => item.name, item => item.Code);
        Map = OpCodeMap.FromPairs(_codes.Select(item => (item.Value, item.Key)));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "ReferenceAssemblies", "mscorlib.dll"), Path.Combine(_directory, "mscorlib.dll"));
        using var target = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("SyntheticTarget", new Version(1, 0)), "SyntheticTarget", ModuleKind.Dll);
        var module = target.MainModule;
        var calculator = new TypeDefinition("Example", "Calculator", CecilTypeAttributes.Public, module.TypeSystem.Object);
        module.Types.Add(calculator);
        var functionType = new GenericInstanceType(new TypeReference("System", "Func`1", module, module.TypeSystem.CoreLibrary));
        functionType.GenericArguments.Add(module.TypeSystem.Int32);
        void Declare(string name, TypeReference result, params TypeReference[] parameters)
        {
            var method = new MethodDefinition(name, CecilMethodAttributes.Public | CecilMethodAttributes.Static, result);
            foreach (var parameter in parameters) method.Parameters.Add(new ParameterDefinition(parameter));
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Throw));
            calculator.Methods.Add(method);
        }
        Declare("Value", module.TypeSystem.Int32, module.TypeSystem.Int32);
        Declare("Binary", module.TypeSystem.Int32, module.TypeSystem.Int32, module.TypeSystem.Int32);
        Declare("LongValue", module.TypeSystem.Int64);
        Declare("DoubleValue", module.TypeSystem.Double);
        Declare("FloatValue", module.TypeSystem.Single);
        Declare("ObjectValue", module.TypeSystem.Object);
        Declare("TextValue", module.TypeSystem.String);
        Declare("TypeValue", new TypeReference("System", "Type", module, module.TypeSystem.CoreLibrary));
        Declare("HandleValue", new TypeReference("System", "RuntimeTypeHandle", module, module.TypeSystem.CoreLibrary, true));
        Declare("Pointer", functionType, module.TypeSystem.Int32);
        Declare("ReadA", module.TypeSystem.Int32);
        Declare("ReadB", module.TypeSystem.Int32);
        Declare("ReadInitializations", module.TypeSystem.Int32);
        Declare("Bump", module.TypeSystem.Int32);
        Declare("GetField", module.TypeSystem.Int32, module.TypeSystem.Object);
        Declare("SetField", module.TypeSystem.Void, module.TypeSystem.Object, module.TypeSystem.Int32);
        Declare("GetStaticField", module.TypeSystem.Int32);
        Declare("SetStaticField", module.TypeSystem.Void, module.TypeSystem.Int32);
        var reader = new TypeDefinition("Example", "IReader",
            CecilTypeAttributes.Public | CecilTypeAttributes.Interface | CecilTypeAttributes.Abstract, null);
        module.Types.Add(reader);
        var read = new MethodDefinition("Read", CecilMethodAttributes.Public | CecilMethodAttributes.Abstract | CecilMethodAttributes.Virtual | CecilMethodAttributes.NewSlot, module.TypeSystem.Int32);
        read.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
        reader.Methods.Add(read);
        reader.Methods.Add(new MethodDefinition("Reset", CecilMethodAttributes.Public | CecilMethodAttributes.Abstract | CecilMethodAttributes.Virtual | CecilMethodAttributes.NewSlot, module.TypeSystem.Void));
        target.Write(Path.Combine(_directory, "SyntheticTarget.dll"));
    }

    public void SaveMap(string path) => File.WriteAllLines(path, _codes.Select(pair => $"{pair.Key}={pair.Value}"));

    public Instr Op(string name, int operand = 0) => new() { Code = _codes[name], Operand = operand };

    public PatchFile Patch() => new()
    {
        FullyParsed = true,
        ExternTypes = ["Example.Calculator, SyntheticTarget", "System.Int32, mscorlib", "System.Object, mscorlib",
            "System.Func`1[[System.Int32, mscorlib]], mscorlib", "System.IntPtr, mscorlib"],
    };

    public int Method(PatchFile patch, params Instr[] instructions)
    {
        var id = patch.Methods.Count;
        patch.Methods.Add(new IFixMethod { Id = id, Code = [.. instructions] });
        return id;
    }

    public void Fix(PatchFile patch, int methodId, string name, params int[] parameterTypes) => patch.FixList.Add(new FixEntry
    {
        MethodId = methodId,
        Method = new ExternMethodSig
        {
            DeclaringTypeId = 0,
            Name = name,
            Params = parameterTypes.Select(type => new ExternMethodSig.Param { TypeId = type }).ToList()
        },
    });

    public PatchFile VirtualPatch(bool sharedImplementation = false)
    {
        var patch = Patch();
        patch.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 2, Name = ".ctor" });
        Method(patch, Op("StackSpace", 1), Op("Ldarg"), Op("Brfalse", 3), Op("Newanon"), Op("Br", 2),
            Op("Newanon", 1), Op("Callvirtvirt", (1 << 16) | Slot), Op("Ret", 1));
        for (var id = 1; id <= 2; id++)
        {
            var constructor = Method(patch, Op("StackSpace", 1), Op("Ldarg"), Op("CallExtern", 1 << 16), Op("Ret"));
            if (sharedImplementation)
            {
                patch.Methods[constructor].Code[0] = Op("StackSpace", 2);
                patch.Methods[constructor].Code.RemoveAt(3);
                patch.Methods[constructor].Code.AddRange([Op("Ldarg"), Op("Ldc_I4", id * 11), Op("Stfld", -1), Op("Ret")]);
            }
        }
        Method(patch, Op("StackSpace", 1), Op("Ldc_I4", 11), Op("Ret", 1));
        Method(patch, Op("StackSpace", 1), Op("Ldc_I4", 22), Op("Ret", 1));
        patch.AnonStoreys.Add(new AnonStorey { CtorId = 1, VTable = [-1, -1, -1, -1, 3] });
        patch.AnonStoreys.Add(new AnonStorey { CtorId = 2, VTable = [-1, -1, -1, -1, sharedImplementation ? 3 : 4] });
        if (sharedImplementation)
        {
            foreach (var storey in patch.AnonStoreys) storey.FieldTypes.Add(0);
            patch.Methods[3].Code = [Op("StackSpace", 1), Op("Ldarg"), Op("Ldfld", -1), Op("Ret", 1)];
        }
        patch.ExternMethods.Add(new ExternMethodSig
        {
            DeclaringTypeId = 3,
            Name = ".ctor",
            Params = [new() { TypeId = 2 }, new() { TypeId = 4 }]
        });
        // The branch lands on the multi-instruction Ldvirtftn2 translation, including its prefix.
        Method(patch, Op("StackSpace", 2), Op("Ldarg"), Op("Brfalse", 3), Op("Newanon"), Op("Br", 2),
            Op("Newanon", 1), Op("Ldc_I4"), Op("Br", 1), Op("Ldvirtftn2", Slot), Op("Newobj", (2 << 16) | 1), Op("Ret", 1));
        Fix(patch, 0, "Value", 1);
        Fix(patch, 5, "Pointer", 1);
        return patch;
    }

    public (DecompileReport Report, Type Owner, Assembly Assembly) Decompile(PatchFile patch)
    {
        PatchValidation.RequireValid(patch, Map);
        using var source = new DummyDllSource(_directory);
        var report = PatchDecompiler.Run(patch, Map, source);
        Assert.Empty(report.Balance);
        Assert.NotEmpty(report.CSharp);
        var assembly = Assembly.Load(report.AssemblyImage);
        return (report, assembly.GetType("Example.Calculator__Patch", throwOnError: true)!, assembly);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
