using Microsoft.Extensions.Configuration;

namespace StarTrek.Mcp;

public static class McpConfiguration
{
    public static IConfiguration Load(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        var indexerSettings = Path.Combine(repositoryRoot, "src", "StarTrek.Indexer", "appsettings.json");
        if (!File.Exists(indexerSettings))
        {
            indexerSettings = Path.Combine(AppContext.BaseDirectory, "indexer.appsettings.json");
        }

        var serverSettings = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(serverSettings))
        {
            serverSettings = Path.Combine(repositoryRoot, "src", "StarTrek.Mcp", "appsettings.json");
        }

        return new ConfigurationBuilder()
            .AddJsonFile(indexerSettings, optional: false, reloadOnChange: false)
            .AddJsonFile(serverSettings, optional: false, reloadOnChange: false)
            .AddEnvironmentVariables("STARTREK_")
            .Build();
    }
}
