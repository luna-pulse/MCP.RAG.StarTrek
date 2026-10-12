using StarTrek.Indexer.Chunking;

namespace StarTrek.Indexer.Tests;

public sealed class SentenceSplitterTests
{
    [Fact]
    public void SplitsOnSentenceEndFollowedByCapitalLetter()
    {
        Assert.Equal(
            ["Пайк — капитан.", "Спок — научный офицер!", "«Энтерпрайз» улетает?"],
            SentenceSplitter.Split("Пайк — капитан. Спок — научный офицер! «Энтерпрайз» улетает?"));
    }

    [Theory]
    [InlineData("Джеймс Т. Кирк командовал кораблём.")]
    [InlineData("В 2254 г. Пайк попал на Талос IV.")]
    [InlineData("Земля XXI в. Пережила войну.")]
    [InlineData("Варп 4,5 и скорость 3.5 узла.")]
    [InlineData("Это был т. е. особый случай.")]
    public void KeepsInitialsAbbreviationsAndNumbersInsideSentence(string text)
    {
        Assert.Equal([text], SentenceSplitter.Split(text));
    }

    [Fact]
    public void KeepsRegistryCodesIntact()
    {
        Assert.Equal(
            ["Корабль получил номер NCC-1701-D.", "Капитаном стал Пикар."],
            SentenceSplitter.Split("Корабль получил номер NCC-1701-D. Капитаном стал Пикар."));
    }

    [Fact]
    public void KeepsEpisodeCitationWithPrecedingSentence()
    {
        Assert.Equal(
            ["Талосианцы — телепатическая раса. (TOS: \"The Cage\")", "Они живут на Талосе IV."],
            SentenceSplitter.Split("Талосианцы — телепатическая раса. (TOS: \"The Cage\") Они живут на Талосе IV."));
    }
}
