using Aegis.Application.Observability;
using Aegis.Application.Tools;
using Aegis.Domain.Entities;

namespace Aegis.Application.Memory;

public sealed class MemoryService(IMemoryStore store, TimeProvider clock, AegisMetrics metrics,
    MemorySemanticSearch? semantic = null, MemoryHybridRetriever? hybrid = null)
{
    public const int DefaultSearchLimit = 10;
    public const int MaxSearchLimit = 30;
    public Task<string?> GetContextAsync(Guid conversationId, CancellationToken ct) => store.GetContextAsync(conversationId, clock.GetUtcNow(), ct);

    public async Task<(MemoryRecord Record, bool Deduplicated)> RememberAsync(string content, DateTimeOffset? validFrom, DateTimeOffset? validUntil, ToolExecutionContext context, CancellationToken ct)
    {
        MemorySecretGuard.RejectIfSecret(content);
        var now = clock.GetUtcNow();
        var candidate = new MemoryRecord(content, validFrom, validUntil, now);
        var result = await store.WriteAsync(async s =>
        {
            var resolved = MemoryTemporal.Resolve(await s.FindActiveByHashAllAsync(candidate.ContentHash, ct),
                candidate, now, validFrom is null && validUntil is null);
            var existing = resolved.Existing;
            candidate = resolved.Candidate;
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

    public async Task<IReadOnlyList<MemoryRecord>> SearchAsync(string query, int limit, Guid conversationId, CancellationToken ct) =>
        (await SearchWithModeAsync(query, limit, conversationId, ct)).Results;

    public async Task<(IReadOnlyList<MemoryRecord> Results, string Mode)> SearchWithModeAsync(string query, int limit, Guid conversationId, CancellationToken ct)
    {
        var result = await SearchHybridAsync(query, limit, null, conversationId, ct);
        return (result.Memories, result.Mode);
    }

    public async Task<MemoryHybridResult> SearchHybridAsync(string query, int limit, DateTimeOffset? asOf,
        Guid conversationId, CancellationToken ct)
    {
        if (limit is < 1 or > MaxSearchLimit) throw new ArgumentException("Limite deve estar entre 1 e 30.");
        var clean = MemoryText.Clean(query, 200);
        var now = clock.GetUtcNow();
        MemoryHybridResult result;
        if (hybrid is not null) result = await hybrid.SearchAsync(clean, limit, asOf, false, ct);
        else if (semantic is not null)
        {
            var (ranked, mode) = await semantic.SearchDetailedAsync(clean, limit, asOf ?? now, 0.45, ct);
            result = new(ranked.Select(x => x.Record).ToArray(), [], mode);
        }
        else result = new(await store.SearchAsync(clean, limit, asOf ?? now, ct), [], "canonical_text_fallback");
        await store.ObserveAsync(conversationId, result.Memories, "memory_search", now, ct);
        metrics.MemorySearches.Add(1); metrics.MemorySearchResults.Record(result.Memories.Count);
        return result;
    }

    public async Task<MemoryRecord> UpdateAsync(Guid id, string content, DateTimeOffset? validFrom, DateTimeOffset? validUntil, ToolExecutionContext context, CancellationToken ct)
    {
        MemorySecretGuard.RejectIfSecret(content);
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
            var existing = MemoryTemporal.Resolve(await s.FindActiveByHashAllAsync(candidate.ContentHash, ct),
                candidate, now, implicitCurrent: false).Existing;
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
            foreach (var relation in await s.FindRelationsExclusivelySupportedByMemoryAsync(old.Id, now, ct))
                if (relation.Forget(now)) s.Add(new MemoryProjectionJob(MemoryProjectionTarget.Graph,
                    MemoryAggregateType.MemoryRelation, relation.Id, relation.Revision, MemoryProjectionOperation.Delete, now));
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
            if (didChange)
                foreach (var relation in await s.FindRelationsExclusivelySupportedByMemoryAsync(found.Id, now, ct))
                    if (relation.Forget(now)) s.Add(new MemoryProjectionJob(MemoryProjectionTarget.Graph,
                        MemoryAggregateType.MemoryRelation, relation.Id, relation.Revision, MemoryProjectionOperation.Delete, now));
            return (found, didChange);
        }, ct);
        if (changed) { metrics.MemoryForgotten.Add(1); metrics.MemoryProjectionJobsCreated.Add(1); }
        return record;
    }

    public async Task<MemoryRecord> CloseRecordValidityAsync(Guid id, DateTimeOffset at, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return await store.WriteAsync(async s =>
        {
            var record = await s.FindRecordAsync(id, ct) ?? throw new MemoryException("memory_not_found", "Memória não encontrada.");
            if (record.CloseValidity(at, now))
            {
                s.Add(new MemoryProjectionJob(MemoryProjectionTarget.Semantic, MemoryAggregateType.MemoryRecord,
                    record.Id, record.Revision, MemoryProjectionOperation.Upsert, now));
                foreach (var relation in await s.FindRelationsExclusivelySupportedByMemoryAsync(record.Id, at, ct))
                    if (relation.ValidFrom is null || relation.ValidFrom < at)
                        if (relation.ValidUntil is null || relation.ValidUntil > at)
                            if (relation.CloseValidity(at, now)) s.Add(new MemoryProjectionJob(MemoryProjectionTarget.Graph,
                                MemoryAggregateType.MemoryRelation, relation.Id, relation.Revision, MemoryProjectionOperation.Upsert, now));
            }
            return record;
        }, ct);
    }

    // Internal canonical graph operations; there are no public graph tools.
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
            var existing = FindExactOrRejectOverlap(await s.FindActiveRelationsAsync(subjectId, candidate.Predicate, objectId, ct), candidate);
            if (existing is not null) return (existing, false);
            s.Add(candidate);
            s.Add(new MemoryProjectionJob(MemoryProjectionTarget.Graph, MemoryAggregateType.MemoryRelation, candidate.Id, candidate.Revision, MemoryProjectionOperation.Upsert, now));
            return (candidate, true);
        }, ct);
        if (created) metrics.MemoryProjectionJobsCreated.Add(1); return relation;
    }

    public async Task<MemoryRelation> SupersedeRelationAsync(Guid relationId, Guid subjectId, string predicate, Guid objectId,
        DateTimeOffset? validFrom, DateTimeOffset? validUntil, CancellationToken ct)
    {
        var now = clock.GetUtcNow(); var candidate = new MemoryRelation(subjectId, predicate, objectId, validFrom, validUntil, now);
        var (replacement, changed, created) = await store.WriteAsync(async s =>
        {
            var old = await s.FindRelationAsync(relationId, ct) ?? throw new MemoryException("memory_relation_not_found", "Relação não encontrada.");
            if (old.Status != MemoryStatus.Active) throw new MemoryException("memory_relation_not_active", "Relação não está vigente.");
            if (old.SubjectEntityId == candidate.SubjectEntityId && old.Predicate == candidate.Predicate &&
                old.ObjectEntityId == candidate.ObjectEntityId && old.ValidFrom == candidate.ValidFrom && old.ValidUntil == candidate.ValidUntil)
                return (old, false, false);
            if (await s.FindEntityAsync(subjectId, ct) is null || await s.FindEntityAsync(objectId, ct) is null)
                throw new MemoryException("memory_entity_not_found", "Entidade não encontrada.");
            var others = (await s.FindActiveRelationsAsync(subjectId, candidate.Predicate, objectId, ct))
                .Where(x => x.Id != old.Id).ToArray();
            var existing = FindExactOrRejectOverlap(others, candidate);
            var current = existing ?? candidate;
            if (existing is null)
            {
                s.Add(current);
                s.Add(new MemoryProjectionJob(MemoryProjectionTarget.Graph, MemoryAggregateType.MemoryRelation,
                    current.Id, current.Revision, MemoryProjectionOperation.Upsert, now));
            }
            old.Supersede(current.Id, now);
            s.Add(new MemoryProjectionJob(MemoryProjectionTarget.Graph, MemoryAggregateType.MemoryRelation,
                old.Id, old.Revision, MemoryProjectionOperation.Delete, now));
            return (current, true, existing is null);
        }, ct);
        if (changed) metrics.MemoryProjectionJobsCreated.Add(created ? 2 : 1);
        return replacement;
    }

    public async Task<MemoryRelation> ForgetRelationAsync(Guid relationId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var (relation, changed) = await store.WriteAsync(async s =>
        {
            var found = await s.FindRelationAsync(relationId, ct) ?? throw new MemoryException("memory_relation_not_found", "Relação não encontrada.");
            var changed = found.Forget(now);
            if (changed) s.Add(new MemoryProjectionJob(MemoryProjectionTarget.Graph, MemoryAggregateType.MemoryRelation,
                found.Id, found.Revision, MemoryProjectionOperation.Delete, now));
            return (found, changed);
        }, ct);
        if (changed) metrics.MemoryProjectionJobsCreated.Add(1);
        return relation;
    }

    public async Task<MemoryRelation> CloseRelationValidityAsync(Guid relationId, DateTimeOffset at, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return await store.WriteAsync(async s =>
        {
            var relation = await s.FindRelationAsync(relationId, ct) ?? throw new MemoryException("memory_relation_not_found", "Relação não encontrada.");
            if (relation.CloseValidity(at, now))
                s.Add(new MemoryProjectionJob(MemoryProjectionTarget.Graph, MemoryAggregateType.MemoryRelation,
                    relation.Id, relation.Revision, MemoryProjectionOperation.Upsert, now));
            return relation;
        }, ct);
    }

    public async Task<MemoryEntity> RetireEntityAsync(Guid entityId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var (entity, changed) = await store.WriteAsync(async s =>
        {
            var found = await s.FindEntityAsync(entityId, ct) ?? throw new MemoryException("memory_entity_not_found", "Entidade não encontrada.");
            if (found.RetiredAt is not null) return (found, false);
            found.Retire(now);
            s.Add(new MemoryProjectionJob(MemoryProjectionTarget.Graph, MemoryAggregateType.MemoryEntity,
                found.Id, found.Revision, MemoryProjectionOperation.Upsert, now));
            return (found, true);
        }, ct);
        if (changed) metrics.MemoryProjectionJobsCreated.Add(1);
        return entity;
    }

    private static MemoryRelation? FindExactOrRejectOverlap(IEnumerable<MemoryRelation> active, MemoryRelation candidate)
    {
        MemoryRelation? exact = null;
        foreach (var relation in active)
        {
            if (relation.ValidFrom == candidate.ValidFrom && relation.ValidUntil == candidate.ValidUntil)
            {
                exact = relation;
                continue;
            }
            if ((relation.ValidUntil is null || candidate.ValidFrom is null || relation.ValidUntil > candidate.ValidFrom) &&
                (candidate.ValidUntil is null || relation.ValidFrom is null || candidate.ValidUntil > relation.ValidFrom))
                throw new MemoryException("memory_relation_overlap", "Já existe uma relação Active com intervalo sobreposto.");
        }
        return exact;
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
