namespace StarTrek.Indexer.Configuration;

public static class RepositoryPathResolver
{
    private const string SolutionFileName = "MCP.RAG.StarTrek.sln";

    public static string FindRepositoryRoot(params string?[] startingDirectories)
    {
        foreach (var startingDirectory in startingDirectories.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var root = SearchUpward(startingDirectory!);
            if (root is not null)
            {
                return root;
            }
        }

        throw new DirectoryNotFoundException(
            $"Не найден корень репозитория с файлом {SolutionFileName}. " +
            "Запустите команду из репозитория или его подпапки.");
    }

    public static RepositoryPaths Resolve(string repositoryRoot, PathOptions paths) =>
        new(
            Path.GetFullPath(repositoryRoot),
            ResolvePath(repositoryRoot, paths.Corpus),
            ResolvePath(repositoryRoot, paths.Staging),
            ResolvePath(repositoryRoot, paths.EmbeddingModel),
            ResolvePath(repositoryRoot, paths.Bm25Corpus));

    public static string ResolvePath(string repositoryRoot, string configuredPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredPath);
        return Path.GetFullPath(
            Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.Combine(repositoryRoot, configuredPath));
    }

    private static string? SearchUpward(string startingDirectory)
    {
        var path = Path.GetFullPath(startingDirectory);
        if (File.Exists(path))
        {
            path = Path.GetDirectoryName(path)!;
        }

        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
            {
                return directory.FullName;
            }
        }

        return null;
    }
}
