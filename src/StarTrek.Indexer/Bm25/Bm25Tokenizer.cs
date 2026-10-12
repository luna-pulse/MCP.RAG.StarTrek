using System.Globalization;

namespace StarTrek.Indexer.Bm25;

/// <summary>
/// Токенизатор для лексического индекса BM25. Не связан с токенизатором e5: это другой индекс.
/// Токен — слово из букв и цифр в нижнем регистре. Дефис между буквами/цифрами оставляет составной
/// токен целым, если в нём есть цифра (NCC-1701-D, NX-01); слова вроде «научно-исследовательский»
/// режутся по дефису. Остальная пунктуация и пробелы разделяют токены.
/// </summary>
public static class Bm25Tokenizer
{
    /// <summary>Версия правил токенизации. При смене правил сохранённый корпус считается устаревшим.</summary>
    public const int Version = 1;

    private const char Hyphen = '-';
    private const char NonBreakingHyphen = '\u2011';

    public static IReadOnlyList<string> Tokenize(string? text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return tokens;
        }

        var position = 0;
        while (position < text.Length)
        {
            if (!IsWordChar(text[position]))
            {
                position++;
                continue;
            }

            var start = position;
            var parts = new List<(int Start, int End)>(1);
            var hasDigit = false;
            while (true)
            {
                var partStart = position;
                while (position < text.Length && IsWordChar(text[position]))
                {
                    hasDigit |= char.IsDigit(text[position]);
                    position++;
                }

                parts.Add((partStart, position));
                if (position + 1 < text.Length &&
                    IsHyphen(text[position]) &&
                    IsWordChar(text[position + 1]))
                {
                    position++;
                    continue;
                }

                break;
            }

            if (hasDigit || parts.Count == 1)
            {
                tokens.Add(Normalize(text.AsSpan(start, position - start)));
            }
            else
            {
                foreach (var (partStart, partEnd) in parts)
                {
                    tokens.Add(Normalize(text.AsSpan(partStart, partEnd - partStart)));
                }
            }
        }

        return tokens;
    }

    private static string Normalize(ReadOnlySpan<char> token)
    {
        var normalized = token.ToString().ToLowerInvariant();
        return normalized.Contains(NonBreakingHyphen) ? normalized.Replace(NonBreakingHyphen, Hyphen) : normalized;
    }

    private static bool IsHyphen(char value) => value is Hyphen or NonBreakingHyphen;

    private static bool IsWordChar(char value) =>
        char.IsLetterOrDigit(value) ||
        CharUnicodeInfo.GetUnicodeCategory(value) is
            UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;
}
