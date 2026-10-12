using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StarTrek.Indexer.Commands;
using StarTrek.Indexer.Configuration;

Console.OutputEncoding = Encoding.UTF8;

try
{
    var repositoryRoot = RepositoryPathResolver.FindRepositoryRoot(
        Environment.CurrentDirectory,
        AppContext.BaseDirectory);
    var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    if (!File.Exists(settingsPath))
    {
        settingsPath = Path.Combine(repositoryRoot, "src", "StarTrek.Indexer", "appsettings.json");
    }

    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        ContentRootPath = repositoryRoot
    });
    builder.Configuration.Sources.Clear();
    builder.Configuration
        .AddJsonFile(settingsPath, optional: false, reloadOnChange: false)
        .AddEnvironmentVariables("STARTREK_");

    builder.Services.AddStarTrekIndexer(builder.Configuration, repositoryRoot);

    using var host = builder.Build();
    return host.Services.GetRequiredService<CommandDispatcher>().Execute(args);
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Ошибка: {exception.Message}");
    return 1;
}
