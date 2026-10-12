using System.Reflection;
using Microsoft.Extensions.Logging;
using NLog.Config;
using NLog.Extensions.Logging;
using NLog.Targets;

namespace StarTrek.Mcp;

public static class GelfFileLog
{
    public static void Add(ILoggingBuilder logging, string repositoryRoot, McpLoggingOptions options)
    {
        ArgumentNullException.ThrowIfNull(logging);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(options);

        ConfigurationItemFactory.Default.RegisterItemsFromAssembly(Assembly.Load("NLog.Layouts.GelfLayout"));

        var (microsoftLevel, nlogLevel) = ParseLevel(options.Level);
        var configuration = new LoggingConfiguration();
        AddFile(
            configuration,
            "mcp-initialization",
            McpPaths.Resolve(repositoryRoot, options.InitializationLogPath),
            McpLog.Initialization,
            nlogLevel);
        AddFile(
            configuration,
            "mcp-requests",
            McpPaths.Resolve(repositoryRoot, options.RequestLogPath),
            McpLog.Requests,
            nlogLevel);

        logging.ClearProviders();
        logging.SetMinimumLevel(microsoftLevel);
        logging.AddNLog(configuration);
        logging.AddConsole();
    }

    private static void AddFile(
        LoggingConfiguration configuration,
        string targetName,
        string fileName,
        string loggerName,
        NLog.LogLevel minimum)
    {
        var target = new FileTarget(targetName)
        {
            FileName = fileName,
            Layout = "${gelf}",
            CreateDirs = true,
            KeepFileOpen = false
        };
        configuration.AddTarget(target);
        configuration.LoggingRules.Add(new LoggingRule(loggerName, minimum, target) { Final = true });
    }

    private static (Microsoft.Extensions.Logging.LogLevel Microsoft, NLog.LogLevel NLog) ParseLevel(string value)
    {
        if (!Enum.TryParse(value, ignoreCase: true, out Microsoft.Extensions.Logging.LogLevel microsoft))
        {
            throw UnknownLevel(value);
        }

        var nlog = microsoft switch
        {
            Microsoft.Extensions.Logging.LogLevel.Trace => NLog.LogLevel.Trace,
            Microsoft.Extensions.Logging.LogLevel.Debug => NLog.LogLevel.Debug,
            Microsoft.Extensions.Logging.LogLevel.Information => NLog.LogLevel.Info,
            Microsoft.Extensions.Logging.LogLevel.Warning => NLog.LogLevel.Warn,
            Microsoft.Extensions.Logging.LogLevel.Error => NLog.LogLevel.Error,
            Microsoft.Extensions.Logging.LogLevel.Critical => NLog.LogLevel.Fatal,
            Microsoft.Extensions.Logging.LogLevel.None => NLog.LogLevel.Off,
            _ => throw UnknownLevel(value)
        };

        return (microsoft, nlog);
    }

    private static InvalidOperationException UnknownLevel(string value) =>
        new($"Уровень журнала Mcp:Logging:Level не распознан: «{value}».");
}
