using Aegis.Application.Memory;
using Aegis.Application.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aegis.Infrastructure.Memory;

public sealed class MemoryExtractionWorker(IServiceScopeFactory scopes, MemoryAutomaticOptions options,
    TimeProvider clock, AegisMetrics metrics, ILogger<MemoryExtractionWorker> logger) : BackgroundService
{
    private static readonly TimeSpan[] Delays = [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30)];

    public async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IMemoryExtractionJobStore>();
        var claim = await jobs.ClaimAsync(clock.GetUtcNow(), TimeSpan.FromMinutes(2), ct);
        if (claim is null) return false;
        try
        {
            var source = await jobs.ReadSourceAsync(claim, ct);
            if (source is null || MemorySecretGuard.ContainsSecret(source.Target))
            {
                await jobs.CompleteAsync(claim, new(0, 0, 0, 0, 0, 0, 0), clock.GetUtcNow(), ct);
                metrics.MemoryExtractionJobsCompleted.Add(1);
                return true;
            }
            var ingestion = scope.ServiceProvider.GetRequiredService<MemoryAutomaticIngestionService>();
            var input = await ingestion.BuildInputAsync(source, ct);
            // Claim has already committed. No PostgreSQL transaction remains open during model I/O.
            var output = await scope.ServiceProvider.GetRequiredService<IMemoryExtractionClient>().ExtractAsync(input, ct);
            var summary = await ingestion.ApplyAsync(source, input, output, ct);
            if (await jobs.CompleteAsync(claim, summary, clock.GetUtcNow(), ct))
                metrics.MemoryExtractionJobsCompleted.Add(1);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            var code = e is MemoryExtractionException known ? known.Code : "memory_extraction_unavailable";
            var retryable = e is not MemoryExtractionException knownFailure || knownFailure.Retryable;
            var delay = retryable && claim.Attempt <= Delays.Length ? Delays[claim.Attempt - 1] : (TimeSpan?)null;
            if (await jobs.FailAsync(claim, code, clock.GetUtcNow(), delay, ct))
            {
                if (delay is null) metrics.MemoryExtractionJobsFailed.Add(1);
                else metrics.MemoryExtractionRetries.Add(1);
            }
            logger.LogWarning("Memory extraction failed: {Category}", code);
        }
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                for (var i = 0; i < 20; i++) if (!await ProcessNextAsync(stoppingToken)) break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception) { logger.LogWarning("Memory extraction worker paused: memory_extraction_unavailable"); }
            try { await Task.Delay(TimeSpan.FromSeconds(options.PollSeconds), clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}
