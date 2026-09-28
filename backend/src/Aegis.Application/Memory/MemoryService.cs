using Aegis.Application.Observability;
using Aegis.Application.Tools;
using Aegis.Domain.Entities;

namespace Aegis.Application.Memory;

public sealed class MemoryService(IMemoryStore store, TimeProvider clock, AegisMetrics metrics)
{
    public const int DefaultSearchLimit = 10;
    public const int MaxSearchLimit = 30;
    public Task<string?> GetContextAsync(Guid conversationId, CancellationToken ct) => store.GetContextAsync(conversationId, clock.GetUtcNow(), ct);

    public async Task<(MemoryRecord Record, bool Deduplicated)> RememberAsync(string content, DateTimeOffset? validFrom, DateTimeOffset? validUntil, ToolExecutionContext context, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var candidate = new MemoryRecord(content, validFrom, validUntil, now);
        var result = await store.WriteAsync(async s =>
        {
            var existing = await s.FindActiveByHashAsync(candidate.ContentHash, ct);
            if (existing is not null && validFrom is null && validUntil is null &&
                (existing.ValidFrom > now || existing.ValidUntil <= now))
                throw new MemoryException("memory_not_current", "Existe uma memória temporal idêntica fora da validade atual; consulte e reformule a informação antes de guardar novamente.");
            if (existing is not null && (validFrom is not null && existing.ValidFrom != candidate.ValidFrom ||
                validUntil is not null && existing.ValidUntil != candidate.ValidUntil))
                throw new MemoryException("memory_validity_conflict", "Já existe uma memória vigente com esse conteúdo e validade diferente; corrija a memória específica após consultá-la.");
            var record = existing ?? candidate;
            if (existing is null)
            {
                s.Add(record);
                s.Add(new MemoryProjectionJob(MemoryProjectionTarget.Semantic, MemoryAggregateType.MemoryRecord, record.Id, record.Revision, MemoryProjectionOperation.Upsert, now));
            }
            if (!await s.HasEvidenceAsync(record.Id, MemorySourceKind.ExplicitMemoryRequest, context.UserMessageId, ct))
                s.Add(new MemoryEvidence(record.Id, MemorySourceKind.ExplicitMemoryRequest, context.ConversationId, context.UserMessageId, now, now));
            await s.ObserveAsync(context.ConversationId, [record], "memory_remember", now, ct);
            return (record, existing is not null);
        }, ct);
        if (result.Item2) metrics.MemoryDeduplicated.Add(1); else { metrics.MemoryCreated.Add(1); metrics.MemoryProjectionJobsCreated.Add(1); }
        return result;
    }

    public async Task<IReadOnlyList<MemoryRecord>> SearchAsync(string query, int limit, Guid conversationId, CancellationToken ct)
    {
        if (limit is < 1 or > MaxSearchLimit) throw new ArgumentException("Limite deve estar entre 1 e 30.");
        var clean = MemoryText.Clean(query, 200);
        var now = clock.GetUtcNow();
        var results = await store.SearchAsync(clean, limit, now, ct);
        await store.ObserveAsync(conversationId, results, "memory_search", now, ct);
        metrics.MemorySearches.Add(1); metrics.MemorySearchResults.Record(results.Count);
        return results;
    }

    public async Task<MemoryRecord> UpdateAsync(Guid id, string content, DateTimeOffset? validFrom, DateTimeOffset? validUntil, ToolExecutionContext context, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var (record, superseded, created) = await store.WriteAsync(async s =>
        {
            if (!await s.WasObservedAsync(context.ConversationId, id, now, ct)) throw new MemoryException("memory_reference_required", "Consulte memory_search e escolha uma memória inequívoca e ainda válida.");
            var old = await s.FindRecordAsync(id, ct);
            if (old is null || old.Status != MemoryStatus.Active) throw new MemoryException("memory_not_active", "Essa memória não está vigente. Consulte memory_search novamente.");
            if (validFrom is null && validUntil is null && (old.ValidFrom > now || old.ValidUntil <= now))
                throw new MemoryException("memory_not_current", "A memória observada está fora da validade atual; consulte e informe a nova validade antes de corrigi-la.");
            var candidate = new MemoryRecord(content, validFrom ?? old.ValidFrom, validUntil ?? old.ValidUntil, now);
            if (old.ContentHash == candidate.ContentHash)
            {
                if (old.ValidFrom != candidate.ValidFrom || old.ValidUntil != candidate.ValidUntil)
                    throw new MemoryException("memory_validity_conflict", "Alterar somente a validade de uma memória exige uma nova formulação do conteúdo nesta etapa.");
                if (!await s.HasEvidenceAsync(old.Id, MemorySourceKind.ExplicitMemoryRequest, context.UserMessageId, ct))
                    s.Add(new MemoryEvidence(old.Id, MemorySourceKind.ExplicitMemoryRequest, context.ConversationId, context.UserMessageId, now, now));
                return (old, false, false);
            }
            var existing = await s.FindActiveByHashAsync(candidate.ContentHash, ct);
            var replacement = existing ?? candidate;
            if (existing is null)
            {
                s.Add(replacement);
                s.Add(new MemoryProjectionJob(MemoryProjectionTarget.Semantic, MemoryAggregateType.MemoryRecord, replacement.Id, replacement.Revision, MemoryProjectionOperation.Upsert, now));
            }
            if (!await s.HasEvidenceAsync(replacement.Id, MemorySourceKind.ExplicitMemoryRequest, context.UserMessageId, ct))
                s.Add(new MemoryEvidence(replacement.Id, MemorySourceKind.ExplicitMemoryRequest, context.ConversationId, context.UserMessageId, now, now));
            old.Supersede(replacement.Id, now);
            s.Add(new MemoryProjectionJob(MemoryProjectionTarget.Semantic, MemoryAggregateType.MemoryRecord, old.Id, old.Revision, MemoryProjectionOperation.Delete, now));
            await s.ObserveAsync(context.ConversationId, [replacement], "memory_update", now, ct);
            return (replacement, true, existing is null);
        }, ct);
        if (superseded) { metrics.MemorySuperseded.Add(1); metrics.MemoryProjectionJobsCreated.Add(created ? 2 : 1); }
        if (created) metrics.MemoryCreated.Add(1);
        return record;
    }

