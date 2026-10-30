using Aegis.Application.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
namespace Aegis.Infrastructure.Proactivity;

public sealed class ReminderInterventionWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<ReminderInterventionWorker> logger) : BackgroundService
{
    public async Task ReconcileAsync(CancellationToken ct) {
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ReminderInterventionStore>();
        foreach (var batch in (await store.ReconcileAsync(ct)).GroupBy(c => c.NodeId)) {
            string result;
            try { result = await scope.ServiceProvider.GetRequiredService<INodeDomainSignals>().SendAsync(batch.Key, "geofence.monitor", new { operation = "sync" }, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { result = "unconfirmed"; }
            foreach (var claim in batch) await store.RecoveryCompletedAsync(claim, result, ct);
        }
    }
    public async Task<bool> ProcessNextAsync(CancellationToken ct) {
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ReminderInterventionStore>();
        var claim = await store.ClaimAsync(ct);
        if (claim is null) return false;
        try {
            if (!await store.RevalidateAsync(claim, ct)) return true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var result = await scope.ServiceProvider.GetRequiredService<IProactiveMessageGenerator>().GenerateAsync(claim, timeout.Token);
            await store.CompleteAsync(claim, result, ct);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) { await store.FailAsync(claim, ct, (error as Aegis.Application.Llm.LlmRequestException)?.AuditData); logger.LogWarning("Reminder intervention generation deferred: model_unavailable"); }
        return true;
    }
    protected override async Task ExecuteAsync(CancellationToken ct) {
        // Reliability never waits for the language model. Each has an independent loop.
        await Task.WhenAll(Loop(ReconcileAsync, ct), Loop(async token => { for (var i = 0; i < 10; i++) if (!await ProcessNextAsync(token)) break; }, ct));
    }
    private async Task Loop(Func<CancellationToken,Task> work, CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            try { await work(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch { logger.LogWarning("Reminder intervention processing deferred; persisted work will recover."); }
            try { await Task.Delay(ReminderReliabilityPolicy.ScanInterval, clock, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
    }
}
