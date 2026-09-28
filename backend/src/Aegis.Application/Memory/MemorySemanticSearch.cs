using Aegis.Application.Observability;
using Aegis.Domain.Entities;

namespace Aegis.Application.Memory;

public sealed class MemorySemanticSearch(IMemoryStore store, IMemoryEmbeddingClient embeddings, IMemoryVectorStore vectors,
    MemorySemanticOptions options, TimeProvider clock, AegisMetrics metrics)
{
    public async Task<(IReadOnlyList<MemoryRecord> Results, string Mode)> SearchAsync(string query, int limit, CancellationToken ct)
    {
        var start = clock.GetTimestamp();
        metrics.MemorySemanticSearches.Add(1);
        try
        {
            if (!options.Enabled) return await FallbackAsync(query, limit, ct);
            var queryVector = await embeddings.EmbedAsync(query, ct);
            MemoryVectorValidation.Validate(queryVector, options.EmbeddingDimensions);
            var candidateLimit = Math.Min(100, Math.Max(20, limit * 4));
            var candidates = await vectors.SearchAsync(queryVector, candidateLimit, options.SearchScoreThreshold, ct);
            var qualified = candidates.Where(x => x.Score >= options.SearchScoreThreshold)
                .Select(x => x.MemoryId).Distinct().ToArray();
            metrics.MemorySemanticSearchCandidates.Record(qualified.Length);
            var current = await store.LoadActiveByIdsAsync(qualified, clock.GetUtcNow(), ct);
            var byId = current.ToDictionary(x => x.Id);
            var result = new List<MemoryRecord>(limit);
            foreach (var id in qualified)
                if (byId.TryGetValue(id, out var record) && result.Count < limit) result.Add(record);
            // Fresh PostgreSQL writes remain discoverable during the asynchronous projection window.
            if (result.Count < limit)
            {
                var lexical = await store.SearchAsync(query, limit, clock.GetUtcNow(), ct);
                foreach (var record in lexical)
                    if (result.Count < limit && result.All(x => x.Id != record.Id)) result.Add(record);
            }
            metrics.MemorySemanticSearchResults.Record(result.Count);
            return (result, "semantic");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is MemorySemanticException or HttpRequestException or TimeoutException or OperationCanceledException)
        { return await FallbackAsync(query, limit, ct); }
        finally { metrics.MemorySemanticSearchDuration.Record(clock.GetElapsedTime(start).TotalMilliseconds); }
    }

    private async Task<(IReadOnlyList<MemoryRecord>, string)> FallbackAsync(string query, int limit, CancellationToken ct)
    {
        metrics.MemorySemanticSearchFallbacks.Add(1);
        var results = await store.SearchAsync(query, limit, clock.GetUtcNow(), ct);
        metrics.MemorySemanticSearchResults.Record(results.Count);
        return (results, "canonical_text_fallback");
    }
}
