using Aegis.Domain.Entities;

namespace Aegis.Application.Memory;

public sealed class MemoryGraphOptions
{
    public bool Enabled { get; set; } = true;
    public string Neo4jUri { get; set; } = "bolt://neo4j:7687";
    public string Neo4jUsername { get; set; } = "neo4j";
    public string Neo4jPassword { get; set; } = "";
    public string Neo4jDatabase { get; set; } = "neo4j";
    public int WorkerPollSeconds { get; set; } = 5;
    public int MaxTraversalDepth { get; set; } = 3;
    public int MaxTraversalResults { get; set; } = 50;
}

public sealed record MemoryGraphEntityProjection(Guid EntityId, string CanonicalName, string NormalizedName,
    string? EntityType, IReadOnlyList<string> Aliases, int Revision, DateTimeOffset? RetiredAt);
public sealed record MemoryGraphRelationProjection(Guid RelationId, Guid SubjectEntityId, string Predicate,
    Guid ObjectEntityId, int Revision, DateTimeOffset? ValidFrom, DateTimeOffset? ValidUntil);
public sealed record MemoryGraphProjectionState(MemoryGraphEntityProjection? Entity,
    MemoryGraphRelationProjection? Relation, MemoryGraphEntityProjection? Subject, MemoryGraphEntityProjection? Object);
public sealed record MemoryGraphCandidatePath(IReadOnlyList<Guid> EntityIds, IReadOnlyList<Guid> RelationIds);
public sealed record MemoryGraphEntity(Guid Id, string CanonicalName, string? EntityType);
public sealed record MemoryGraphRelation(Guid Id, Guid SubjectEntityId, string Predicate, Guid ObjectEntityId,
    DateTimeOffset? ValidFrom, DateTimeOffset? ValidUntil);
public sealed record MemoryGraphPath(IReadOnlyList<MemoryGraphEntity> Entities, IReadOnlyList<MemoryGraphRelation> Relations);
public enum MemoryGraphDirection { Outgoing, Incoming, Both }
public sealed record MemoryGraphTraversalRequest(IReadOnlyList<Guid> StartEntityIds, MemoryGraphDirection Direction,
    IReadOnlyList<string>? Predicates = null, int MaxDepth = 2, int Limit = 10, DateTimeOffset? AsOf = null);

public interface IMemoryGraphStore
{
    Task EnsureSchemaAsync(CancellationToken ct);
    Task<MemoryGraphEntityProjection?> GetEntityProjectionAsync(Guid id, CancellationToken ct);
    Task UpsertEntityAsync(MemoryGraphEntityProjection entity, CancellationToken ct);
    Task DeleteEntityAsync(Guid id, CancellationToken ct);
    Task<MemoryGraphRelationProjection?> GetRelationProjectionAsync(Guid id, CancellationToken ct);
    Task UpsertRelationAsync(MemoryGraphRelationProjection relation, CancellationToken ct);
    Task DeleteRelationAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<MemoryGraphCandidatePath>> TraverseAsync(IReadOnlyList<Guid> startIds, MemoryGraphDirection direction,
        IReadOnlyList<string> predicates, int maxDepth, int limit, DateTimeOffset asOf, CancellationToken ct);
    Task DeleteManagedProjectionAsync(CancellationToken ct);
}

public interface IMemoryGraphProjectionStore
{
    Task<MemoryProjectionJob?> ClaimAsync(DateTimeOffset now, TimeSpan lease, CancellationToken ct);
    Task<bool> ReconcileClaimAsync(MemoryProjectionJob claim,
        Func<MemoryGraphProjectionState, CancellationToken, Task> reconcile, CancellationToken ct);
    Task<bool> FailAsync(MemoryProjectionJob claim, string code, DateTimeOffset now, TimeSpan? retryDelay, CancellationToken ct);
    Task<int> RequeueCurrentStateAsync(DateTimeOffset now, CancellationToken ct);
    Task<(IReadOnlyList<MemoryEntity> Entities, IReadOnlyList<MemoryRelation> Relations)> LoadCandidatesAsync(
        IReadOnlyList<Guid> entityIds, IReadOnlyList<Guid> relationIds, CancellationToken ct);
}

public sealed class MemoryGraphException(string code, bool retryable = true) : Exception(code)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}
