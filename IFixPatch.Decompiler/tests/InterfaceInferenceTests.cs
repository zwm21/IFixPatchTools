using IFixPatch.Core;
using IFixPatchDecompiler;
using Mono.Cecil;

namespace IFixPatch.Decompiler.Tests;

public sealed class InterfaceInferenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InterfaceSlotsKeepTheirMethodsLiveAndAllowAbsentImplementations(bool hole)
    {
        using var fixture = new DecompilerFixture();
        var patch = Patch(fixture, hole);
        using var source = new DummyDllSource(fixture.DirectoryPath);
        var report = PatchDecompiler.Run(patch, fixture.Map, source);
        Assert.DoesNotContain(0, report.OrphanMethods);
        Assert.DoesNotContain(2, report.OrphanMethods);
        Assert.Equal(hole, report.OrphanMethods.Contains(1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InterfaceSeedingPreservesKnownOriginalSignaturesAndSkipsMissingSlots(bool hole)
    {
        using var fixture = new DecompilerFixture();
        var patch = Patch(fixture, hole);
        using var source = new DummyDllSource(fixture.DirectoryPath);
        using var module = ModuleDefinition.CreateModule("Inference", new ModuleParameters { Kind = ModuleKind.Dll, AssemblyResolver = source.Resolver });
        var signatures = new InternalSignatures(patch, fixture.Map);
        var transpiler = new ILTranspiler(patch, fixture.Map, source, module) { SeedAnonStoreys = true, InternalReturnsValue = id => signatures.ReturnsValue[id] };
        transpiler.BuildStoreyBindings();
        // The caller's known original signature is authoritative; interface analysis must not
        // replace it with a guessed int parameter, even when that interface slot names method 1.
        TypeReference?[] known = [module.TypeSystem.Object, module.TypeSystem.String];
        var (returns, arguments) = transpiler.AnalyzeInternalTypes(signatures, id => id == 1 ? known : null);
        Assert.DoesNotContain(arguments[1], type => type?.FullName == "System.Int32");
        Assert.Equal("System.String", returns[1]!.FullName);
        // This is a precedence test for conflicting analysis evidence, not an assertion
        // that one CLR method implements both incompatible runtime signatures.
    }

    private static PatchFile Patch(DecompilerFixture fixture, bool hole)
    {
        var path = Path.Combine(fixture.DirectoryPath, "SyntheticTarget.dll");
        using (var target = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { InMemory = true }))
        {
            target.MainModule.Types.Add(new TypeDefinition("Example", "IEmpty", TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract));
            target.Write(path);
        }
        var patch = fixture.Patch(); patch.ExternTypes.AddRange(["Example.IReader, SyntheticTarget", "Example.IEmpty, SyntheticTarget"]);
        patch.ExternMethods.Add(new() { DeclaringTypeId = 2, Name = ".ctor" });
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldarg"), fixture.Op("CallExtern", 1 << 16), fixture.Op("Ret"));
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldarg"), fixture.Op("Pop"), fixture.Op("Ldarg", 1), fixture.Op("Ret", 1));
        fixture.Method(patch, fixture.Op("StackSpace", 1), fixture.Op("Ldarg"), fixture.Op("Pop"), fixture.Op("Ret"));
        patch.AnonStoreys.Add(new() { CtorId = 0, HasInterfaces = true, Interfaces = [new() { TypeId = 5, MethodIds = [hole ? -1 : 1, 2] }, new() { TypeId = 6 }] });
        PatchValidation.RequireValid(patch, fixture.Map);
        return patch;
    }
}
