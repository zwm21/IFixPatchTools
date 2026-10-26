using IFixPatch.Core.IR;
namespace IFixPatch.Core.Tests;

public sealed class PatchDocumentTests
{
    [Fact]
    public void AnEmptyPatchIrRetainsAnEmptyMethodTable()
    {
        var document = new PatchDocument();
        var patch = document.ToPatchFile(out var map);
        Assert.Empty(patch.Methods);
        Assert.Empty(patch.FixList);
        Assert.True(map.IsEmpty);
        Assert.Empty(PatchValidation.Validate(patch));
    }

    [Fact]
    public void JsonNullIsNotAPatchDocument()
        => Assert.Throws<InvalidDataException>(() => PatchDocument.FromJson("null"));
}
