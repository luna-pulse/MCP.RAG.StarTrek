using System.Text;
using StarTrek.Indexer.Parsing;

namespace StarTrek.Indexer.Tests;

public sealed class TextNormalizerTests
{
    [Fact]
    public void NormalizesBomLineEndingsAndUnicodeToNfc()
    {
        var decomposed = "и\u0306";
        var normalized = TextNormalizer.Normalize($"\uFEFF{decomposed}\r\nстрока\rконец");

        Assert.Equal("й\nстрока\nконец", normalized);
        Assert.True(normalized.IsNormalized(NormalizationForm.FormC));
        Assert.DoesNotContain('\r', normalized);
    }
}
