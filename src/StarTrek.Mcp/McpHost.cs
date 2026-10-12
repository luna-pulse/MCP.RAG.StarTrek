using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using StarTrek.Indexer.Configuration;

namespace StarTrek.Mcp;

public static class McpHost
{
    public static WebApplication Build(
        string repositoryRoot,
        IConfiguration configuration,
        Action<IServiceCollection>? configureServices = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(configuration);

        var builder = WebApplication.CreateBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddConfiguration(configuration);

        builder.Services.AddStarTrekSearchRead(builder.Configuration, repositoryRoot);
        builder.Services.Configure<McpOptions>(builder.Configuration.GetSection(McpOptions.SectionName));
        configureServices?.Invoke(builder.Services);
        // AddStarTrekSearchRead registers the repository root as a string singleton.
        // The MCP SDK treats every resolvable service type as a tool argument, so that
        // registration would replace the caller's query. Nothing in the search graph resolves string.
        for (var index = builder.Services.Count - 1; index >= 0; index--)
        {
            var descriptor = builder.Services[index];
            if (descriptor.ServiceType == typeof(string) && descriptor.ImplementationInstance is string)
            {
                builder.Services.RemoveAt(index);
            }
        }

        builder.Services
            .AddMcpServer()
            .WithHttpTransport(options =>
            {
                options.SessionMode = HttpServerSessionMode.Stateless;
            })
            .WithTools<SearchTools>()
            .WithRequestFilters(filters =>
            {
                filters.AddCallToolFilter(next => async (context, cancellationToken) =>
                {
                    var logger = context.Services?.GetService<ILoggerFactory>()?.CreateLogger(McpLog.Requests);
                    LogIncoming(logger, context);
                    CallToolResult result;
                    try
                    {
                        result = StripSdkErrorPrefix(context, await next(context, cancellationToken));
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        var message = exception is McpException mcp ? mcp.Message : exception.Message;
                        result = new CallToolResult
                        {
                            IsError = true,
                            Content = [new TextContentBlock { Text = message }]
                        };
                    }

                    LogOutgoing(logger, result);
                    return result;
                });
            });

        var mcpOptions = builder.Configuration.GetSection(McpOptions.SectionName).Get<McpOptions>() ?? new McpOptions();
        GelfFileLog.Add(builder.Logging, repositoryRoot, mcpOptions.Logging);
        builder.WebHost.UseUrls(mcpOptions.Urls);
        builder.Services.AddHostedService<McpInitializationHostedService>();

        var app = builder.Build();
        app.MapGet("/health", () => Results.Text("MCP-сервер отвечает: healthy.", "text/plain", Encoding.UTF8));
        app.MapMcp(mcpOptions.Path);
        return app;
    }

    private static CallToolResult StripSdkErrorPrefix(
        RequestContext<CallToolRequestParams> context,
        CallToolResult result)
    {
        if (result.IsError != true)
        {
            return result;
        }

        var prefix = $"An error occurred invoking '{context.Params?.Name}': ";
        foreach (var block in result.Content)
        {
            if (block is TextContentBlock text &&
                text.Text.StartsWith(prefix, StringComparison.Ordinal))
            {
                text.Text = text.Text[prefix.Length..];
            }
        }

        return result;
    }

    private static void LogIncoming(ILogger? logger, RequestContext<CallToolRequestParams> context)
    {
        if (logger is null)
        {
            return;
        }

        var arguments = context.Params?.Arguments;
        var limit = arguments is not null && arguments.ContainsKey("limit")
            ? ArgumentText(arguments, "limit")
            : "нет";
        logger.LogDebug(
            "Входящая модель запроса: инструмент {Tool}, query {Query}, limit {Limit}.",
            context.Params?.Name,
            ArgumentText(arguments, "query"),
            limit);
    }

    private static void LogOutgoing(ILogger? logger, CallToolResult result)
    {
        if (logger is null)
        {
            return;
        }

        var text = result.IsError == true
            ? FirstText(result)
            : result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? string.Empty;
        logger.LogDebug("Исходящая модель ответа: {Response}", text);
    }

    private static string ArgumentText(IDictionary<string, JsonElement>? arguments, string name)
    {
        if (arguments is null || !arguments.TryGetValue(name, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.GetRawText();
    }

    private static string FirstText(CallToolResult result) =>
        result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? string.Empty;

    private sealed class McpInitializationHostedService(ILoggerFactory loggerFactory, IOptions<McpOptions> options)
        : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            var mcp = options.Value;
            loggerFactory.CreateLogger(McpLog.Initialization).LogDebug(
                "Слушатель HTTP {Urls}. Конечная точка MCP {Path}. Зарегистрированы инструменты search_bm25, search_semantic и search_hybrid.",
                mcp.Urls,
                mcp.Path);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
