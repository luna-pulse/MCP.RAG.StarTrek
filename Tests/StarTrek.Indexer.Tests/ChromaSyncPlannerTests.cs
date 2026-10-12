using StarTrek.Indexer.Chroma;
using StarTrek.Indexer.Chunking;

namespace StarTrek.Indexer.Tests;

public sealed class ChromaSyncPlannerTests
{
    [Fact]
    public void NewDocumentIsAdded()
    {
        var plan = ChromaSyncPlanner.Create([StagedDocuments.Race()], [], force: false);

        var item = Assert.Single(plan.Items);
        Assert.Equal(ChromaSyncAction.Add, item.Action);
        Assert.Empty(item.ExistingIds);
    }

    [Fact]
    public void SameSourceFileAndContentHashIsSkipped()
    {
        var document = StagedDocuments.Race();

        var plan = ChromaSyncPlanner.Create([document], Stored(document), force: false);

        Assert.Equal(ChromaSyncAction.Skip, Assert.Single(plan.Items).Action);
    }

    [Fact]
    public void ChangedContentHashReplacesEveryOldIdOfTheCard()
    {
        var old = StagedDocuments.Race(chunkCount: 3, version: "v1");
        var changed = StagedDocuments.Race(chunkCount: 2, version: "v2");

        var plan = ChromaSyncPlanner.Create([changed], Stored(old), force: false);

        var item = Assert.Single(plan.Items);
        Assert.Equal(ChromaSyncAction.ReplaceChangedHash, item.Action);
        Assert.Equal(
            Enumerable.Range(1, 3).Select(index => $"{old.DocumentId}:{index}"),
            item.ExistingIds.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void SameHashWithMissingChunkIsRewritten()
    {
        var document = StagedDocuments.Race(chunkCount: 3);

        var plan = ChromaSyncPlanner.Create([document], Stored(document).Take(2).ToList(), force: false);

        Assert.Equal(ChromaSyncAction.Rewrite, Assert.Single(plan.Items).Action);
    }

    [Fact]
    public void SameHashWithDifferentSourceFileIsRewritten()
    {
        var document = StagedDocuments.Race();
        var renamed = document with { SourceFile = document.SourceFile.ToUpperInvariant() };

        var plan = ChromaSyncPlanner.Create([renamed], Stored(document), force: false);

        Assert.Equal(ChromaSyncAction.Rewrite, Assert.Single(plan.Items).Action);
    }

    [Fact]
    public void ForceRewritesUnchangedCard()
    {
        var document = StagedDocuments.Race();

        var plan = ChromaSyncPlanner.Create([document], Stored(document), force: true);

        var item = Assert.Single(plan.Items);
        Assert.Equal(ChromaSyncAction.Rewrite, item.Action);
        Assert.Equal(2, item.ExistingIds.Count);
    }

    [Fact]
    public void RecordsWithoutStagingFileAreReportedButNotPlanned()
    {
        var race = StagedDocuments.Race();
        var planet = StagedDocuments.Planet(chunkCount: 3);

        var plan = ChromaSyncPlanner.Create([race], [.. Stored(race), .. Stored(planet)], force: false);

        Assert.Equal([race.DocumentId], plan.Items.Select(item => item.Document.DocumentId));
        Assert.Equal(1, plan.UnstagedDocuments);
        Assert.Equal(3, plan.UnstagedRecords);
        Assert.Equal(
            Enumerable.Range(1, 3).Select(index => $"{planet.DocumentId}:{index}"),
            plan.UnstagedRecordIds.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void RecordWithoutMetadataIsGroupedByIdPrefix()
    {
        var document = StagedDocuments.Race();
        var orphan = new ChromaStoredRecord($"{document.DocumentId}:9", new Dictionary<string, object?>());

        var plan = ChromaSyncPlanner.Create([document], [.. Stored(document), orphan], force: false);

        var item = Assert.Single(plan.Items);
        Assert.Equal(ChromaSyncAction.ReplaceChangedHash, item.Action);
        Assert.Contains(orphan.Id, item.ExistingIds);
    }

    private static List<ChromaStoredRecord> Stored(ChunkedDocument document) =>
        document.Chunks
            .Select(chunk => new ChromaStoredRecord(
                ChromaChunkRecords.CreateId(document.DocumentId, chunk.ChunkIndex),
                ChromaChunkRecords.CreateMetadata(document, chunk)
                    .ToDictionary(pair => pair.Key, pair => (object?)pair.Value)))
            .ToList();
}
