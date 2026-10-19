namespace StarTrek.Mcp.Rag;

/// <summary>
/// Разбор ответа функции Grade: модель должна сказать «да» или «нет».
/// Неоднозначный текст считается нерелевантным.
/// </summary>
public static class GradeVerdict
{
    public static bool IsRelevant(string? response)
    {
        var word = FirstVerdictWord(response);
        return word is "да" or "yes" or "relevant" or "релевантен" or "релевантно" or "релевантный";
    }

    public static string? FirstVerdictWord(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return null;
        }

        var line = response.Trim();
        var breakAt = line.IndexOfAny(['\r', '\n']);
        if (breakAt >= 0)
        {
            line = line[..breakAt];
        }

        line = line.Trim().Trim('"', '\'', '«', '»', '`', '*', ' ');
        var colon = line.IndexOf(':');
        if (colon >= 0 && colon < 24)
        {
            var head = Normalize(FirstWord(line[..colon]));
            if (head is not ("да" or "yes" or "no" or "нет" or "relevant" or "irrelevant"))
            {
                line = line[(colon + 1)..].Trim();
            }
        }

        var word = Normalize(FirstWord(line));
        return word.Length == 0 ? null : word;
    }

    private static string FirstWord(string line)
    {
        var start = 0;
        while (start < line.Length && !char.IsLetter(line[start]))
        {
            start++;
        }

        var end = start;
        while (end < line.Length && char.IsLetter(line[end]))
        {
            end++;
        }

        return start == end ? string.Empty : line[start..end];
    }

    private static string Normalize(string word) => word.Trim().ToLowerInvariant();
}
