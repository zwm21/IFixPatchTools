using IFixPatch.Core;
using IFixPatchDecompiler;
using Mono.Cecil;

namespace IFixPatch.Decompiler.Tests;

public sealed class TypeInferenceTests
{
    [Fact]
    public void OptInInterfaceSeedingUsesConstructedInterfaceParameterAndReturnContracts()
    {
        using var f = new DecompilerFixture();
        var p = f.Patch();
        p.ExternTypes.Add("System.Collections.Generic.IEqualityComparer`1[[System.Int32, mscorlib]], mscorlib");
        f.Method(p, f.Op("StackSpace", 1), f.Op("Ldarg"), f.Op("Pop"), f.Op("Ret"));
        f.Method(p, f.Op("StackSpace", 2), f.Op("Ldarg", 1), f.Op("Ldarg", 2), f.Op("Ceq"), f.Op("Ret", 1));
        f.Method(p, f.Op("StackSpace", 1), f.Op("Ldarg", 1), f.Op("Ret", 1));
        p.AnonStoreys.Add(new AnonStorey
        {
            CtorId = 0,
            HasInterfaces = true,
            Interfaces = [new() { TypeId = 5, MethodIds = [1, 2] }]
        });
        PatchValidation.RequireValid(p, f.Map);
        using var source = new DummyDllSource(f.DirectoryPath);
        using var module = ModuleDefinition.CreateModule("InterfaceInference", new ModuleParameters { Kind = ModuleKind.Dll, AssemblyResolver = source.Resolver });
        var signatures = new InternalSignatures(p, f.Map);
        var transpiler = new ILTranspiler(p, f.Map, source, module) { SeedAnonStoreys = true, InternalReturnsValue = id => signatures.ReturnsValue[id] };
        transpiler.BuildStoreyBindings();
        var (returns, arguments) = transpiler.AnalyzeInternalTypes(signatures, _ => null);
        Assert.Equal("System.Boolean", returns[1]!.FullName);
        Assert.Equal("System.Int32", arguments[1][1]!.FullName);
        Assert.Equal("System.Int32", arguments[1][2]!.FullName);
        Assert.Equal("System.Int32", returns[2]!.FullName);
        Assert.Equal("System.Int32", arguments[2][1]!.FullName);
    }

    [Fact]
    public void MultipleDerivedReferenceArgumentsRetainTheirSharedBaseContract()
    {
        using var f = new DecompilerFixture();
        var p = f.Patch();
        p.ExternTypes.AddRange(["System.InvalidOperationException, mscorlib", "System.ArgumentException, mscorlib", "System.Exception, mscorlib", "System.String, mscorlib"]);
        p.InternStrings.AddRange(["first", "second"]);
        p.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 5, Name = ".ctor", Params = [new() { TypeId = 8 }] });
        p.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 6, Name = ".ctor", Params = [new() { TypeId = 8 }] });
        p.ExternMethods.Add(new ExternMethodSig { DeclaringTypeId = 7, Name = "get_Message" });
        f.Method(p, f.Op("StackSpace", 1), f.Op("Ldstr"), f.Op("Newobj", 1 << 16), f.Op("Call", (1 << 16) | 1), f.Op("Pop"),
            f.Op("Ldstr", 1), f.Op("Newobj", (1 << 16) | 1), f.Op("Call", (1 << 16) | 1), f.Op("Ret", 1));
        f.Method(p, f.Op("StackSpace", 1), f.Op("Ldarg"), f.Op("CallExtern", (1 << 16) | 2), f.Op("Ret", 1));
        f.Fix(p, 0, "TextValue");
        var (report, owner, _) = f.Decompile(p);
        Assert.Empty(report.Problems);
        Assert.Equal("second", owner.GetMethod("TextValue__m0")!.Invoke(null, null));
        using var image = new MemoryStream(report.AssemblyImage);
        using var assembly = AssemblyDefinition.ReadAssembly(image);
        var fixedMethod = assembly.MainModule.Types.SelectMany(type => type.Methods).Single(method => method.Name == "TextValue__m0");
        var internalCall = fixedMethod.Body.Instructions.Select(instruction => instruction.Operand).OfType<MethodReference>().First(method => method.Name != ".ctor");
        // Both framework exception classes derive directly from SystemException.
        Assert.Equal(typeof(SystemException).FullName, Assert.Single(internalCall.Parameters).ParameterType.FullName);
    }
}
