using StarTrek.Indexer.Configuration;

namespace StarTrek.Mcp;

public static class SearchLimits
{
    public static int Clamp(int? limit, SearchOptions search)
    {
        ArgumentNullException.ThrowIfNull(search);
        var value = limit ?? search.ResultCount;
        if (value < 1)
        {
            return 1;
        }

        var ceiling = Math.Min(search.VectorTopN, search.Bm25TopN);
        return value > ceiling ? ceiling : value;
    }
}
