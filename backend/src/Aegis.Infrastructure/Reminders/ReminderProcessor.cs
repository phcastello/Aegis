using Aegis.Application.Nodes;
using Aegis.Application.Observability;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aegis.Infrastructure.Reminders;

// Scheduling/leases stay in the reminder domain. Nodes own target selection and transport.
public sealed class ReminderProcessor(AegisDbContext db, ReminderStore store, INodeTargetResolver resolver,
    INodeNotificationDispatcher dispatcher, TimeProvider clock, AegisMetrics metrics)
{
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)];
    public async Task<bool> ProcessNextAsync(CancellationToken ct = default)
    {
        var claim = await store.ClaimAsync(clock.GetUtcNow(), ct);
        if (claim is null) return false;
        var (id, leaseId) = claim.Value;
        var started = await store.LockedAsync(id, async r =>
        {
            if (r.AcknowledgedAt is not null || !r.OwnsLease(leaseId)) return false;
            if (r.BeginTrigger(clock.GetUtcNow()))
            {
                metrics.RemindersTriggered.Add(1);
                metrics.ReminderTriggerDelay.Record(Math.Max(0, (clock.GetUtcNow() - r.DueAtUtc).TotalMilliseconds));
            }
            var accepted = await db.ReminderDeliveryAttempts.AnyAsync(a => a.ReminderId == id && a.AcceptedAt != null, ct);
            if (accepted) r.Finish(null, true, clock.GetUtcNow());
            return !accepted;
        }, ct);
        if (!started) return true;

        var previous = await db.ReminderDeliveryAttempts.AsNoTracking().Where(a => a.ReminderId == id)
            .OrderByDescending(a => a.Attempt).FirstOrDefaultAsync(ct);
        // An interrupted send may already have displayed. Pin its target and command;
        // preferred targets are insufficient because the resolver may fallback to another Node.
        var ambiguous = previous?.CommandId is not null && (previous.CompletedAt is null || previous.OutcomeAmbiguous);
        Guid? nodeId = ambiguous ? previous!.NodeId : (await resolver.ResolveForDeliveryAsync(
            new([new(NotificationContract.Capability, 1)], Reachability: "notification"), ct)).Node?.Id;
        var commandId = nodeId is null ? null : ambiguous ? previous!.CommandId : Guid.NewGuid();
        var expiresAt = nodeId is null ? null : ambiguous ? previous!.CommandExpiresAt : clock.GetUtcNow().AddSeconds(300);

        // Persist before network I/O; every recovered pending send consumes the same bounded budget.
        var attempt = await store.LockedAsync<ReminderDeliveryAttempt?>(id, async r =>
        {
            if (r.AcknowledgedAt is not null || !r.OwnsLease(leaseId)) return null;
            // A late acceptance may have committed after the initial check/target resolution.
            if (await db.ReminderDeliveryAttempts.AnyAsync(a => a.ReminderId == id && a.AcceptedAt != null, ct))
            {
                r.Finish(null, true, clock.GetUtcNow()); return null;
            }
            var last = await db.ReminderDeliveryAttempts.Where(a => a.ReminderId == id).OrderByDescending(a => a.Attempt).FirstOrDefaultAsync(ct);
            if (last?.CompletedAt is null && last is not null)
                last.Complete("outcome_unknown", last.Transport, last.CommandId is not null,
                    last.Attempt <= Backoff.Length ? clock.GetUtcNow() : null, clock.GetUtcNow());
            if (last?.Attempt > Backoff.Length)
            {
                r.Finish(null, false, clock.GetUtcNow()); metrics.RemindersFailed.Add(1); return null;
            }
            var next = new ReminderDeliveryAttempt(id, (last?.Attempt ?? 0) + 1, nodeId, commandId, expiresAt, clock.GetUtcNow());
            db.ReminderDeliveryAttempts.Add(next); r.RenewLease(clock.GetUtcNow());
            return next;
        }, ct);
        if (attempt is null) return true;

        var text = await store.LockedAsync<string?>(id, r =>
        {
            // Recheck ownership/ACK immediately before dispatch, without holding a DB lock over I/O.
            if (r.AcknowledgedAt is not null || !r.OwnsLease(leaseId)) return Task.FromResult<string?>(null);
            return Task.FromResult<string?>(r.Text);
        }, ct);
        if (text is null) return true;
        metrics.ReminderDeliveryAttempts.Add(1);
        NotificationDispatchResult result;
        if (attempt.NodeId is null) result = new(null, "offline", null, "no_eligible_node", null);
        else if (attempt.CommandExpiresAt <= clock.GetUtcNow())
            result = new(new(attempt.NodeId.Value, ""), "offline", null, "expired", attempt.CommandId);
        else result = await dispatcher.DispatchToNodeAsync(attempt.NodeId.Value,
            new(attempt.CommandId!.Value, NotificationContract.Capability, 1, attempt.CommandExpiresAt!.Value, new("Aegis", text)), ct);

        await store.LockedAsync(id, async r =>
        {
            // Read fresh after a possible lease takeover: a stale worker cannot overwrite recovery.
            var saved = await db.ReminderDeliveryAttempts.AsNoTracking().SingleAsync(a => a.Id == attempt.Id, ct);
            var tracked = db.ChangeTracker.Entries<ReminderDeliveryAttempt>().SingleOrDefault(e => e.Entity.Id == attempt.Id);
            if (tracked is not null) tracked.State = EntityState.Detached;
            db.Attach(saved);
            if (saved.CompletedAt is not null) return false;
            var now = clock.GetUtcNow();
            var accepted = result.Status is "success" or "accepted" or "duplicate";
            // Transport errors/failed renderers may follow a send; be conservative about duplicates.
            var unknown = !accepted && (ambiguous || result.Transport is not null && (result.Status is "timeout" or "unavailable" or "failed"));
            var retryAt = !accepted && saved.Attempt <= Backoff.Length && !(unknown && saved.CommandExpiresAt <= now)
                ? now.Add(Backoff[saved.Attempt - 1]) : (DateTimeOffset?)null;
            saved.Complete(result.Status, result.Transport, unknown, retryAt, now);
            if (accepted) metrics.ReminderDeliveryAccepted.Add(1); else metrics.ReminderDeliveryFailed.Add(1);
            if (r.OwnsLease(leaseId))
            {
                r.Finish(retryAt, accepted, now);
                if (r.Status == ReminderStatus.Failed) metrics.RemindersFailed.Add(1);
            }
            return true;
        }, ct);
        return true;
    }
}
