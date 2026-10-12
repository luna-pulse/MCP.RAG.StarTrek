namespace StarTrek.Mcp;

public sealed class McpOptions
{
    public const string SectionName = "Mcp";

    public string Path { get; init; } = string.Empty;

    public string Urls { get; init; } = string.Empty;

    public McpLoggingOptions Logging { get; init; } = new();
}

public sealed class McpLoggingOptions
{
    public string Level { get; init; } = string.Empty;

    public string InitializationLogPath { get; init; } = string.Empty;

    public string RequestLogPath { get; init; } = string.Empty;
}
