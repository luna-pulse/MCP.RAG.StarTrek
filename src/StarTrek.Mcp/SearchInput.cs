using ModelContextProtocol;

namespace StarTrek.Mcp;

public static class SearchInput
{
    public const string EmptyQueryMessage = "Запрос пуст: нужна непустая фраза.";

    public static void RejectBlank(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new McpException(EmptyQueryMessage);
        }
    }
}
