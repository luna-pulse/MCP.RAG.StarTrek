namespace StarTrek.Mcp.Rag;

public interface IOllamaReadiness
{
    Task EnsureAsync(CancellationToken cancellationToken);
}
