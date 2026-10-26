using IFixPatch.Core;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Runtime.Loader;

namespace IFixPatch.Decompiler.Tests;

public sealed class NativeFieldTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    public void ExistingNativeFieldReadsWritesAndAddressesUseRealStorage(bool isStatic, bool address, bool markedNew)
    {
        using var f = new DecompilerFixture();
        var name = "RuntimeStorage" + Guid.NewGuid().ToString("N");
        var path = Path.Combine(f.DirectoryPath, name + ".dll");
        using (var asm = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name, new Version(1, 0)), name, ModuleKind.Dll))
        {
            var m = asm.MainModule;
            var type = new TypeDefinition("Example", "Storage", TypeAttributes.Public, m.TypeSystem.Object);
            m.Types.Add(type);
            var ctor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, m.TypeSystem.Void);
            var objectCtor = new MethodReference(".ctor", m.TypeSystem.Void, m.TypeSystem.Object) { HasThis = true };
            ctor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
            ctor.Body.Instructions.Add(Instruction.Create(OpCodes.Call, objectCtor));
            ctor.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            type.Methods.Add(ctor);
            type.Fields.Add(new FieldDefinition("Number", FieldAttributes.Public | (isStatic ? FieldAttributes.Static : 0), m.TypeSystem.Int32));
            asm.Write(path);
        }
        AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        var p = f.Patch();
        p.ExternTypes.Add("Example.Storage, " + name);
        p.Fields.Add(new FieldEntry { DeclaringTypeId = 5, Name = "Number", IsNew = markedNew, FieldTypeId = markedNew ? 1 : -1 });
        p.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 5, Name = ".ctor" });
        var code = new List<Instr> { f.Op("StackSpace", (1 << 16) | 2) };
        if (!isStatic) code.AddRange([f.Op("Newobj"), f.Op("Stloc"), f.Op("Ldloc")]);
        if (address) code.Add(f.Op(isStatic ? "Ldsflda" : "Ldflda"));
        code.Add(f.Op("Ldc_I4", 42));
        code.Add(f.Op(address ? "Stind_I4" : isStatic ? "Stsfld" : "Stfld"));
        if (!isStatic) code.Add(f.Op("Ldloc"));
        code.Add(f.Op(isStatic ? address ? "Ldsflda" : "Ldsfld" : address ? "Ldflda" : "Ldfld"));
        if (address) code.Add(f.Op("Ldind_I4"));
        code.Add(f.Op("Ret", 1));
        f.Method(p, code.ToArray()); f.Fix(p, 0, "ReadA");
        var (report, owner, _) = f.Decompile(p);
        Assert.Empty(report.Problems);
        Assert.Equal(42, owner.GetMethod("ReadA__m0")!.Invoke(null, null));
    }
}