    public async Task<MemoryRecord> ForgetAsync(Guid id, Guid conversationId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var (record, changed) = await store.WriteAsync(async s =>
        {
            if (!await s.WasObservedAsync(conversationId, id, now, ct)) throw new MemoryException("memory_reference_required", "Consulte memory_search e escolha uma memória inequívoca e ainda válida.");
            var found = await s.FindRecordAsync(id, ct) ?? throw new MemoryException("memory_not_found", "Memória não encontrada.");
            var didChange = found.Forget(now);
            if (didChange) s.Add(new MemoryProjectionJob(MemoryProjectionTarget.Semantic, MemoryAggregateType.MemoryRecord, found.Id, found.Revision, MemoryProjectionOperation.Delete, now));
            return (found, didChange);
        }, ct);
        if (changed) { metrics.MemoryForgotten.Add(1); metrics.MemoryProjectionJobsCreated.Add(1); }
        return record;
    }

    // Internal canonical graph operations. No public graph tools or projection consumers exist yet.
    public async Task<MemoryEntity> CreateEntityAsync(string name, string? type, CancellationToken ct)
    {
        var now = clock.GetUtcNow(); var entity = new MemoryEntity(name, type, now);
        await store.WriteAsync(s => { s.Add(entity); s.Add(new MemoryProjectionJob(MemoryProjectionTarget.Graph, MemoryAggregateType.MemoryEntity, entity.Id, entity.Revision, MemoryProjectionOperation.Upsert, now)); return Task.FromResult(0); }, ct);
        metrics.MemoryProjectionJobsCreated.Add(1); return entity;
    }
    public async Task<MemoryEntityAlias> AddAliasAsync(Guid entityId, string alias, CancellationToken ct)
    {
        var now = clock.GetUtcNow(); var candidate = new MemoryEntityAlias(entityId, alias, now);
        var (result, created) = await store.WriteAsync(async s =>
        {
            var entity = await s.FindEntityAsync(entityId, ct) ?? throw new MemoryException("memory_entity_not_found", "Entidade não encontrada.");
            var existing = await s.FindAliasAsync(entityId, candidate.NormalizedAlias, ct);
            if (existing is not null) return (existing, false);
            s.Add(candidate); entity.AliasChanged(now);
            s.Add(new MemoryProjectionJob(MemoryProjectionTarget.Graph, MemoryAggregateType.MemoryEntity, entity.Id, entity.Revision, MemoryProjectionOperation.Upsert, now));
            return (candidate, true);
        }, ct);
        if (created) metrics.MemoryProjectionJobsCreated.Add(1); return result;
    }
    public async Task<MemoryRelation> CreateRelationAsync(Guid subjectId, string predicate, Guid objectId, DateTimeOffset? validFrom, DateTimeOffset? validUntil, CancellationToken ct)
    {
        var now = clock.GetUtcNow(); var candidate = new MemoryRelation(subjectId, predicate, objectId, validFrom, validUntil, now);
        var (relation, created) = await store.WriteAsync(async s =>
        {
            if (await s.FindEntityAsync(subjectId, ct) is null || await s.FindEntityAsync(objectId, ct) is null) throw new MemoryException("memory_entity_not_found", "Entidade não encontrada.");
            var existing = await s.FindActiveRelationAsync(subjectId, candidate.Predicate, objectId, ct);
            if (existing is not null) return (existing, false);
            s.Add(candidate);
            s.Add(new MemoryProjectionJob(MemoryProjectionTarget.Graph, MemoryAggregateType.MemoryRelation, candidate.Id, candidate.Revision, MemoryProjectionOperation.Upsert, now));
            return (candidate, true);
        }, ct);
        if (created) metrics.MemoryProjectionJobsCreated.Add(1); return relation;
    }
    public async Task SupportRelationAsync(Guid relationId, Guid memoryId, CancellationToken ct)
    {
        await store.WriteAsync(async s =>
        {
            if (await s.FindRecordAsync(memoryId, ct) is null || await s.HasRelationEvidenceAsync(relationId, memoryId, ct)) return 0;
            s.Add(new MemoryRelationEvidence(relationId, memoryId, clock.GetUtcNow())); return 0;
        }, ct);
    }
}

public sealed class MemoryException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
