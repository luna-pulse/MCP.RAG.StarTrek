using Microsoft.Extensions.DependencyInjection;

namespace StarTrek.Indexer.Commands;

public sealed class CommandDispatcher(IServiceProvider serviceProvider)
{
    public int Execute(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            PrintHelp();
            return 2;
        }

        return arguments[0].ToLowerInvariant() switch
        {
            "analyze" => Analyze(arguments.Skip(1).ToList()),
            "chunk" => Chunk(arguments.Skip(1).ToList()),
            "embed" => Embed(arguments.Skip(1).ToList()),
            "help" or "--help" or "-h" => PrintHelpAndSucceed(),
            _ => UnknownCommand(arguments[0])
        };
    }

    private int Analyze(IReadOnlyList<string> options)
    {
        var unknownOption = options.FirstOrDefault(option =>
            !option.Equals(AnalyzeCommand.ShowAllOption, StringComparison.OrdinalIgnoreCase));
        if (unknownOption is not null)
        {
            Console.Error.WriteLine($"Неизвестный параметр analyze: {unknownOption}");
            PrintHelp();
            return 2;
        }

        return serviceProvider.GetRequiredService<AnalyzeCommand>().Execute(showAll: options.Count > 0);
    }

    private int Chunk(IReadOnlyList<string> options)
    {
        var unknownOption = options.FirstOrDefault(option =>
            !option.Equals(ChunkCommand.ForceOption, StringComparison.OrdinalIgnoreCase));
        if (unknownOption is not null)
        {
            Console.Error.WriteLine($"Неизвестный параметр chunk: {unknownOption}");
            PrintHelp();
            return 2;
        }

        return serviceProvider.GetRequiredService<ChunkCommand>().Execute(force: options.Count > 0);
    }

    private int Embed(IReadOnlyList<string> options)
    {
        var unknownOption = options.FirstOrDefault(option =>
            !option.Equals(EmbedCommand.ForceOption, StringComparison.OrdinalIgnoreCase));
        if (unknownOption is not null)
        {
            Console.Error.WriteLine($"Неизвестный параметр embed: {unknownOption}");
            PrintHelp();
            return 2;
        }

        return serviceProvider.GetRequiredService<EmbedCommand>()
            .ExecuteAsync(force: options.Count > 0)
            .GetAwaiter()
            .GetResult();
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Неизвестная команда: {command}");
        PrintHelp();
        return 2;
    }

    private static int PrintHelpAndSucceed()
    {
        PrintHelp();
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Использование: dotnet run --project src/StarTrek.Indexer -- <команда>");
        Console.WriteLine("  analyze  Проанализировать структуру и размеры разделов корпуса");
        Console.WriteLine($"           {AnalyzeCommand.ShowAllOption}  вывести полные списки вместо первых 20 строк");
        Console.WriteLine("  chunk    Нарезать корпус на чанки и сохранить JSON в папку подготовки");
        Console.WriteLine($"           {ChunkCommand.ForceOption}  пересобрать все файлы, даже с тем же content_hash");
        Console.WriteLine("  embed    Посчитать векторы и записать чанки в ChromaDB (ChromaDB должна быть запущена)");
        Console.WriteLine($"           {EmbedCommand.ForceOption}  перезаписать все карточки, даже с тем же content_hash");
    }
}
