using Aegis.Application.Observability;
using Aegis.Domain.Entities;

namespace Aegis.Application.Memory;

public sealed class MemorySemanticSearch(IMemoryStore store, IMemoryEmbeddingClient embeddings, IMemoryVectorStore vectors,
    MemorySemanticOptions options, TimeProvider clock, AegisMetrics metrics)
{
    public sealed record RankedMemory(MemoryRecord Record, double? Similarity);

    public async Task<(IReadOnlyList<MemoryRecord> Results, string Mode)> SearchAsync(string query, int limit, CancellationToken ct)
    {
        var (results, mode) = await SearchDetailedAsync(query, limit, clock.GetUtcNow(), options.SearchScoreThreshold, ct);
        return (results.Select(x => x.Record).ToArray(), mode);
    }

    public async Task<(IReadOnlyList<RankedMemory> Results, string Mode)> SearchDetailedAsync(string query, int limit,
        DateTimeOffset asOf, double threshold, CancellationToken ct)
    {
        var start = clock.GetTimestamp();
        metrics.MemorySemanticSearches.Add(1);
        try
        {
            if (!options.Enabled) return await FallbackAsync(query, limit, asOf, ct);
            var queryVector = await embeddings.EmbedAsync(query, ct);
            MemoryVectorValidation.Validate(queryVector, options.EmbeddingDimensions);
            var candidateLimit = Math.Min(100, Math.Max(20, limit * 4));
            var candidates = await vectors.SearchAsync(queryVector, candidateLimit, threshold, ct);
            var qualifiedCandidates = candidates.Where(x => x.Score >= threshold).DistinctBy(x => x.MemoryId).ToArray();
            var qualified = qualifiedCandidates.Select(x => x.MemoryId).ToArray();
            metrics.MemorySemanticSearchCandidates.Record(qualified.Length);
            var current = await store.LoadActiveByIdsAsync(qualified, asOf, ct);
            var byId = current.ToDictionary(x => x.Id);
            var result = new List<RankedMemory>(limit);
            foreach (var candidate in qualifiedCandidates)
                if (byId.TryGetValue(candidate.MemoryId, out var record) && result.Count < limit) result.Add(new(record, candidate.Score));
            // Fresh PostgreSQL writes remain discoverable during the asynchronous projection window.
            if (result.Count < limit)
            {
                var lexical = await store.SearchAsync(query, limit, asOf, ct);
                foreach (var record in lexical)
                    if (result.Count < limit && result.All(x => x.Record.Id != record.Id)) result.Add(new(record, null));
            }
            metrics.MemorySemanticSearchResults.Record(result.Count);
            return (result, "semantic");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is MemorySemanticException or HttpRequestException or TimeoutException or OperationCanceledException)
        { return await FallbackAsync(query, limit, asOf, ct); }
        finally { metrics.MemorySemanticSearchDuration.Record(clock.GetElapsedTime(start).TotalMilliseconds); }
    }

    private async Task<(IReadOnlyList<RankedMemory>, string)> FallbackAsync(string query, int limit, DateTimeOffset asOf, CancellationToken ct)
    {
        metrics.MemorySemanticSearchFallbacks.Add(1);
        var results = await store.SearchAsync(query, limit, asOf, ct);
        metrics.MemorySemanticSearchResults.Record(results.Count);
        return (results.Select(x => new RankedMemory(x, null)).ToArray(), "canonical_text_fallback");
    }
}
