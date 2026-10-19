namespace StarTrek.Mcp.Rag;

public interface IRagChat
{
    Task<string> InvokeAsync(
        string functionName,
        IReadOnlyDictionary<string, string> variables,
        CancellationToken cancellationToken);
}
