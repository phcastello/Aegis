using Aegis.Application.Memory;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aegis.Infrastructure.Memory;

public sealed class MemoryActivityStore(AegisDbContext db) : IMemoryActivityStore
{
    private static readonly MemoryActivityKind[] Order =
        [MemoryActivityKind.Used, MemoryActivityKind.Consulted, MemoryActivityKind.Created,
            MemoryActivityKind.Updated, MemoryActivityKind.Deleted];

    public async Task SaveResponseWithUsedAsync(IReadOnlyList<MemoryActivityEvent> used, CancellationToken ct)
    {
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        await db.SaveChangesAsync(ct);
        foreach (var item in used) await RecordAsync(item, ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
    }

    public async Task RecordAsync(MemoryActivityEvent activity, CancellationToken ct)
    {
        if (!db.Database.IsRelational())
        {
            if (!await db.MemoryActivityEvents.AnyAsync(x => x.DedupeKey == activity.DedupeKey, ct))
            {
                db.MemoryActivityEvents.Add(activity);
                await db.SaveChangesAsync(ct);
            }
            return;
        }
        // Shares the caller's canonical transaction. A retry can never abort it on a duplicate key.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO memory_activity_events
                ("Id", "ConversationId", "UserMessageId", "Kind", "Source", "TargetType",
                 "TargetId", "DedupeKey", "OccurredAt")
            VALUES ({activity.Id}, {activity.ConversationId}, {activity.UserMessageId},
                    {activity.Kind.ToString()}, {activity.Source.ToString()}, {activity.TargetType.ToString()},
                    {activity.TargetId}, {activity.DedupeKey}, {activity.OccurredAt})
            ON CONFLICT ("DedupeKey") DO NOTHING
            """, ct);
    }

    public async Task<IReadOnlyDictionary<Guid, MemoryActivitySnapshot>> LoadForAssistantMessagesAsync(
        IReadOnlyList<Guid> assistantMessageIds, CancellationToken ct)
    {
        if (assistantMessageIds.Count == 0) return new Dictionary<Guid, MemoryActivitySnapshot>();
        var ids = assistantMessageIds.Distinct().ToArray();
        var turns = await db.LlmRequestAudits.AsNoTracking()
            .Where(x => x.Success && x.AssistantMessageId != null && ids.Contains(x.AssistantMessageId.Value))
            .Select(x => new { AssistantId = x.AssistantMessageId!.Value, x.UserMessageId })
            .ToListAsync(ct);
        if (turns.Count == 0) return new Dictionary<Guid, MemoryActivitySnapshot>();
        var userIds = turns.Select(x => x.UserMessageId).Distinct().ToArray();
        var events = await db.MemoryActivityEvents.AsNoTracking()
            .Where(x => x.UserMessageId != null && userIds.Contains(x.UserMessageId.Value))
            .OrderBy(x => x.OccurredAt).ThenBy(x => x.Id).ToListAsync(ct);
        var pendingIds = await db.MemoryExtractionJobs.AsNoTracking()
            .Where(x => x.UserMessageId != null && userIds.Contains(x.UserMessageId.Value) &&
                (x.Status == MemoryExtractionStatus.Pending || x.Status == MemoryExtractionStatus.Processing))
            .Select(x => x.UserMessageId!.Value).ToListAsync(ct);
        var pending = pendingIds.ToHashSet();
        var recordIds = events.Where(x => x.TargetType == MemoryActivityTargetType.MemoryRecord)
            .Select(x => x.TargetId).Distinct().ToArray();
        var relationIds = events.Where(x => x.TargetType == MemoryActivityTargetType.MemoryRelation)
            .Select(x => x.TargetId).Distinct().ToArray();
        var records = await db.MemoryRecords.AsNoTracking().Where(x => recordIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Content }).ToDictionaryAsync(x => x.Id, x => x.Content, ct);
        var supporting = await db.MemoryRelationEvidences.AsNoTracking()
            .Where(x => relationIds.Contains(x.RelationId))
            .Join(db.MemoryRecords.AsNoTracking(), x => x.MemoryId, x => x.Id,
                (evidence, record) => new { evidence.RelationId, record.Content })
            .ToListAsync(ct);
        var support = supporting.GroupBy(x => x.RelationId)
            .ToDictionary(x => x.Key, x => x.Select(y => y.Content).FirstOrDefault(y => !string.IsNullOrWhiteSpace(y)));
        var relations = await db.MemoryRelations.AsNoTracking().Where(x => relationIds.Contains(x.Id))
            .Select(x => new { x.Id, x.SubjectEntityId, x.ObjectEntityId, x.Predicate }).ToListAsync(ct);
        var entityIds = relations.SelectMany(x => new[] { x.SubjectEntityId, x.ObjectEntityId }).Distinct().ToArray();
        var names = await db.MemoryEntities.AsNoTracking().Where(x => entityIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.CanonicalName, ct);
        var relationText = relations.ToDictionary(x => x.Id, x =>
            support.GetValueOrDefault(x.Id) ??
            (names.TryGetValue(x.SubjectEntityId, out var subject) && names.TryGetValue(x.ObjectEntityId, out var obj)
                ? $"{subject} — {x.Predicate} → {obj}" : null));
        var byUser = events.GroupBy(x => x.UserMessageId!.Value).ToDictionary(x => x.Key, x => x.ToArray());
        var result = new Dictionary<Guid, MemoryActivitySnapshot>();
        foreach (var turn in turns)
        {
            var activity = byUser.GetValueOrDefault(turn.UserMessageId) ?? [];
            var sections = new List<MemoryActivitySection>();
            foreach (var kind in Order)
            {
                var texts = activity.Where(x => x.Kind == kind).Select(x =>
                    x.TargetType == MemoryActivityTargetType.MemoryRecord
                        ? records.GetValueOrDefault(x.TargetId)
                        : relationText.GetValueOrDefault(x.TargetId))
                    .Select(x => string.IsNullOrWhiteSpace(x) && kind == MemoryActivityKind.Deleted
                        ? "Memória apagada" : x)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.Ordinal).ToArray();
                if (texts.Length > 0)
                    sections.Add(new MemoryActivitySection(kind.ToString().ToLowerInvariant(),
                        texts.Take(3).Select(x => x!).ToArray(), texts.Length));
            }
            if (sections.Count > 0 || pending.Contains(turn.UserMessageId))
                result[turn.AssistantId] = new MemoryActivitySnapshot(pending.Contains(turn.UserMessageId), sections);
        }
        return result;
    }
}
