using Aegis.Application.Memory;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Aegis.Infrastructure.Memory;

public sealed class MemoryExtractionJobStore(AegisDbContext db) : IMemoryExtractionJobStore
{
    public async Task<MemoryExtractionJob?> ClaimAsync(DateTimeOffset now, TimeSpan lease, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var leaseId = Guid.NewGuid();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE memory_extraction_jobs SET "Status" = 'Failed', "LastError" = 'memory_extraction_lease_exhausted',
              "LeaseId" = NULL, "LeaseExpiresAt" = NULL, "UpdatedAt" = {now}
            WHERE "Status" = 'Processing' AND "LeaseExpiresAt" <= {now} AND "Attempt" >= 6
            """, ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = tx.GetDbTransaction();
        command.CommandText = """
            WITH candidate AS (
              SELECT "Id" FROM memory_extraction_jobs
              WHERE ("Status" = 'Pending' AND "Attempt" < 6 AND ("NextAttemptAt" IS NULL OR "NextAttemptAt" <= @now))
                 OR ("Status" = 'Processing' AND "Attempt" < 6 AND "LeaseExpiresAt" <= @now)
              ORDER BY "CreatedAt", "Id" FOR UPDATE SKIP LOCKED LIMIT 1
            )
            UPDATE memory_extraction_jobs AS job SET
              "Status" = 'Processing', "Attempt" = job."Attempt" + 1,
              "ProcessingStartedAt" = @now, "LeaseExpiresAt" = @expires,
              "LeaseId" = @leaseId, "NextAttemptAt" = NULL, "LastError" = NULL,
              "CompletedAt" = NULL, "UpdatedAt" = @now
            WHERE job."Id" = (SELECT "Id" FROM candidate) RETURNING job."Id"
            """;
        Add(command, "now", now); Add(command, "expires", now.Add(lease)); Add(command, "leaseId", leaseId);
        var result = await command.ExecuteScalarAsync(ct);
        await tx.CommitAsync(ct);
        return result is Guid id ? await db.MemoryExtractionJobs.AsNoTracking().SingleAsync(x => x.Id == id, ct) : null;
    }

    public async Task<MemoryExtractionSource?> ReadSourceAsync(MemoryExtractionJob job, CancellationToken ct)
    {
        if (job.ConversationId is not { } conversationId || job.UserMessageId is not { } messageId) return null;
        var conversation = await db.Conversations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == conversationId, ct);
        if (conversation is null || conversation.DeletedAt is not null) return null;
        var target = await db.ChatMessages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == messageId &&
            x.ConversationId == conversationId && x.Role == "user", ct);
        if (target is null) return null;
        var recent = await db.ChatMessages.AsNoTracking().Where(x => x.ConversationId == conversationId &&
                x.CreatedAt < target.CreatedAt &&
                (x.Role == "user" || x.Role == "assistant"))
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(6)
            .Select(x => new { x.Role, x.Content }).ToListAsync(ct);
        return new(conversationId, messageId, target.Content[..Math.Min(target.Content.Length, 12000)], target.CreatedAt,
            recent.AsEnumerable().Reverse().Select(x => new MemoryRecentMessage(x.Role,
                x.Content[..Math.Min(x.Content.Length, 800)])).ToArray());
    }

    public Task<bool> CompleteAsync(MemoryExtractionJob job, MemoryExtractionSummary summary, DateTimeOffset now, CancellationToken ct) =>
        UpdateAsync(job, x => x.Complete(job.LeaseId!.Value, now, summary.Candidates, summary.Created,
            summary.Reinforced, summary.Corrected, summary.Transitioned, summary.GraphMutations, summary.Skipped), ct);

    public Task<bool> FailAsync(MemoryExtractionJob job, string code, DateTimeOffset now, TimeSpan? delay, CancellationToken ct) =>
        UpdateAsync(job, x => x.Fail(job.LeaseId!.Value, code, now, delay), ct);

    private async Task<bool> UpdateAsync(MemoryExtractionJob claim, Func<MemoryExtractionJob, bool> update, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.MemoryExtractionJobs.FromSqlInterpolated(
            $"SELECT * FROM memory_extraction_jobs WHERE \"Id\" = {claim.Id} FOR UPDATE").ToListAsync(ct);
        var changed = rows.SingleOrDefault() is { } current && update(current);
        if (changed) await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return changed;
    }

    private static void Add(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value; command.Parameters.Add(parameter);
    }
}
