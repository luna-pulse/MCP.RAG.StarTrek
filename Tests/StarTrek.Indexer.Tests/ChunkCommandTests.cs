using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using StarTrek.Indexer.Chunking;
using StarTrek.Indexer.Commands;
using StarTrek.Indexer.Configuration;
using StarTrek.Indexer.Parsing;

namespace StarTrek.Indexer.Tests;

public sealed class ChunkCommandTests : IDisposable
{
    private const string RaceFile = "SourceFiles/Races/Талосианцы.txt";
    private const string PlanetFile = "SourceFiles/Planets/теллар.txt";
    private const string RaceText =
        "# Талосианцы\nОригинальное название: Talosian\n\n####\nОбщие сведения:\nТелепатическая раса.\n";
    private const string PlanetText =
        "**Тип:** планета.\n\n#### Справочная информация\n" +
        "Подробнее см. «Звёздные карты Федерации # Звёздная карта Объединённой Земли».\n";

    private readonly string root = Path.Combine(Path.GetTempPath(), "startrek-chunk-" + Guid.NewGuid().ToString("N"));
    private readonly RepositoryPaths paths;
    private readonly ChunkStagingStore store;

    public ChunkCommandTests()
    {
        paths = new RepositoryPaths(
            root,
            Path.Combine(root, "SourceFiles"),
            Path.Combine(root, "Data", "staging"),
            Path.Combine(root, "models", "multilingual-e5-small"),
            Path.Combine(root, "Data", "bm25", "corpus.json"));
        store = new ChunkStagingStore(paths.Staging);
        WriteSource(RaceFile, RaceText);
        WriteSource(PlanetFile, PlanetText);
    }

    [Fact]
    public void SecondRunSkipsUnchangedFilesAndRewritesChangedOnes()
    {
        var first = Run();
        Assert.Contains("Обработано и записано: 2", first);
        Assert.Contains("Пропущено (тот же content_hash): 0", first);
        Assert.Contains("Чанков записано: 3", first);
        var raceId = DocumentIdentity.ComputeDocumentId(RaceFile);
        var planetId = DocumentIdentity.ComputeDocumentId(PlanetFile);
        Assert.Equal(
            new[] { raceId, planetId }.Order(StringComparer.Ordinal),
            store.EnumerateFiles().Select(path => Path.GetFileNameWithoutExtension(path)));
        var planetWrittenAt = File.GetLastWriteTimeUtc(store.GetPath(planetId));

        var second = Run();
        Assert.Contains("Обработано и записано: 0", second);
        Assert.Contains("Пропущено (тот же content_hash): 2", second);
        Assert.Contains("Чанков записано: 0", second);

        WriteSource(RaceFile, RaceText.Replace("Телепатическая раса.", "Телепатическая раса с Талоса IV."));
        var third = Run();
        Assert.Contains("Обработано и записано: 1", third);
        Assert.Contains("Пропущено (тот же content_hash): 1", third);
        var race = store.Read(store.GetPath(raceId));
        Assert.Equal("Телепатическая раса с Талоса IV.", Assert.Single(race.Chunks).Body);
        Assert.Equal(
            DocumentIdentity.ComputeContentHash(File.ReadAllText(Path.Combine(root, RaceFile))),
            race.ContentHash);
        Assert.Equal(planetWrittenAt, File.GetLastWriteTimeUtc(store.GetPath(planetId)));
    }

    [Fact]
    public void RemovesStagingFileWhenSourceDisappears()
    {
        Run();
        File.Delete(Path.Combine(root, PlanetFile));

        var output = Run();

        Assert.Contains("Удалено файлов подготовки без исходника: 1", output);
        Assert.False(File.Exists(store.GetPath(DocumentIdentity.ComputeDocumentId(PlanetFile))));
        Assert.True(File.Exists(store.GetPath(DocumentIdentity.ComputeDocumentId(RaceFile))));
    }

    [Fact]
    public void StagingJsonFollowsPlanModelWithoutPassagePrefixAndHashMarks()
    {
        Run();
        var planetId = DocumentIdentity.ComputeDocumentId(PlanetFile);
        var json = File.ReadAllText(store.GetPath(planetId), Encoding.UTF8);

        foreach (var property in new[]
                 {
                     "document_id", "source_file", "content_hash", "title", "original_title", "entity_type",
                     "franchise", "language", "file_type", "chunks", "chunk_index", "heading_level",
                     "section_path", "section_title", "position", "start_line", "end_line", "token_count",
                     "body", "embedding_text"
                 })
        {
            Assert.Contains($"\"{property}\":", json);
        }

        Assert.DoesNotContain("passage:", json);
        Assert.DoesNotContain("#", json);
        Assert.Contains("Звёздные карты Федерации Звёздная карта", json);
        var document = store.Read(store.GetPath(planetId));
        Assert.Equal(planetId, document.DocumentId);
        Assert.Equal(PlanetFile, document.SourceFile);
        Assert.Equal("Теллар", document.Title);
        Assert.Equal("planet", document.EntityType);
        Assert.Equal("txt", document.FileType);
        Assert.Equal(["Справочная информация"], document.Chunks[1].SectionPath);
        Assert.Equal("lines 3-4", document.Chunks[1].Position);
    }

    [Fact]
    public async Task ChunkWithoutTokenizerFailsAndLeavesStagingUntouched()
    {
        Directory.CreateDirectory(paths.Staging);
        var sentinel = Path.Combine(paths.Staging, new string('a', 64) + ChunkStagingStore.FileExtension);
        File.WriteAllText(sentinel, "{}");
        var emptyModel = Path.Combine(root, "missing-model");
        Directory.CreateDirectory(emptyModel);

        var startInfo = new ProcessStartInfo(
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = TestEnvironment.RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "StarTrek.Indexer.dll"));
        startInfo.ArgumentList.Add("chunk");
        startInfo.Environment["STARTREK_Indexer__Paths__Corpus"] = paths.Corpus;
        startInfo.Environment["STARTREK_Indexer__Paths__Staging"] = paths.Staging;
        startInfo.Environment["STARTREK_Indexer__Paths__EmbeddingModel"] = emptyModel;

        using var process = Process.Start(startInfo)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);

        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains("tokenizer.json", await errorTask + await outputTask);
        Assert.Equal([sentinel], Directory.GetFiles(paths.Staging));
        Assert.Equal("{}", File.ReadAllText(sentinel));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private string Run()
    {
        var options = Options.Create(new IndexerOptions { Chunking = TestEnvironment.ChunkingOptions });
        var command = new ChunkCommand(paths, options, new DocumentParser(), TestEnvironment.CreateChunker());
        using var output = new StringWriter();
        Assert.Equal(0, command.Execute(output: output));
        return output.ToString();
    }

    private void WriteSource(string sourceFile, string text)
    {
        var path = Path.Combine(root, sourceFile);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
