namespace StarTrek.Indexer.Search;

public static class SearchFailures
{
    public static InvalidOperationException CollectionMissing(string name) =>
        new($"Коллекция {name} не найдена в ChromaDB. Сначала выполните команду embed.");
}
