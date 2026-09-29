using Aegis.Application.Observability;
using Aegis.Domain.Entities;

namespace Aegis.Application.Memory;

public sealed class MemoryAutomaticIngestionService(IMemoryStore store, MemorySemanticSearch semantic,
    TimeProvider clock, AegisMetrics metrics)
{
    public async Task<MemoryExtractionInput> BuildInputAsync(MemoryExtractionSource source, CancellationToken ct)
    {
        var (ranked, _) = await semantic.SearchDetailedAsync(source.Target, 10, source.ObservedAt, 0.45, ct);
        var memories = ranked.Select((x, i) => new MemoryExtractionMemory($"m{i + 1}", x.Record)).ToArray();
        var relations = await store.FindRelationsByMemoryAsync(memories.Select(x => x.Record.Id).ToArray(),
            source.ObservedAt, 12, ct);
        var mentions = await MemoryEntityMentions.FindAsync(store, source.Target, 5, ct);
        var mentionedRelations = await store.FindRelationsByEntitiesAsync(mentions.Select(x => x.Id).ToArray(),
            source.ObservedAt, 12, ct);
        var relationRefs = relations.Concat(mentionedRelations).DistinctBy(x => x.Relation.Id).Take(12)
            .Select((x, i) => new MemoryExtractionRelation($"r{i + 1}", x.Relation,
                x.Subject.CanonicalName, x.Object.CanonicalName)).ToArray();
        return new(source.Target, source.Recent, memories, relationRefs,
            await store.ListPredicatesAsync(20, ct));
    }

    public async Task<MemoryExtractionSummary> ApplyAsync(MemoryExtractionSource source,
        MemoryExtractionInput input, MemoryExtractionOutput output, CancellationToken ct)
    {
        if (output.Candidates.Count > 8) throw new MemoryExtractionException("memory_extraction_invalid_response", false);
        var counters = new int[7];
        foreach (var candidate in output.Candidates)
        {
            counters[0]++;
            if (!StructurallyValid(candidate) || MemorySecretGuard.ContainsSecret(source.Target) ||
                MemorySecretGuard.ContainsSecret(candidate.Content) || candidate.Entities.Any(x =>
                    MemorySecretGuard.ContainsSecret(x.CanonicalName) || MemorySecretGuard.ContainsSecret(x.Mention) ||
                    x.Aliases.Any(MemorySecretGuard.ContainsSecret)) || !PreservesPurchaseModality(source.Target, candidate))
            { counters[6]++; metrics.MemoryAutoSkipped.Add(1); continue; }
            var now = clock.GetUtcNow();
            try
            {
                var applied = await store.WriteAsync(async s => await ApplyCandidateAsync(s, source, input, candidate, now, ct), ct);
                if (applied.Skipped) { counters[6]++; metrics.MemoryAutoSkipped.Add(1); continue; }
                switch (applied.Action)
                {
                    case "create": counters[1]++; metrics.MemoryAutoCreated.Add(1); break;
                    case "reinforce": counters[2]++; metrics.MemoryAutoReinforced.Add(1); break;
                    case "correct": counters[3]++; metrics.MemoryAutoCorrected.Add(1); break;
                    case "transition": counters[4]++; metrics.MemoryAutoTransitioned.Add(1); break;
                }
                counters[5] += applied.GraphMutations;
                counters[6] += applied.GraphSkipped;
            }
            catch (Exception e) when (e is MemoryException or ArgumentException or InvalidOperationException)
            { counters[6]++; metrics.MemoryAutoSkipped.Add(1); }
        }
        metrics.MemoryExtractionCandidates.Record(counters[0]);
        return new(counters[0], counters[1], counters[2], counters[3], counters[4], counters[5], counters[6]);
    }

    private static bool StructurallyValid(MemoryExtractionCandidate c) =>
        c.Entities.Count <= 10 && c.Relations.Count <= 10 && c.Entities.All(x => x.Aliases.Count <= 5) &&
        c.Action is "create" or "reinforce" or "correct" or "transition" &&
        c.Content.Length is > 0 and <= MemoryText.MaxContentLength &&
        (c.ValidFrom is null || c.ValidUntil is null || c.ValidUntil > c.ValidFrom);

    private sealed record Applied(string Action, int GraphMutations = 0, int GraphSkipped = 0, bool Skipped = false);

    private static async Task<Applied> ApplyCandidateAsync(IMemoryStore s, MemoryExtractionSource source,
        MemoryExtractionInput input, MemoryExtractionCandidate c, DateTimeOffset now, CancellationToken ct)
    {
        if (!await s.SourceIsAvailableAsync(source, ct)) return new(c.Action, Skipped: true);
        var oldRef = input.ExistingMemories.FirstOrDefault(x => x.Ref == c.ExistingMemoryRef);
        if (c.Action == "create" && c.ExistingMemoryRef is not null ||
            c.Action != "create" && (oldRef is null || c.ExistingMemoryRef is null)) return new(c.Action, Skipped: true);
        if (c.Relations.Any(x => x.Action is "close" or "forget" or "reinforce" &&
                (x.ExistingRelationRef is null || input.ExistingRelations.All(y => y.Ref != x.ExistingRelationRef))))
            return new(c.Action, Skipped: true);
        var old = oldRef is null ? null : await s.FindRecordAsync(oldRef.Record.Id, ct);
        if (oldRef is not null && old is null) return new(c.Action, Skipped: true);
        var at = c.TransitionAt ?? c.ValidFrom ?? source.ObservedAt;
        if (c.Action == "transition" && (old!.Status != MemoryStatus.Active ||
            old.ValidFrom is not null && at <= old.ValidFrom || old.ValidUntil is not null && old.ValidUntil != at))
            return new(c.Action, Skipped: true);
        if (c.Action == "correct" && old!.Status != MemoryStatus.Active)
        {
            // A previous attempt may already have committed this candidate.
            if (old.Status != MemoryStatus.Superseded || old.SupersededById is not { } replacementId)
                return new(c.Action, Skipped: true);
            var replacement = await s.FindRecordAsync(replacementId, ct);
            if (replacement is null || replacement.ContentHash != MemoryText.Hash(MemoryText.Normalize(c.Content, 2000)))
                return new(c.Action, Skipped: true);
            old = replacement;
        }
        if (c.Action == "reinforce" && (old!.Status != MemoryStatus.Active ||
            old.ContentHash != MemoryText.Hash(MemoryText.Normalize(c.Content, 2000))))
            return new(c.Action, Skipped: true);

        var candidate = new MemoryRecord(c.Content,
            c.Action == "transition" ? at : c.ValidFrom,
            c.ValidUntil, now);
        MemoryRecord record;
        if (c.Action == "reinforce") record = old!;
        else
        {
            var resolved = MemoryTemporal.Resolve(await s.FindActiveByHashAllAsync(candidate.ContentHash, ct),
                candidate, now, c.Action == "create" && c.ValidFrom is null && c.ValidUntil is null);
            record = resolved.Existing ?? resolved.Candidate;
            if (resolved.Existing is null)
            {
                s.Add(record);
                s.Add(new MemoryProjectionJob(MemoryProjectionTarget.Semantic, MemoryAggregateType.MemoryRecord,
                    record.Id, record.Revision, MemoryProjectionOperation.Upsert, now));
            }
        }
        if (c.Action == "correct" && old!.Id != record.Id && old.Status == MemoryStatus.Active)
        {
            old.Supersede(record.Id, now);
            s.Add(new MemoryProjectionJob(MemoryProjectionTarget.Semantic, MemoryAggregateType.MemoryRecord,
                old.Id, old.Revision, MemoryProjectionOperation.Delete, now));
            foreach (var relation in await s.FindRelationsExclusivelySupportedByMemoryAsync(old.Id, now, ct))
                if (relation.Forget(now)) s.Add(new MemoryProjectionJob(MemoryProjectionTarget.Graph,
                    MemoryAggregateType.MemoryRelation, relation.Id, relation.Revision, MemoryProjectionOperation.Delete, now));
        }
        if (c.Action == "transition" && old!.CloseValidity(at, now))
        {
            s.Add(new MemoryProjectionJob(MemoryProjectionTarget.Semantic, MemoryAggregateType.MemoryRecord,
                old.Id, old.Revision, MemoryProjectionOperation.Upsert, now));
            foreach (var relation in await s.FindRelationsExclusivelySupportedByMemoryAsync(old.Id, at, ct))
                if ((relation.ValidFrom is null || relation.ValidFrom < at) && (relation.ValidUntil is null || relation.ValidUntil > at))
                    if (relation.CloseValidity(at, now)) s.Add(new MemoryProjectionJob(MemoryProjectionTarget.Graph,
                        MemoryAggregateType.MemoryRelation, relation.Id, relation.Revision, MemoryProjectionOperation.Upsert, now));
        }
        if (!await s.HasEvidenceAsync(record.Id, MemorySourceKind.UserStatement, source.UserMessageId, ct))
            s.Add(new MemoryEvidence(record.Id, MemorySourceKind.UserStatement, source.ConversationId,
                source.UserMessageId, source.ObservedAt, now));

        if (c.Entities.Count == 0 && c.Relations.Count == 0)
            return new(c.Action);
        var resolutions = new Dictionary<string, MemoryEntity?>();
        foreach (var item in c.Entities)
        {
            if (string.IsNullOrWhiteSpace(item.Key) || resolutions.ContainsKey(item.Key) ||
                !MentionSupported(source.Target, item.Mention, item.CanonicalName)) return new(c.Action, GraphSkipped: c.Relations.Count);
            var normalized = MemoryText.Normalize(item.Mention, 200);
            var candidates = (await s.FindCanonicalEntitiesAsync(normalized, ct))
                .Concat(await s.FindAliasedEntitiesAsync(normalized, ct));
            var canonical = MemoryText.Normalize(item.CanonicalName, 200);
            candidates = candidates.Concat(await s.FindCanonicalEntitiesAsync(canonical, ct))
                .Concat(await s.FindAliasedEntitiesAsync(canonical, ct));
            var type = item.EntityType is null ? null : MemoryText.Normalize(item.EntityType, 40);
            var distinct = candidates.Where(x => x.RetiredAt is null && (type is null || x.EntityType == type))
                .DistinctBy(x => x.Id).ToArray();
            if (distinct.Length > 1) return new(c.Action, GraphSkipped: c.Relations.Count);
            resolutions[item.Key] = distinct.SingleOrDefault();
        }
        var entities = new Dictionary<string, MemoryEntity>();
        foreach (var item in c.Entities)
        {
            var entity = resolutions[item.Key];
            if (entity is null)
            {
                entity = new MemoryEntity(item.CanonicalName, item.EntityType, now);
                s.Add(entity);
                s.Add(new MemoryProjectionJob(MemoryProjectionTarget.Graph, MemoryAggregateType.MemoryEntity,
                    entity.Id, entity.Revision, MemoryProjectionOperation.Upsert, now));
            }
            entities[item.Key] = entity;
            foreach (var alias in item.Aliases)
            {
                if (!AliasSupported(source.Target, alias)) continue;
                var normalized = MemoryText.Normalize(alias, 200);
                if (await s.FindAliasAsync(entity.Id, normalized, ct) is not null) continue;
                s.Add(new MemoryEntityAlias(entity.Id, alias, now));
                entity.AliasChanged(now);
                s.Add(new MemoryProjectionJob(MemoryProjectionTarget.Graph, MemoryAggregateType.MemoryEntity,
                    entity.Id, entity.Revision, MemoryProjectionOperation.Upsert, now));
            }
        }
        var mutations = 0; var skipped = 0;
        foreach (var action in c.Relations)
        {
            var existing = input.ExistingRelations.FirstOrDefault(x => x.Ref == action.ExistingRelationRef);
            if (action.Action is "close" or "forget" or "reinforce")
            {
                if (existing is null) { skipped++; continue; }
                var relation = await s.FindRelationAsync(existing.Relation.Id, ct);
                if (relation is null) { skipped++; continue; }
                if (action.Action == "close")
                {
                    if (relation.CloseValidity(action.CloseAt ?? at, now))
                        s.Add(new MemoryProjectionJob(MemoryProjectionTarget.Graph, MemoryAggregateType.MemoryRelation,
                            relation.Id, relation.Revision, MemoryProjectionOperation.Upsert, now));
                    mutations++;
                }
                else if (action.Action == "forget")
                {
                    if (relation.Forget(now)) s.Add(new MemoryProjectionJob(MemoryProjectionTarget.Graph,
                        MemoryAggregateType.MemoryRelation, relation.Id, relation.Revision, MemoryProjectionOperation.Delete, now));
                    mutations++;
                }
                else if (relation.Status == MemoryStatus.Active)
                {
                    if (!await s.HasRelationEvidenceAsync(relation.Id, record.Id, ct))
                        s.Add(new MemoryRelationEvidence(relation.Id, record.Id, now));
                    mutations++;
                }
                continue;
            }
            if (action.Action != "create" || action.SubjectKey is null || action.ObjectKey is null ||
                !entities.TryGetValue(action.SubjectKey, out var subject) ||
                !entities.TryGetValue(action.ObjectKey, out var objectEntity) || action.Predicate is null)
            { skipped++; continue; }
            var predicate = MemoryText.NormalizePredicate(action.Predicate);
            var from = c.Action == "transition" ? action.ValidFrom ?? at : action.ValidFrom;
            var until = action.ValidUntil;
            var active = await s.FindActiveRelationsAsync(subject.Id, predicate, objectEntity.Id, ct);
            var relationExisting = active.FirstOrDefault(x => x.ValidFrom == from && x.ValidUntil == until);
            if (relationExisting is null && from is null && until is null)
            {
                relationExisting = active.FirstOrDefault(x => MemoryTemporal.IsValid(x.ValidFrom, x.ValidUntil, now));
                if (relationExisting is null && active.Count > 0) from = now;
            }
            if (relationExisting is null && active.Any(x => MemoryTemporal.Overlaps(x.ValidFrom, x.ValidUntil, from, until)))
            { skipped++; continue; }
            var relationNew = relationExisting ?? new MemoryRelation(subject.Id, predicate, objectEntity.Id, from, until, now);
            if (relationExisting is null)
            {
                s.Add(relationNew);
                s.Add(new MemoryProjectionJob(MemoryProjectionTarget.Graph, MemoryAggregateType.MemoryRelation,
                    relationNew.Id, relationNew.Revision, MemoryProjectionOperation.Upsert, now));
            }
            if (!await s.HasRelationEvidenceAsync(relationNew.Id, record.Id, ct))
                s.Add(new MemoryRelationEvidence(relationNew.Id, record.Id, now));
            mutations++;
        }
        return new(c.Action, mutations, skipped);
    }

    private static bool MentionSupported(string target, string mention, string canonical) =>
        target.Contains(mention, StringComparison.OrdinalIgnoreCase) ||
        target.Contains(canonical, StringComparison.OrdinalIgnoreCase) ||
        canonical.Equals("Pedro", StringComparison.OrdinalIgnoreCase) &&
            target.Contains("meu", StringComparison.OrdinalIgnoreCase) ||
        canonical.Equals("Pedro", StringComparison.OrdinalIgnoreCase) &&
            target.Contains("minha", StringComparison.OrdinalIgnoreCase) ||
        canonical.Equals("Pedro", StringComparison.OrdinalIgnoreCase) &&
            target.Contains("eu", StringComparison.OrdinalIgnoreCase) ||
        canonical.Equals("Pedro", StringComparison.OrdinalIgnoreCase) &&
            target.Contains("me chamam", StringComparison.OrdinalIgnoreCase);

    private static bool PreservesPurchaseModality(string target, MemoryExtractionCandidate candidate)
    {
        var consideration = target.Contains("pensando em comprar", StringComparison.OrdinalIgnoreCase) ||
            target.Contains("considerando comprar", StringComparison.OrdinalIgnoreCase) ||
            target.Contains("talvez compre", StringComparison.OrdinalIgnoreCase);
        if (!consideration) return true;
        return candidate.Relations.All(x => !string.Equals(x.Predicate, "OWNS", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(x.Predicate, "USES", StringComparison.OrdinalIgnoreCase)) &&
            !candidate.Content.Contains("possui", StringComparison.OrdinalIgnoreCase) &&
            !candidate.Content.Contains("comprou", StringComparison.OrdinalIgnoreCase) &&
            !candidate.Content.Contains("já tem", StringComparison.OrdinalIgnoreCase);
    }

    private static bool AliasSupported(string target, string alias) =>
        target.Contains(alias, StringComparison.OrdinalIgnoreCase) &&
        (target.Contains("chamam", StringComparison.OrdinalIgnoreCase) ||
         target.Contains("apelido", StringComparison.OrdinalIgnoreCase) ||
         target.Contains("conhecido", StringComparison.OrdinalIgnoreCase));
}
