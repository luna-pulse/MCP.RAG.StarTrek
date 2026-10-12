namespace StarTrek.Indexer.Chunking;

public static class SentenceSplitter
{
    private const int MaxCitationLength = 200;

    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "гг", "вв", "др", "пр", "им", "ок", "англ", "лат", "греч", "нем", "фр", "франц", "см", "ср",
        "напр", "тыс", "млн", "млрд", "ст", "стр", "рис", "гл", "ул", "проф", "акад", "св", "род",
        "ум", "прим", "изд", "мр", "mr", "mrs", "ms", "dr", "st", "vs", "no", "jr", "sr"
    };

    public static IReadOnlyList<string> Split(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var sentences = new List<string>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            int end;
            if (IsTerminal(text[index]))
            {
                var next = index + 1;
                while (next < text.Length && IsTerminal(text[next]))
                {
                    next++;
                }

                while (next < text.Length && IsClosing(text[next]))
                {
                    next++;
                }

                if (text[index] == '.' && next == index + 1 && IsAbbreviation(text, index))
                {
                    continue;
                }

                end = next;
            }
            else if (text[index] == ')' && ClosesCitationAfterSentence(text, index))
            {
                end = index + 1;
            }
            else
            {
                continue;
            }

            if (end >= text.Length || !char.IsWhiteSpace(text[end]))
            {
                continue;
            }

            var nextStart = end;
            while (nextStart < text.Length && char.IsWhiteSpace(text[nextStart]))
            {
                nextStart++;
            }

            if (nextStart >= text.Length || !IsSentenceStart(text[nextStart]))
            {
                continue;
            }

            sentences.Add(text[start..end].TrimEnd());
            start = nextStart;
            index = nextStart - 1;
        }

        var tail = text[start..].TrimEnd();
        if (tail.Trim().Length > 0)
        {
            sentences.Add(tail);
        }

        return sentences;
    }

    private static bool IsTerminal(char value) => value is '.' or '!' or '?' or '…';

    private static bool IsClosing(char value) => value is '»' or '"' or '”' or '\'' or ')' or ']';

    private static bool IsSentenceStart(char value) =>
        char.IsUpper(value) || char.IsDigit(value) || value is '«' or '"' or '“' or '„';

    private static bool IsAbbreviation(string text, int dotIndex)
    {
        var wordStart = dotIndex;
        while (wordStart > 0 && char.IsLetter(text[wordStart - 1]))
        {
            wordStart--;
        }

        var word = text[wordStart..dotIndex];
        if (word.Length == 1)
        {
            return wordStart == 0 || char.IsWhiteSpace(text[wordStart - 1]) || text[wordStart - 1] is '.' or '(' or '«';
        }

        return word.Length > 1 && Abbreviations.Contains(word);
    }

    private static bool ClosesCitationAfterSentence(string text, int closeIndex)
    {
        var depth = 0;
        for (var index = closeIndex; index >= 0 && closeIndex - index <= MaxCitationLength; index--)
        {
            if (text[index] == ')')
            {
                depth++;
            }
            else if (text[index] == '(' && --depth == 0)
            {
                var before = index - 1;
                while (before >= 0 && char.IsWhiteSpace(text[before]))
                {
                    before--;
                }

                while (before >= 0 && IsClosing(text[before]))
                {
                    before--;
                }

                return before >= 0 && IsTerminal(text[before]);
            }
        }

        return false;
    }
}
