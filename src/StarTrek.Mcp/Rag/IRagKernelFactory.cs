using Microsoft.SemanticKernel;

namespace StarTrek.Mcp.Rag;

public interface IRagKernelFactory
{
    Kernel Create();
}
