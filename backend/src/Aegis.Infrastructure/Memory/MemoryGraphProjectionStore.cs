using Aegis.Application.Memory;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Aegis.Infrastructure.Memory;

public sealed class MemoryGraphProjectionStore(AegisDbContext db, TimeProvider clock) : IMemoryGraphProjectionStore
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
              WHERE "ProjectionTarget" = 'Graph' AND "AggregateType" IN ('MemoryEntity', 'MemoryRelation')
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
        Add(command, "now", now); Add(command, "expires", now.Add(lease)); Add(command, "leaseId", leaseId);
        var result = await command.ExecuteScalarAsync(ct);
        await tx.CommitAsync(ct);
        return result is Guid id ? await db.MemoryProjectionJobs.AsNoTracking().SingleAsync(x => x.Id == id, ct) : null;
    }

    public async Task<bool> ReconcileClaimAsync(MemoryProjectionJob claim,
        Func<MemoryGraphProjectionState, CancellationToken, Task> reconcile, CancellationToken ct)
    {
        if (claim.ProjectionTarget != MemoryProjectionTarget.Graph ||
            claim.AggregateType is not (MemoryAggregateType.MemoryEntity or MemoryAggregateType.MemoryRelation)) return false;
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(6081, {BitConverter.ToInt32(claim.AggregateId.ToByteArray(), 0)})", ct);
        var jobs = await db.MemoryProjectionJobs.FromSqlInterpolated($"SELECT * FROM memory_projection_jobs WHERE \"Id\" = {claim.Id} FOR UPDATE").ToListAsync(ct);
        var job = jobs.SingleOrDefault();
        if (job is null || job.Status != MemoryProjectionStatus.Processing || job.LeaseId != claim.LeaseId)
        { await tx.RollbackAsync(ct); return false; }
        MemoryGraphProjectionState state;
        if (claim.AggregateType == MemoryAggregateType.MemoryEntity)
        {
            var entities = await db.MemoryEntities.FromSqlInterpolated($"SELECT * FROM memory_entities WHERE \"Id\" = {claim.AggregateId} FOR UPDATE").ToListAsync(ct);
            state = new(await ProjectEntityAsync(entities.SingleOrDefault(), ct), null, null, null);
        }
        else
        {
            var relations = await db.MemoryRelations.FromSqlInterpolated($"SELECT * FROM memory_relations WHERE \"Id\" = {claim.AggregateId} FOR UPDATE").ToListAsync(ct);
            var relation = relations.SingleOrDefault();
            if (relation?.Status == MemoryStatus.Active)
            {
                var endpoints = await db.MemoryEntities.AsNoTracking().Where(x => x.Id == relation.SubjectEntityId || x.Id == relation.ObjectEntityId).ToListAsync(ct);
                var subject = await ProjectEntityAsync(endpoints.SingleOrDefault(x => x.Id == relation.SubjectEntityId), ct);
                var @object = await ProjectEntityAsync(endpoints.SingleOrDefault(x => x.Id == relation.ObjectEntityId), ct);
                state = new(null, new(relation.Id, relation.SubjectEntityId, relation.Predicate, relation.ObjectEntityId,
                    relation.Revision, relation.ValidFrom, relation.ValidUntil), subject, @object);
            }
            else state = new(null, null, null, null);
        }
        await reconcile(state, ct);
        job.Complete(claim.LeaseId!.Value, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<bool> FailAsync(MemoryProjectionJob claim, string code, DateTimeOffset now, TimeSpan? retryDelay, CancellationToken ct)
    {
        if (claim.ProjectionTarget != MemoryProjectionTarget.Graph ||
            claim.AggregateType is not (MemoryAggregateType.MemoryEntity or MemoryAggregateType.MemoryRelation)) return false;
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var jobs = await db.MemoryProjectionJobs.FromSqlInterpolated($"SELECT * FROM memory_projection_jobs WHERE \"Id\" = {claim.Id} FOR UPDATE").ToListAsync(ct);
        var changed = jobs.SingleOrDefault()?.Fail(claim.LeaseId!.Value, code, now, retryDelay) ?? false;
        if (changed) await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return changed;
    }

    public async Task<int> RequeueCurrentStateAsync(DateTimeOffset now, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (db.Database.IsNpgsql()) await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(6080, 1)", ct);
        var entities = await db.MemoryEntities.AsNoTracking().Select(x => new { x.Id, x.Revision }).ToListAsync(ct);
        var relations = await db.MemoryRelations.AsNoTracking().Select(x => new { x.Id, x.Revision, x.Status }).ToListAsync(ct);
        var jobs = await db.MemoryProjectionJobs.Where(x => x.ProjectionTarget == MemoryProjectionTarget.Graph &&
            (x.AggregateType == MemoryAggregateType.MemoryEntity || x.AggregateType == MemoryAggregateType.MemoryRelation)).ToListAsync(ct);
        var byKey = jobs.ToDictionary(x => (x.AggregateType, x.AggregateId, x.AggregateRevision, x.Operation));
        var count = 0;
        foreach (var entity in entities)
            count += Requeue(MemoryAggregateType.MemoryEntity, entity.Id, entity.Revision, MemoryProjectionOperation.Upsert);
        foreach (var relation in relations)
            count += Requeue(MemoryAggregateType.MemoryRelation, relation.Id, relation.Revision,
                relation.Status == MemoryStatus.Active ? MemoryProjectionOperation.Upsert : MemoryProjectionOperation.Delete);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return count;

        int Requeue(MemoryAggregateType type, Guid id, int revision, MemoryProjectionOperation operation)
        {
            if (byKey.TryGetValue((type, id, revision, operation), out var job))
            {
                if (job.Status == MemoryProjectionStatus.Pending ||
                    job.Status == MemoryProjectionStatus.Processing && job.LeaseExpiresAt > now) return 0;
                job.Requeue(now);
            }
            else db.MemoryProjectionJobs.Add(new MemoryProjectionJob(MemoryProjectionTarget.Graph, type, id, revision, operation, now));
            return 1;
        }
    }

    public async Task<(IReadOnlyList<MemoryEntity> Entities, IReadOnlyList<MemoryRelation> Relations)> LoadCandidatesAsync(
        IReadOnlyList<Guid> entityIds, IReadOnlyList<Guid> relationIds, CancellationToken ct)
    {
        var entities = await db.MemoryEntities.AsNoTracking().Where(x => entityIds.Contains(x.Id)).ToListAsync(ct);
        var relations = await db.MemoryRelations.AsNoTracking().Where(x => relationIds.Contains(x.Id)).ToListAsync(ct);
        return (entities, relations);
    }

    private async Task<MemoryGraphEntityProjection?> ProjectEntityAsync(MemoryEntity? entity, CancellationToken ct)
    {
        if (entity is null) return null;
        var aliases = await db.MemoryEntityAliases.AsNoTracking().Where(x => x.EntityId == entity.Id)
            .OrderBy(x => x.NormalizedAlias).Select(x => x.Alias).ToListAsync(ct);
        return new(entity.Id, entity.CanonicalName, entity.NormalizedName, entity.EntityType, aliases, entity.Revision, entity.RetiredAt);
    }

    private static void Add(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value; command.Parameters.Add(parameter);
    }
}
