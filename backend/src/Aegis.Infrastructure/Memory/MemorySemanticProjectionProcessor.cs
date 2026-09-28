using Aegis.Application.Memory;
using Aegis.Application.Observability;
using Aegis.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aegis.Infrastructure.Memory;

public sealed class MemorySemanticProjectionProcessor(IMemorySemanticProjectionStore jobs, IMemoryEmbeddingClient embeddings,
    IMemoryVectorStore vectors, MemorySemanticOptions options, TimeProvider clock, AegisMetrics metrics)
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
            var completed = await jobs.ReconcileClaimAsync(claim, async (record, token) =>
            {
                if (record is null || record.Status != MemoryStatus.Active)
                {
                    await vectors.DeleteAsync(claim.AggregateId, token);
                    return;
                }
                var desired = new MemoryVectorPoint(record.Id, record.Revision, record.ContentHash, options.EmbeddingModel, options.EmbeddingDimensions);
                var existing = await vectors.GetPointAsync(record.Id, token);
                if (existing == desired) return;
                var vector = await embeddings.EmbedAsync(record.Content, token);
                MemoryVectorValidation.Validate(vector, options.EmbeddingDimensions);
                await vectors.UpsertAsync(desired, vector, token);
            }, ct);
            if (completed)
            {
                metrics.MemorySemanticProjectionCompleted.Add(1);
                metrics.MemorySemanticProjectionLag.Record(Math.Max(0, (clock.GetUtcNow() - claim.CreatedAt).TotalMilliseconds));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            // Provider exception bodies can contain memory text or credentials. Persist only a small category.
            var category = error is MemorySemanticException known ? known.Code : "semantic_projection_error";
            var retryable = error is not MemorySemanticException semantic || semantic.Retryable;
            var delay = retryable && claim.Attempt <= Delays.Length ? Delays[claim.Attempt - 1] : (TimeSpan?)null;
            if (await jobs.FailAsync(claim, category, clock.GetUtcNow(), delay, ct))
            {
                if (delay is null) metrics.MemorySemanticProjectionFailed.Add(1);
                else metrics.MemorySemanticProjectionRetries.Add(1);
            }
        }
        return true;
    }
}

public sealed class MemorySemanticProjectionWorker(IServiceScopeFactory scopes, IMemoryVectorStore vectors,
    MemorySemanticOptions options, MemoryReconcileOptions reconcileOptions, TimeProvider clock,
    ILogger<MemorySemanticProjectionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        var initialized = false;
        var nextReconcile = DateTimeOffset.MaxValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var created = await vectors.EnsureCollectionAsync(stoppingToken);
                if (created || !initialized || reconcileOptions.IntervalMinutes > 0 && clock.GetUtcNow() >= nextReconcile)
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<IMemorySemanticProjectionStore>()
                        .RequeueCurrentStateAsync(clock.GetUtcNow(), stoppingToken);
                    initialized = true;
                    nextReconcile = clock.GetUtcNow().AddMinutes(Math.Max(1, reconcileOptions.IntervalMinutes));
                }
                for (var i = 0; i < 20; i++)
                {
                    await using var scope = scopes.CreateAsyncScope();
                    if (!await scope.ServiceProvider.GetRequiredService<MemorySemanticProjectionProcessor>().ProcessNextAsync(stoppingToken)) break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                var category = e is MemorySemanticException known ? known.Code : "semantic_worker_error";
                logger.LogWarning("Semantic memory projection paused: {Category}", category);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(options.WorkerPollSeconds), clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}
