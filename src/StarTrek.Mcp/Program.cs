using StarTrek.Indexer.Configuration;

namespace StarTrek.Mcp;

public static class Program
{
    public static void Main(string[] args)
    {
        var repositoryRoot = RepositoryPathResolver.FindRepositoryRoot(
            Environment.CurrentDirectory,
            AppContext.BaseDirectory);
        var configuration = McpConfiguration.Load(repositoryRoot);
        var app = McpHost.Build(repositoryRoot, configuration);
        app.Run();
    }
}
