using Aegis.Application.Memory;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Aegis.Infrastructure.Memory;

public sealed class MemorySemanticProjectionStore(AegisDbContext db, TimeProvider clock) : IMemorySemanticProjectionStore
{
    public async Task<MemoryProjectionJob?> ClaimAsync(DateTimeOffset now, TimeSpan lease, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var leaseId = Guid.NewGuid();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = tx.GetDbTransaction();
        command.CommandText = """
            WITH candidate AS (
              SELECT "Id" FROM memory_projection_jobs
              WHERE "ProjectionTarget" = 'Semantic' AND "AggregateType" = 'MemoryRecord'
                AND (("Status" = 'Pending' AND ("NextAttemptAt" IS NULL OR "NextAttemptAt" <= @now))
                  OR ("Status" = 'Processing' AND "LeaseExpiresAt" <= @now))
              ORDER BY "CreatedAt", "Id" FOR UPDATE SKIP LOCKED LIMIT 1
            )
            UPDATE memory_projection_jobs AS job SET
              "Status" = 'Processing', "Attempt" = job."Attempt" + 1,
              "ProcessingStartedAt" = @now, "LeaseExpiresAt" = @expires,
              "LeaseId" = @leaseId, "NextAttemptAt" = NULL, "LastError" = NULL,
              "CompletedAt" = NULL, "UpdatedAt" = @now
            WHERE job."Id" = (SELECT "Id" FROM candidate) RETURNING job."Id"
            """;
        Add(command, "now", now);
        Add(command, "expires", now.Add(lease));
        Add(command, "leaseId", leaseId);
        var result = await command.ExecuteScalarAsync(ct);
        await tx.CommitAsync(ct);
        if (result is not Guid id) return null;
        return await db.MemoryProjectionJobs.AsNoTracking().SingleAsync(x => x.Id == id, ct);
    }

    public async Task<bool> ReconcileClaimAsync(MemoryProjectionJob claim, Func<MemoryRecord?, CancellationToken, Task> reconcile, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // One aggregate at a time across processes, including an old Upsert and a newer Delete.
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(6080, {BitConverter.ToInt32(claim.AggregateId.ToByteArray(), 0)})", ct);
        var jobs = await db.MemoryProjectionJobs.FromSqlInterpolated($"SELECT * FROM memory_projection_jobs WHERE \"Id\" = {claim.Id} FOR UPDATE").ToListAsync(ct);
        var job = jobs.SingleOrDefault();
        if (job is null || job.Status != MemoryProjectionStatus.Processing || job.LeaseId != claim.LeaseId)
        {
            await tx.RollbackAsync(ct);
            return false;
        }
        // The row lock serializes canonical forget/supersession with Qdrant I/O. A stale point is
        // still filtered at search time if it was present before the PostgreSQL write committed.
        var records = await db.MemoryRecords.FromSqlInterpolated($"SELECT * FROM memory_records WHERE \"Id\" = {claim.AggregateId} FOR UPDATE").ToListAsync(ct);
        await reconcile(records.SingleOrDefault(), ct);
        job.Complete(claim.LeaseId!.Value, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<bool> FailAsync(MemoryProjectionJob claim, string code, DateTimeOffset now, TimeSpan? retryDelay, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var jobs = await db.MemoryProjectionJobs.FromSqlInterpolated($"SELECT * FROM memory_projection_jobs WHERE \"Id\" = {claim.Id} FOR UPDATE").ToListAsync(ct);
        var changed = jobs.SingleOrDefault()?.Fail(claim.LeaseId!.Value, code, now, retryDelay) ?? false;
        if (changed) await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return changed;
    }

    // Run after collection validation on every worker start and again after collection loss.
    // Reopening the current unique job preserves the existing idempotency constraint.
    public async Task<int> RequeueActiveAsync(DateTimeOffset now, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (db.Database.IsNpgsql()) await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(6080, 1)", ct);
        var records = await db.MemoryRecords.AsNoTracking().Where(x => x.Status == MemoryStatus.Active)
            .Select(x => new { x.Id, x.Revision }).ToListAsync(ct);
        var jobs = await db.MemoryProjectionJobs.Where(x => x.ProjectionTarget == MemoryProjectionTarget.Semantic &&
            x.AggregateType == MemoryAggregateType.MemoryRecord && x.Operation == MemoryProjectionOperation.Upsert).ToListAsync(ct);
        var byKey = jobs.ToDictionary(x => (x.AggregateId, x.AggregateRevision));
        var count = 0;
        foreach (var record in records)
        {
            if (byKey.TryGetValue((record.Id, record.Revision), out var job))
            {
                if (job.Status == MemoryProjectionStatus.Pending && (job.NextAttemptAt is null || job.NextAttemptAt <= now) ||
                    job.Status == MemoryProjectionStatus.Processing && job.LeaseExpiresAt > now) continue;
                job.Requeue(now);
            }
            else db.MemoryProjectionJobs.Add(new MemoryProjectionJob(MemoryProjectionTarget.Semantic, MemoryAggregateType.MemoryRecord,
                record.Id, record.Revision, MemoryProjectionOperation.Upsert, now));
            count++;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return count;
    }

    private static void Add(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
