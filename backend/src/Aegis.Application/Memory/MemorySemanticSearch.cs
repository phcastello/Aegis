using Aegis.Application.Observability;
using Aegis.Domain.Entities;

namespace Aegis.Application.Memory;

public sealed class MemorySemanticSearch(IMemoryStore store, IMemoryEmbeddingClient embeddings, IMemoryVectorStore vectors,
    MemorySemanticOptions options, TimeProvider clock, AegisMetrics metrics)
{
    public sealed record RankedMemory(MemoryRecord Record, double? Similarity);
    public sealed record CandidateTrace(Guid MemoryId, string? Content, double? Score, bool CanonicalValid,
        bool Kept, string? DropReason);
    public sealed record SearchTrace(double Threshold, IReadOnlyList<CandidateTrace> Semantic,
        IReadOnlyList<CandidateTrace> Lexical, string Mode, string? FallbackReason = null);

    public async Task<(IReadOnlyList<MemoryRecord> Results, string Mode)> SearchAsync(string query, int limit, CancellationToken ct)
    {
        var (results, mode) = await SearchDetailedAsync(query, limit, clock.GetUtcNow(), options.SearchScoreThreshold, ct);
        return (results.Select(x => x.Record).ToArray(), mode);
    }

    public async Task<(IReadOnlyList<RankedMemory> Results, string Mode)> SearchDetailedAsync(string query, int limit,
        DateTimeOffset asOf, double threshold, CancellationToken ct)
    {
        var (results, mode, _) = await SearchTracedAsync(query, limit, asOf, threshold, ct);
        return (results, mode);
    }

    public async Task<(IReadOnlyList<RankedMemory> Results, string Mode, SearchTrace Trace)> SearchTracedAsync(
        string query, int limit, DateTimeOffset asOf, double threshold, CancellationToken ct,
        bool includeHistorical = false)
    {
        var start = clock.GetTimestamp();
        metrics.MemorySemanticSearches.Add(1);
        try
        {
            if (!options.Enabled) return await FallbackTracedAsync(query, limit, asOf, threshold,
                includeHistorical, "semantic_disabled", ct);
            var queryVector = await embeddings.EmbedAsync(query, ct);
            MemoryVectorValidation.Validate(queryVector, options.EmbeddingDimensions);
            var candidateLimit = Math.Min(100, Math.Max(20, limit * 4));
            var candidates = (await vectors.SearchAsync(queryVector, candidateLimit, 0, ct))
                .DistinctBy(x => x.MemoryId).ToArray();
            var qualifiedCandidates = candidates.Where(x => x.Score >= threshold).ToArray();
            var qualified = qualifiedCandidates.Select(x => x.MemoryId).ToArray();
            metrics.MemorySemanticSearchCandidates.Record(qualified.Length);
            var current = includeHistorical
                ? await store.LoadHistoricalByIdsAsync(candidates.Select(x => x.MemoryId).ToArray(), asOf, ct)
                : await store.LoadActiveByIdsAsync(candidates.Select(x => x.MemoryId).ToArray(), asOf, ct);
            var byId = current.ToDictionary(x => x.Id);
            var result = new List<RankedMemory>(limit);
            var semanticTrace = new List<CandidateTrace>();
            foreach (var candidate in candidates.Where(x => x.Score < threshold))
                semanticTrace.Add(new(candidate.MemoryId,
                    byId.TryGetValue(candidate.MemoryId, out var low) ? Safe(low.Content) : null,
                    candidate.Score, byId.ContainsKey(candidate.MemoryId), false, "below_threshold"));
            foreach (var candidate in qualifiedCandidates)
            {
                var valid = byId.TryGetValue(candidate.MemoryId, out var record);
                var kept = valid && result.Count < limit;
                semanticTrace.Add(new(candidate.MemoryId, valid ? Safe(record!.Content) : null,
                    candidate.Score, valid, kept, valid ? kept ? null : "limit" : "canonical_invalid"));
                if (kept) result.Add(new(record!, candidate.Score));
            }
            // Fresh PostgreSQL writes remain discoverable during the asynchronous projection window.
            var lexicalTrace = new List<CandidateTrace>();
            if (result.Count < limit)
            {
                var lexical = includeHistorical
                    ? await store.SearchHistoricalAsync(query, limit, ct)
                    : await store.SearchAsync(query, limit, asOf, ct);
                foreach (var record in lexical)
                {
                    var kept = result.Count < limit && result.All(x => x.Record.Id != record.Id);
                    lexicalTrace.Add(new(record.Id, Safe(record.Content), null, true, kept,
                        kept ? null : "duplicate_or_limit"));
                    if (kept) result.Add(new(record, null));
                }
            }
            metrics.MemorySemanticSearchResults.Record(result.Count);
            return (result, "semantic", new(threshold,
                semanticTrace.OrderByDescending(x => x.Score).ToArray(), lexicalTrace, "semantic"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is MemorySemanticException or HttpRequestException or TimeoutException or OperationCanceledException)
        { return await FallbackTracedAsync(query, limit, asOf, threshold, includeHistorical,
            e is MemorySemanticException semanticError ? semanticError.Code : e.GetType().Name, ct); }
        finally { metrics.MemorySemanticSearchDuration.Record(clock.GetElapsedTime(start).TotalMilliseconds); }
    }

    private async Task<(IReadOnlyList<RankedMemory>, string)> FallbackAsync(string query, int limit,
        DateTimeOffset asOf, bool includeHistorical, CancellationToken ct)
    {
        metrics.MemorySemanticSearchFallbacks.Add(1);
        var results = includeHistorical
            ? await store.SearchHistoricalAsync(query, limit, ct)
            : await store.SearchAsync(query, limit, asOf, ct);
        metrics.MemorySemanticSearchResults.Record(results.Count);
        return (results.Select(x => new RankedMemory(x, null)).ToArray(), "canonical_text_fallback");
    }

    private async Task<(IReadOnlyList<RankedMemory>, string, SearchTrace)> FallbackTracedAsync(
        string query, int limit, DateTimeOffset asOf, double threshold, bool includeHistorical,
        string fallbackReason, CancellationToken ct)
    {
        var (results, mode) = await FallbackAsync(query, limit, asOf, includeHistorical, ct);
        return (results, mode, new(threshold, [], results.Select(x =>
            new CandidateTrace(x.Record.Id, Safe(x.Record.Content), null, true, true, null)).ToArray(),
            mode, fallbackReason));
    }

    private static string Safe(string content) => MemorySecretGuard.ContainsSecret(content) ? "[REDACTED]" : content;
}
