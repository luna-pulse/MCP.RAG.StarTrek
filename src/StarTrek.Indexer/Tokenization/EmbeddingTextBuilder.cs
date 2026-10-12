using System.Text.RegularExpressions;
using StarTrek.Indexer.Parsing;

namespace StarTrek.Indexer.Tokenization;

public static partial class EmbeddingTextBuilder
{
    public static string ForSection(ParsedDocument document, ParsedSection section)
    {
        var entityType = document.EntityType switch
        {
            "race" => "раса",
            "planet" => "планета",
            "starship" => "звездолёт",
            "series" => "сериал",
            _ => document.EntityType
        };
        var sectionPath = string.Join(" > ", section.Path);

        var text =
            $"Документ: {document.Title}\n" +
            $"Тип сущности: {entityType}\n" +
            $"Раздел: {sectionPath}\n\n" +
            section.Body;
        return RemoveHashMarks(text);
    }

    public static string RemoveHashMarks(string text) => HashMarks().Replace(text, " ");

    [GeneratedRegex(@"[ \t]*#+[ \t]*")]
    private static partial Regex HashMarks();
}
