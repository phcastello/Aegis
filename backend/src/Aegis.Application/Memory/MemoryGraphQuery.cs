using Aegis.Application.Observability;
using Aegis.Domain.Entities;

namespace Aegis.Application.Memory;

// Internal graph retrieval for Part 4. Neo4j supplies paths; PostgreSQL decides every returned fact.
public sealed class MemoryGraphQuery(IMemoryGraphStore graph, IMemoryGraphProjectionStore canonical,
    MemoryGraphOptions options, TimeProvider clock, AegisMetrics metrics, IMemoryStore? memoryStore = null)
{
    public async Task<IReadOnlyList<MemoryGraphPath>> TraverseAsync(MemoryGraphTraversalRequest request, CancellationToken ct)
    {
        if (request.StartEntityIds.Count is < 1 or > 10 || request.StartEntityIds.Any(x => x == Guid.Empty) ||
            !Enum.IsDefined(request.Direction) || request.MaxDepth < 1 || request.MaxDepth > options.MaxTraversalDepth ||
            request.Limit < 1 || request.Limit > options.MaxTraversalResults || request.Predicates is { Count: > 20 })
            throw new ArgumentException("Invalid graph traversal bounds.");
        var predicates = (request.Predicates ?? []).Select(MemoryText.NormalizePredicate).Distinct().ToArray();
        var asOf = (request.AsOf ?? clock.GetUtcNow()).ToUniversalTime();
        var start = clock.GetTimestamp();
        metrics.MemoryGraphTraversals.Add(1);
        try
        {
            var candidateLimit = Math.Min(200, Math.Max(20, request.Limit * 4));
            var candidates = await graph.TraverseAsync(request.StartEntityIds, request.Direction, predicates,
                request.MaxDepth, candidateLimit, asOf, ct);
            var entityIds = candidates.SelectMany(x => x.EntityIds).Distinct().ToArray();
            var relationIds = candidates.SelectMany(x => x.RelationIds).Distinct().ToArray();
            var (entities, relations) = await canonical.LoadCandidatesAsync(entityIds, relationIds, ct);
            var unsupported = memoryStore is null ? new HashSet<Guid>() :
                await memoryStore.FindRelationsWithoutValidSupportAsync(relationIds, asOf, ct);
            var entityById = entities.ToDictionary(x => x.Id);
            var relationById = relations.ToDictionary(x => x.Id);
            var result = new List<MemoryGraphPath>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var candidate in candidates)
            {
                if (result.Count >= request.Limit) break;
                if (candidate.RelationIds.Count is < 1 || candidate.RelationIds.Count > request.MaxDepth ||
                    candidate.EntityIds.Count != candidate.RelationIds.Count + 1 ||
                    !request.StartEntityIds.Contains(candidate.EntityIds[0]) ||
                    candidate.EntityIds.Any(id => !entityById.ContainsKey(id))) continue;
                var pathRelations = new List<MemoryGraphRelation>();
                var valid = true;
                for (var i = 0; i < candidate.RelationIds.Count; i++)
                {
                    if (!relationById.TryGetValue(candidate.RelationIds[i], out var relation) ||
                        relation.Status != MemoryStatus.Active ||
                        unsupported.Contains(relation.Id) ||
                        relation.ValidFrom is not null && relation.ValidFrom > asOf ||
                        relation.ValidUntil is not null && relation.ValidUntil <= asOf ||
                        predicates.Length > 0 && !predicates.Contains(relation.Predicate)) { valid = false; break; }
                    var forward = relation.SubjectEntityId == candidate.EntityIds[i] && relation.ObjectEntityId == candidate.EntityIds[i + 1];
                    var reverse = relation.ObjectEntityId == candidate.EntityIds[i] && relation.SubjectEntityId == candidate.EntityIds[i + 1];
                    if (request.Direction == MemoryGraphDirection.Outgoing && !forward ||
                        request.Direction == MemoryGraphDirection.Incoming && !reverse ||
                        request.Direction == MemoryGraphDirection.Both && !forward && !reverse) { valid = false; break; }
                    pathRelations.Add(new MemoryGraphRelation(relation.Id, relation.SubjectEntityId, relation.Predicate,
                        relation.ObjectEntityId, relation.ValidFrom, relation.ValidUntil));
                }
                if (!valid) continue;
                var signature = string.Join(":", candidate.EntityIds) + "/" + string.Join(":", candidate.RelationIds);
                if (!seen.Add(signature)) continue;
                result.Add(new MemoryGraphPath(candidate.EntityIds.Select(id =>
                    new MemoryGraphEntity(id, entityById[id].CanonicalName, entityById[id].EntityType)).ToArray(), pathRelations));
            }
            metrics.MemoryGraphTraversalResults.Record(result.Count);
            return result;
        }
        finally { metrics.MemoryGraphTraversalDuration.Record(clock.GetElapsedTime(start).TotalMilliseconds); }
    }
}
