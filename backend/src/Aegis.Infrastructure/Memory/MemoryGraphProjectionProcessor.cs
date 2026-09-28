using Aegis.Application.Memory;
using Aegis.Application.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aegis.Infrastructure.Memory;

public sealed class MemoryGraphProjectionProcessor(IMemoryGraphProjectionStore jobs, IMemoryGraphStore graph,
    TimeProvider clock, AegisMetrics metrics)
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan[] Delays = [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30)];

    public async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        var claim = await jobs.ClaimAsync(clock.GetUtcNow(), Lease, ct);
        if (claim is null) return false;
        try
        {
            var completed = await jobs.ReconcileClaimAsync(claim, async (state, token) =>
            {
                switch (claim.AggregateType)
                {
                    case Aegis.Domain.Entities.MemoryAggregateType.MemoryEntity:
                        if (state.Entity is null) await graph.DeleteEntityAsync(claim.AggregateId, token);
                        else if (!SameEntity(await graph.GetEntityProjectionAsync(claim.AggregateId, token), state.Entity))
                            await graph.UpsertEntityAsync(state.Entity, token);
                        break;
                    case Aegis.Domain.Entities.MemoryAggregateType.MemoryRelation:
                        if (state.Relation is null) await graph.DeleteRelationAsync(claim.AggregateId, token);
                        else
                        {
                            if (state.Subject is null || state.Object is null)
                                throw new MemoryGraphException("graph_endpoint_missing", false);
                            if (!SameEntity(await graph.GetEntityProjectionAsync(state.Subject.EntityId, token), state.Subject))
                                await graph.UpsertEntityAsync(state.Subject, token);
                            if (!SameEntity(await graph.GetEntityProjectionAsync(state.Object.EntityId, token), state.Object))
                                await graph.UpsertEntityAsync(state.Object, token);
                            if (await graph.GetRelationProjectionAsync(claim.AggregateId, token) != state.Relation)
                                await graph.UpsertRelationAsync(state.Relation, token);
                        }
                        break;
                    default: throw new MemoryGraphException("graph_aggregate_invalid", false);
                }
            }, ct);
            if (completed)
            {
                metrics.MemoryGraphProjectionCompleted.Add(1);
                metrics.MemoryGraphProjectionLag.Record(Math.Max(0, (clock.GetUtcNow() - claim.CreatedAt).TotalMilliseconds));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            var code = error is MemoryGraphException known ? known.Code : "neo4j_query_failed";
            var retryable = error is not MemoryGraphException graphError || graphError.Retryable;
            var delay = retryable && claim.Attempt <= Delays.Length ? Delays[claim.Attempt - 1] : (TimeSpan?)null;
            if (await jobs.FailAsync(claim, code, clock.GetUtcNow(), delay, ct))
            {
                if (delay is null) metrics.MemoryGraphProjectionFailed.Add(1);
                else metrics.MemoryGraphProjectionRetries.Add(1);
            }
        }
        return true;
    }

    private static bool SameEntity(MemoryGraphEntityProjection? current, MemoryGraphEntityProjection desired) =>
        current is not null && current.EntityId == desired.EntityId && current.CanonicalName == desired.CanonicalName &&
        current.NormalizedName == desired.NormalizedName && current.EntityType == desired.EntityType &&
        current.Revision == desired.Revision && current.RetiredAt == desired.RetiredAt &&
        current.Aliases.SequenceEqual(desired.Aliases);
}

public sealed class MemoryGraphProjectionWorker(IServiceScopeFactory scopes, IMemoryGraphStore graph,
    MemoryGraphOptions options, TimeProvider clock, ILogger<MemoryGraphProjectionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        var initialized = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!initialized)
                {
                    await graph.EnsureSchemaAsync(stoppingToken);
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<IMemoryGraphProjectionStore>()
                        .RequeueCurrentStateAsync(clock.GetUtcNow(), stoppingToken);
                    initialized = true;
                }
                for (var i = 0; i < 20; i++)
                {
                    await using var scope = scopes.CreateAsyncScope();
                    if (!await scope.ServiceProvider.GetRequiredService<MemoryGraphProjectionProcessor>().ProcessNextAsync(stoppingToken)) break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                logger.LogWarning("Graph memory projection paused: {Category}",
                    error is MemoryGraphException known ? known.Code : "neo4j_unavailable");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(options.WorkerPollSeconds), clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}

public sealed class MemoryGraphRebuild(IMemoryGraphStore graph, IMemoryGraphProjectionStore jobs, TimeProvider clock)
{
    // Explicit maintenance operation; never invoked implicitly during normal startup.
    public async Task<int> RebuildAsync(CancellationToken ct)
    {
        await graph.EnsureSchemaAsync(ct);
        await graph.DeleteManagedProjectionAsync(ct);
        return await jobs.RequeueCurrentStateAsync(clock.GetUtcNow(), ct);
    }
}
