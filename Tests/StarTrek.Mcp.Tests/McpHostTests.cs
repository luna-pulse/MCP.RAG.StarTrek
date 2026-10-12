using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StarTrek.Indexer.Configuration;
using StarTrek.Indexer.Embeddings;
using StarTrek.Mcp;

namespace StarTrek.Mcp.Tests;

[Collection("McpHost")]
public sealed class McpHostTests
{
    [Fact]
    public void LoadReadsIndexerFileAndEnvOverride()
    {
        var root = RepositoryPathResolver.FindRepositoryRoot(AppContext.BaseDirectory);
        var loaded = McpConfiguration.Load(root);
        Assert.Equal("startrek", loaded["Indexer:Chroma:Collection"]);
        Assert.Equal("1.5", loaded["Indexer:Bm25:K1"]);
        Assert.Equal("0.75", loaded["Indexer:Bm25:B"]);
        Assert.Equal("60", loaded["Indexer:Search:RrfK"]);
        Assert.Equal("10", loaded["Indexer:Search:ResultCount"]);
        Assert.Equal("http://127.0.0.1:8000", loaded["Indexer:Chroma:Endpoint"]);
        Assert.Equal("/mcp", loaded["Mcp:Path"]);
        Assert.Equal("http://0.0.0.0:8080", loaded["Mcp:Urls"]);
        Assert.DoesNotContain(
            "\"Indexer\"",
            File.ReadAllText(Path.Combine(root, "src", "StarTrek.Mcp", "appsettings.json")));

        var absolute = Path.Combine(Path.GetTempPath(), "mcp-init.log");
        Assert.Equal(absolute, McpPaths.Resolve(root, absolute));
        Assert.Equal(
            Path.GetFullPath(Path.Combine(root, "Data", "Logs", "mcp-init.log")),
            McpPaths.Resolve(root, "Data/Logs/mcp-init.log"));
    }

    [Fact]
    public void LoadAppliesStartrekEnvironmentOverrides()
    {
        var root = RepositoryPathResolver.FindRepositoryRoot(AppContext.BaseDirectory);
        Environment.SetEnvironmentVariable("STARTREK_Mcp__Path", "/custom");
        Environment.SetEnvironmentVariable("STARTREK_Indexer__Chroma__Endpoint", "http://chromadb:8000");
        try
        {
            var loaded = McpConfiguration.Load(root);
            Assert.Equal("/custom", loaded["Mcp:Path"]);
            Assert.Equal("http://chromadb:8000", loaded["Indexer:Chroma:Endpoint"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("STARTREK_Mcp__Path", null);
            Environment.SetEnvironmentVariable("STARTREK_Indexer__Chroma__Endpoint", null);
        }
    }

    [Fact]
    public async Task HealthDoesNotTouchIndexes()
    {
        var handler = new ScriptedChromaHandler { Available = false };
        var embedderCalls = 0;
        await using var app = await Start(services =>
        {
            services.AddSingleton(handler.CreateClient());
            services.AddSingleton<IQueryEmbeddingGenerator>(_ =>
            {
                embedderCalls++;
                return new RecordingQueryEmbedder();
            });
        });
        var baseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient();
        var health = await client.GetAsync(new Uri(new Uri(baseUrl), "/health"));
        var root = await client.GetAsync(new Uri(new Uri(baseUrl), "/"));
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("MCP-сервер отвечает: healthy.", await health.Content.ReadAsStringAsync());
        Assert.NotEqual(HttpStatusCode.OK, root.StatusCode);
        Assert.Equal(0, embedderCalls);
        Assert.Empty(handler.Requests);
    }

    internal static async Task<WebApplication> Start(
        Action<IServiceCollection>? configureServices = null,
        string mcpPath = "/mcp",
        string? corpusPath = null)
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var corpus = corpusPath ?? Path.Combine(root, "missing-corpus.json");
        var keys = TestIndexerOptions.Create(corpus).AsMemoryKeys();
        keys["Mcp:Urls"] = "http://127.0.0.1:0";
        keys["Mcp:Path"] = mcpPath;
        keys["Mcp:Logging:Level"] = "Debug";
        keys["Mcp:Logging:InitializationLogPath"] = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "mcp-init.log");
        keys["Mcp:Logging:RequestLogPath"] = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "mcp-requests.log");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(keys).Build();
        var app = McpHost.Build(root, configuration, configureServices);
        await app.StartAsync();
        return app;
    }
}
