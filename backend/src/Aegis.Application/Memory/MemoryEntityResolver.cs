using Aegis.Application.Observability;
using Aegis.Domain.Entities;

namespace Aegis.Application.Memory;

public enum MemoryEntityResolutionKind { Resolved, NotFound, Ambiguous }
public sealed record MemoryEntityResolution(MemoryEntityResolutionKind Kind, IReadOnlyList<MemoryEntity> Candidates)
{
    public MemoryEntity? Entity => Kind == MemoryEntityResolutionKind.Resolved ? Candidates[0] : null;
}

public sealed class MemoryEntityResolver(IMemoryStore store, TimeProvider clock, AegisMetrics metrics)
{
    public async Task<MemoryEntityResolution> ResolveAsync(string mention, string? entityType = null,
        bool includeRetired = false, CancellationToken ct = default)
    {
        var result = await ResolveCoreAsync(store, mention, entityType, includeRetired, ct);
        RecordMetrics(result.Kind);
        return result;
    }

    public async Task<MemoryEntity> ResolveOrCreateAsync(string mention, string? entityType = null, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        (MemoryEntity entity, MemoryEntityResolutionKind kind, bool created) result;
        try
        {
            result = await store.WriteAsync(async s =>
            {
                var resolved = await ResolveCoreAsync(s, mention, entityType, false, ct);
                if (resolved.Kind == MemoryEntityResolutionKind.Ambiguous)
                    throw new MemoryException("memory_entity_ambiguous", "Mais de uma entidade canônica corresponde à menção.");
                if (resolved.Entity is not null) return (resolved.Entity, MemoryEntityResolutionKind.Resolved, false);
                var candidate = new MemoryEntity(mention, entityType, now);
                s.Add(candidate);
                s.Add(new MemoryProjectionJob(MemoryProjectionTarget.Graph, MemoryAggregateType.MemoryEntity,
                    candidate.Id, candidate.Revision, MemoryProjectionOperation.Upsert, now));
                return (candidate, MemoryEntityResolutionKind.NotFound, true);
            }, ct);
        }
        catch (MemoryException e) when (e.Code == "memory_entity_ambiguous")
        {
            RecordMetrics(MemoryEntityResolutionKind.Ambiguous);
            throw;
        }
        var (entity, kind, created) = result;
        RecordMetrics(kind);
        if (created) metrics.MemoryProjectionJobsCreated.Add(1);
        return entity;
    }

    private static async Task<MemoryEntityResolution> ResolveCoreAsync(IMemoryStore s, string mention, string? entityType,
        bool includeRetired, CancellationToken ct)
    {
        var normalized = MemoryText.Normalize(mention, 200);
        var type = entityType is null ? null : MemoryText.Normalize(entityType, 40);
        var canonical = await s.FindCanonicalEntitiesAsync(normalized, ct);
        var aliased = await s.FindAliasedEntitiesAsync(normalized, ct);
        return Result(Filter(canonical.Concat(aliased), type, includeRetired));
    }

    private static IReadOnlyList<MemoryEntity> Filter(IEnumerable<MemoryEntity> source, string? type, bool includeRetired) =>
        source.Where(x => (includeRetired || x.RetiredAt is null) && (type is null || x.EntityType == type))
            .DistinctBy(x => x.Id).OrderBy(x => x.NormalizedName, StringComparer.Ordinal).ThenBy(x => x.Id).ToArray();

    private static MemoryEntityResolution Result(IReadOnlyList<MemoryEntity> candidates) =>
        new(candidates.Count switch { 0 => MemoryEntityResolutionKind.NotFound, 1 => MemoryEntityResolutionKind.Resolved,
            _ => MemoryEntityResolutionKind.Ambiguous }, candidates);

    private void RecordMetrics(MemoryEntityResolutionKind kind)
    {
        metrics.MemoryGraphEntityResolutions.Add(1);
        if (kind == MemoryEntityResolutionKind.Ambiguous) metrics.MemoryGraphEntityResolutionAmbiguous.Add(1);
        if (kind == MemoryEntityResolutionKind.NotFound) metrics.MemoryGraphEntityResolutionMiss.Add(1);
    }
}
