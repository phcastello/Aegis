using Aegis.Application.Nodes;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
namespace Aegis.Infrastructure.Proactivity;

public sealed class ProactiveNotificationProcessor(AegisDbContext db, ReminderInterventionStore transactions,
    INodeRegistry nodes, INodeNotificationDispatcher dispatcher, TimeProvider clock)
{
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)];
    public async Task<bool> ProcessNextAsync(CancellationToken ct) {
        // Read eligible administrative capabilities, including offline devices. No UI visibility filter.
        var inventory = await nodes.ListForDeliveryAsync(ct);
        var claim = await transactions.Locked<ProactiveNotification?>(async () => {
            var now = clock.GetUtcNow();
            var item = await db.Set<ProactiveNotification>().Where(x => (x.State == "pending" || x.State == "sending" && x.LeaseUntil <= now) && x.NextAttemptAt <= now)
                .OrderBy(x => x.NextAttemptAt).FirstOrDefaultAsync(ct);
            if (item is null) return null;
            if (item.CreatedAt.AddHours(24) <= now) { item.State = "expired"; return item; }
            if (item.NodeId is null) {
                item.Attempts++;
                var eligible = inventory.Where(n => n.Enabled && n.RevokedAt is null && n.Capabilities.Any(c => c.Name == NotificationContract.ConversationCapability && c.Version >= 1)).ToArray();
                foreach (var node in eligible) db.Set<ProactiveNotification>().Add(new() { MessageId = item.MessageId, ConversationId = item.ConversationId, NodeId = node.Id, CreatedAt = now, NextAttemptAt = now });
                item.LastResult = eligible.Length == 0 ? "no_eligible_device" : "recipients_recorded";
                item.State = eligible.Length == 0 ? "pending" : "expanded"; item.NextAttemptAt = now.AddMinutes(5); return item;
            }
            if (item.Attempts >= 5) { item.State = "failed"; return item; }
            item.State = "sending"; item.Attempts++; item.LeaseToken = Guid.NewGuid(); item.LeaseUntil = now.AddMinutes(2);
            return item;
        }, ct);
        if (claim is null) return false;
        if (claim.State != "sending") return true;
        NotificationDispatchResult result;
        try {
            result = await dispatcher.DispatchToNodeAsync(claim.NodeId!.Value, new(claim.Id, NotificationContract.ConversationCapability, 1,
                clock.GetUtcNow().AddMinutes(5), new("Aegis", "Nova mensagem da Aegis", ConversationId: claim.ConversationId, MessageId: claim.MessageId)), ct);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { result = new(null, "unknown", null, "unconfirmed", claim.Id); }
        var token = claim.LeaseToken;
        await transactions.Locked(async () => {
            var item = await db.Set<ProactiveNotification>().SingleAsync(x => x.Id == claim.Id, ct);
            if (item.State != "sending" || item.LeaseToken != token) return false;
            item.Transport = result.Transport; item.LastResult = result.Status; item.LeaseToken = null; item.LeaseUntil = null;
            if (result.Status is "success" or "duplicate" or "accepted") {
                item.State = "accepted"; item.AcceptedAt = clock.GetUtcNow();
                // Native API receipt is distinct from FCM acceptance and from user visibility/read.
                if (result.Transport == "live_websocket" && result.Status is "success" or "duplicate") item.PostedAt = clock.GetUtcNow();
            } else if (item.Attempts >= 5 || result.Status is "permission_denied" or "unsupported" or "expired") item.State = "failed";
            else { item.State = "pending"; item.NextAttemptAt = clock.GetUtcNow() + RetryDelays[item.Attempts - 1]; }
            return true;
        }, ct);
        return true;
    }
}
public sealed class ProactiveNotificationWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<ProactiveNotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            try { for (var i = 0; i < 20; i++) { await using var scope = scopes.CreateAsyncScope(); if (!await scope.ServiceProvider.GetRequiredService<ProactiveNotificationProcessor>().ProcessNextAsync(ct)) break; } }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch { logger.LogWarning("Proactive native notification deferred; message remains available in chat."); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), clock, ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
    }
}
