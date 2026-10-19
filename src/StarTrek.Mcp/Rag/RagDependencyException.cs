namespace StarTrek.Mcp.Rag;

/// <summary>
/// Ollama или модель недоступны. Сообщение уже можно отдать клиенту MCP.
/// </summary>
public sealed class RagDependencyException(string message, Exception? innerException = null) :
    InvalidOperationException(message, innerException);
