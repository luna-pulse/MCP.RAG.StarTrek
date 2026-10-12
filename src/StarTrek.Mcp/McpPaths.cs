using StarTrek.Indexer.Configuration;

namespace StarTrek.Mcp;

public static class McpPaths
{
    public static string Resolve(string repositoryRoot, string configuredPath) =>
        RepositoryPathResolver.ResolvePath(repositoryRoot, configuredPath);
}
