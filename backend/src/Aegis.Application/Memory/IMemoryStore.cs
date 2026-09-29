using Aegis.Domain.Entities;

namespace Aegis.Application.Memory;

public interface IMemoryStore
{
    Task<T> WriteAsync<T>(Func<IMemoryStore, Task<T>> action, CancellationToken ct);
    Task<MemoryRecord?> FindActiveByHashAsync(string hash, CancellationToken ct);
    Task<IReadOnlyList<MemoryRecord>> FindActiveByHashAllAsync(string hash, CancellationToken ct);
    Task<MemoryRecord?> FindRecordAsync(Guid id, CancellationToken ct);
    Task<bool> HasEvidenceAsync(Guid memoryId, MemorySourceKind kind, Guid? messageId, CancellationToken ct);
    Task<IReadOnlyList<MemoryRecord>> SearchAsync(string query, int limit, DateTimeOffset now, CancellationToken ct);
    Task<IReadOnlyList<MemoryRecord>> LoadActiveByIdsAsync(IReadOnlyList<Guid> ids, DateTimeOffset now, CancellationToken ct);
    void Add(MemoryRecord record);
    void Add(MemoryEvidence evidence);
    void Add(MemoryEntity entity);
    void Add(MemoryEntityAlias alias);
    void Add(MemoryRelation relation);
    void Add(MemoryRelationEvidence evidence);
    void Add(MemoryProjectionJob job);
    Task<bool> WasObservedAsync(Guid conversationId, Guid memoryId, DateTimeOffset now, CancellationToken ct);
    Task ObserveAsync(Guid conversationId, IReadOnlyList<MemoryRecord> records, string sourceTool, DateTimeOffset now, CancellationToken ct);
    Task<string?> GetContextAsync(Guid conversationId, DateTimeOffset now, CancellationToken ct);
    Task<MemoryObservedContextResult> GetContextResultAsync(Guid conversationId, DateTimeOffset now, CancellationToken ct);
    Task<MemoryEntity?> FindEntityAsync(Guid id, CancellationToken ct);
    Task<MemoryEntityAlias?> FindAliasAsync(Guid entityId, string normalizedAlias, CancellationToken ct);
    Task<IReadOnlyList<MemoryRelation>> FindActiveRelationsAsync(Guid subjectId, string predicate, Guid objectId, CancellationToken ct);
    Task<MemoryRelation?> FindRelationAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<MemoryEntity>> FindCanonicalEntitiesAsync(string normalizedName, CancellationToken ct);
    Task<IReadOnlyList<MemoryEntity>> FindAliasedEntitiesAsync(string normalizedAlias, CancellationToken ct);
    Task<bool> HasRelationEvidenceAsync(Guid relationId, Guid memoryId, CancellationToken ct);
    Task<bool> SourceIsAvailableAsync(MemoryExtractionSource source, CancellationToken ct);
    Task SuppressExtractionForMessageAsync(Guid? messageId, DateTimeOffset now, CancellationToken ct);
    Task<IReadOnlyList<(MemoryEntity Entity, string Name)>> ListEntityNamesAsync(CancellationToken ct);
    Task<IReadOnlyList<MemoryRelationContext>> FindRelationsByMemoryAsync(IReadOnlyList<Guid> memoryIds, DateTimeOffset asOf, int limit, CancellationToken ct);
    Task<IReadOnlyList<MemoryRelationContext>> FindRelationsByEntitiesAsync(IReadOnlyList<Guid> entityIds, DateTimeOffset asOf, int limit, CancellationToken ct);
    Task<IReadOnlyList<MemoryRecord>> FindSupportingMemoriesAsync(IReadOnlyList<Guid> relationIds, DateTimeOffset asOf, int limit, CancellationToken ct);
    Task<IReadOnlyList<string>> ListPredicatesAsync(int limit, CancellationToken ct);
    Task<IReadOnlyList<MemoryRelation>> FindRelationsExclusivelySupportedByMemoryAsync(Guid memoryId, DateTimeOffset at, CancellationToken ct);
    Task<IReadOnlySet<Guid>> FindRelationsWithoutValidSupportAsync(IReadOnlyList<Guid> relationIds, DateTimeOffset at, CancellationToken ct);
}
