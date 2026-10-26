namespace IFixPatch.Core;

/// <summary>VM slots belong to the anonymous-type tables serialized inside the patch.</summary>
public static class PatchDispatch
{
    public static IEnumerable<int> Targets(PatchFile patch, int slot) => patch.AnonStoreys
        .Where(type => slot >= 0 && slot < type.VTable.Count)
        .Select(type => type.VTable[slot]).Where(id => id >= 0 && id < patch.Methods.Count).Distinct();
}
