using Microsoft.Extensions.Options;

namespace StarTrek.Mcp.Rag;

/// <summary>
/// Тексты четырёх функций лежат в файлах, а не в коде.
/// </summary>
public sealed class PromptCatalog
{
    public const string Rewrite = "Rewrite";
    public const string Grade = "Grade";
    public const string Broaden = "Broaden";
    public const string Generate = "Generate";

    public static readonly IReadOnlyList<string> Names = [Rewrite, Grade, Broaden, Generate];

    public static readonly IReadOnlyDictionary<string, string> Descriptions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Rewrite] = "Переписывает вопрос в поисковый запрос по корпусу Star Trek.",
            [Grade] = "Оценивает, относится ли фрагмент к вопросу: да или нет.",
            [Broaden] = "Расширяет запрос, если релевантных фрагментов мало.",
            [Generate] = "Пишет ответ только по релевантным фрагментам и указывает источники."
        };

    private readonly string _directory;

    public PromptCatalog(IOptions<RagOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _directory = ResolveDirectory(options.Value.PromptsDirectory);
    }

    public string Read(string name)
    {
        if (!Descriptions.ContainsKey(name))
        {
            throw new ArgumentOutOfRangeException(nameof(name), name, "Неизвестная функция промпта.");
        }

        var path = Path.Combine(_directory, name + ".txt");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Файл промпта не найден: {path}", path);
        }

        return File.ReadAllText(path);
    }

    public static string ResolveDirectory(string configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = "Prompts";
        }

        if (Path.IsPathRooted(configured) && System.IO.Directory.Exists(configured))
        {
            return configured;
        }

        var besideAssembly = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configured));
        if (File.Exists(Path.Combine(besideAssembly, Rewrite + ".txt")))
        {
            return besideAssembly;
        }

        var cursor = new DirectoryInfo(AppContext.BaseDirectory);
        while (cursor is not null)
        {
            var candidate = Path.Combine(cursor.FullName, "src", "StarTrek.Mcp", configured);
            if (File.Exists(Path.Combine(candidate, Rewrite + ".txt")))
            {
                return candidate;
            }

            cursor = cursor.Parent;
        }

        return besideAssembly;
    }
}
