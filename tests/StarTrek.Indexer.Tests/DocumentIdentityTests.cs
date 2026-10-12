using System.Security.Cryptography;
using System.Text;
using StarTrek.Indexer.Chunking;

namespace StarTrek.Indexer.Tests;

public sealed class DocumentIdentityTests
{
    private const string TalosianDocumentId = "57d8ee2158cf932f6e9ebc65416679beaa81c443fbc54342d7afffe84337b8e3";

    [Fact]
    public void DocumentIdIsSha256OfNormalizedRelativePath()
    {
        var expected = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes("sourcefiles/races/талосианцы.txt")));

        Assert.Equal(expected, DocumentIdentity.ComputeDocumentId("SourceFiles/Races/Талосианцы.txt"));
        Assert.Equal(TalosianDocumentId, expected);
    }

    [Theory]
    [InlineData("SourceFiles/Races/Талосианцы.txt")]
    [InlineData("SourceFiles\\Races\\Талосианцы.txt")]
    [InlineData("SOURCEFILES/RACES/ТАЛОСИАНЦЫ.TXT")]
    public void DocumentIdDoesNotDependOnSlashesOrCase(string sourceFile)
    {
        Assert.Equal(TalosianDocumentId, DocumentIdentity.ComputeDocumentId(sourceFile));
    }

    [Fact]
    public void DocumentIdNormalizesUnicodeToNfc()
    {
        var composed = DocumentIdentity.ComputeDocumentId("SourceFiles/Planets/Мой мир.txt");
        var decomposed = DocumentIdentity.ComputeDocumentId("SourceFiles/Planets/Мои\u0306 мир.txt");

        Assert.Equal(composed, decomposed);
        Assert.Matches("^[0-9a-f]{64}$", composed);
    }

    [Fact]
    public void ContentHashIsStableForEquivalentTextAndChangesWithContent()
    {
        var unix = DocumentIdentity.ComputeContentHash("Талосианцы\nTalos IV\n");
        var windows = DocumentIdentity.ComputeContentHash("\uFEFFТалосианцы\r\nTalos IV\r\n");
        var changed = DocumentIdentity.ComputeContentHash("Талосианцы\nTalos V\n");
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("Талосианцы\nTalos IV\n")));

        Assert.Equal(expected, unix);
        Assert.Equal(unix, windows);
        Assert.NotEqual(unix, changed);
    }
}
