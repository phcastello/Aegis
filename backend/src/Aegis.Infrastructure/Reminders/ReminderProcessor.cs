using Aegis.Application.Nodes;
using Aegis.Application.Observability;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Aegis.Application.Reminders;

namespace Aegis.Infrastructure.Reminders;

// One durable occurrence, independent recipients and command identity per presentation generation.
public sealed class ReminderProcessor(AegisDbContext db, ReminderStore store, INodeTargetResolver resolver,
    INodeNotificationDispatcher dispatcher, TimeProvider clock, AegisMetrics metrics, INodeRegistry? registry = null,
    IReminderActionTokens? actionTokens = null)
{
    private static readonly int[] Backoff = [10, 30, 120, 300];
    public async Task<bool> ProcessNextAsync(CancellationToken ct = default)
    {
        var claim = await store.ClaimAsync(clock.GetUtcNow(), ct); if (claim is null) return false;
        var (id, leaseId) = claim.Value;
        var occurrence = await store.LockedAsync<Reminder?>(id, async r => {
            if (r.AcknowledgedAt is not null || !r.OwnsLease(leaseId)) return null;
            if ((r.DeliveryUntil ?? r.DueAtUtc.AddMinutes(10)) <= clock.GetUtcNow())
            {
                var recipients = await db.Set<ReminderRecipient>().Where(p => p.ReminderId == id && p.Generation == r.Generation).ToListAsync(ct);
                foreach (var pending in recipients) pending.Stop(clock.GetUtcNow());
                r.Finish(null, r.ObserverPostedAt is not null || recipients.Any(p => p.Status == "accepted"), clock.GetUtcNow());
                if (r.Status == ReminderStatus.Failed) metrics.RemindersFailed.Add(1); return null;
            }
            if (r.BeginTrigger(clock.GetUtcNow())) { metrics.RemindersTriggered.Add(1); metrics.ReminderTriggerDelay.Record(Math.Max(0, (clock.GetUtcNow() - r.DueAtUtc).TotalMilliseconds)); }
            return r;
        }, ct);
        if (occurrence is null) return true;
        var generation = occurrence.Generation;
        var inventory = registry is null ? Array.Empty<NodeView>() : (await registry.ListForDeliveryAsync(ct)).ToArray();
        var persisted = await db.Set<ReminderRecipient>().AsNoTracking().Where(p => p.ReminderId == id && p.Generation == generation).ToArrayAsync(ct);
        var candidates = inventory.Where(n => n.Enabled && n.RevokedAt is null && n.ReceiveReminders &&
            n.Capabilities.Any(c => c.Name is NotificationContract.Capability or ReminderNotificationContract.NotifyCapability && c.Version >= 1) &&
            !(n.Id == occurrence.ObserverNodeId && occurrence.ObserverPostedAt is not null)).Select(n => n.Id).ToArray();
        if (occurrence.DeliveryPolicy == "bestAvailable" || registry is null)
        {
            var pinned = persisted.FirstOrDefault(p => p.Status == "pending" && p.Ambiguous);
            Guid? resolved = pinned?.NodeId;
            if (resolved is null && registry is not null)
                resolved = inventory.Where(n => candidates.Contains(n.Id) && n.Availability is "online" or "backgroundReachable")
                    .OrderByDescending(n => n.TargetPriority).ThenBy(n => n.CreatedAt).ThenBy(n => n.Id).Select(n => (Guid?)n.Id).FirstOrDefault();
            else if (resolved is null)
                resolved = (await resolver.ResolveForDeliveryAsync(new([new(NotificationContract.Capability, 1)], Reachability: "notification"), ct)).Node?.Id;
            candidates = resolved is null || occurrence.ObserverPostedAt is not null && (resolved == occurrence.ObserverNodeId || occurrence.DeliveryPolicy == "bestAvailable") ? [] : [resolved.Value];
        }
        await store.LockedAsync(id, async r => {
            if (!r.OwnsLease(leaseId) || r.Generation != generation || r.AcknowledgedAt is not null) return false;
            var current = await db.Set<ReminderRecipient>().Where(p => p.ReminderId == id && p.Generation == generation).ToListAsync(ct);
            if (r.ObserverNodeId is { } observer && r.ObserverPostedAt is { } posted && !current.Any(p => p.NodeId == observer))
            { var local = ReminderRecipient.LocalPosted(id, generation, observer, posted); db.Set<ReminderRecipient>().Add(local); current.Add(local); }
            // Upgrade a still-pending Stage07 command without changing dedupe identity/expiry.
            if (current.Count == 0)
            {
                var legacy = await db.ReminderDeliveryAttempts.Where(a => a.ReminderId == id && a.Generation == generation).OrderByDescending(a => a.Attempt).FirstOrDefaultAsync(ct);
                if (legacy?.NodeId is not null)
                { var recovered = ReminderRecipient.Recover(legacy, generation, clock.GetUtcNow()); db.Set<ReminderRecipient>().Add(recovered); current.Add(recovered); }
            }
            if (r.DeliveryPolicy == "bestAvailable" && current.Any(p => p.Status == "accepted")) return true;
            foreach (var nodeId in candidates)
                if (!current.Any(p => p.NodeId == nodeId)) { var next = new ReminderRecipient(id, generation, nodeId, clock.GetUtcNow()); db.Set<ReminderRecipient>().Add(next); current.Add(next); }
            if (r.DeliveryPolicy == "bestAvailable" && candidates.Length > 0)
                foreach (var old in current.Where(p => !p.Ambiguous && p.NodeId is { } node && !candidates.Contains(node))) old.Stop(clock.GetUtcNow());
            return true;
        }, ct);
        var work = await db.Set<ReminderRecipient>().AsNoTracking().Where(p => p.ReminderId == id && p.Generation == generation && p.Status == "pending" && p.Attempts < 5 && p.NextAttemptAt <= clock.GetUtcNow()).Select(p => p.Id).ToArrayAsync(ct);
        foreach (var recipientId in work)
        {
            var prepared = await store.LockedAsync<(ReminderRecipient? Recipient, ReminderDeliveryAttempt? Attempt)>(id, async r => {
                if (!r.OwnsLease(leaseId) || r.Generation != generation || r.AcknowledgedAt is not null) return (null, null);
                var recipient = await FreshRecipient(recipientId, ct); if (!recipient.NeedsRetry(clock.GetUtcNow()) || recipient.NodeId is null) return (null, null);
                var interrupted = await db.ReminderDeliveryAttempts.Where(a => a.RecipientId == recipient.Id && a.CompletedAt == null).ToListAsync(ct);
                foreach (var prior in interrupted) prior.Complete("outcome_unknown", prior.Transport, true, clock.GetUtcNow(), clock.GetUtcNow());
                recipient.Begin(clock.GetUtcNow());
                var attempt = new ReminderDeliveryAttempt(id, recipient.Attempts, recipient.NodeId, recipient.CommandId, recipient.CommandExpiresAt, clock.GetUtcNow()); attempt.BindRecipient(recipient.Id, generation);
                db.ReminderDeliveryAttempts.Add(attempt); r.RenewLease(clock.GetUtcNow()); return (recipient, attempt);
            }, ct);
            if (prepared.Recipient is null || prepared.Attempt is null) continue;
            var recipient = prepared.Recipient; var attempt = prepared.Attempt;
            var input = new NotificationInput("Aegis", occurrence.Text); var capability = NotificationContract.Capability;
            if (actionTokens is not null && inventory.Any(n => n.Id == recipient.NodeId && n.Capabilities.Any(c => c.Name == ReminderNotificationContract.NotifyCapability && c.Version >= 1)))
            { capability = ReminderNotificationContract.NotifyCapability; input = new("Aegis", occurrence.Text, id, occurrence.Revision, generation, actionTokens.Create(occurrence, clock.GetUtcNow().AddDays(7))); }
            // A command may have expired while waiting for recovery. Never mint a fresh one after ambiguous send.
            NotificationDispatchResult result;
            metrics.ReminderDeliveryAttempts.Add(1);
            if (attempt.CommandExpiresAt <= clock.GetUtcNow()) result = new(new(recipient.NodeId!.Value, ""), "offline", null, "expired", attempt.CommandId);
            else result = await dispatcher.DispatchToNodeAsync(recipient.NodeId!.Value, new(attempt.CommandId!.Value, capability, 1, attempt.CommandExpiresAt!.Value, input, IsReminder: true), ct);
            await store.LockedAsync(id, async r => {
                var saved = await db.ReminderDeliveryAttempts.AsNoTracking().SingleAsync(a => a.Id == attempt.Id, ct);
                var tracked = db.ChangeTracker.Entries<ReminderDeliveryAttempt>().FirstOrDefault(e => e.Entity.Id == saved.Id); if (tracked is not null) tracked.State = EntityState.Detached; db.Attach(saved);
                if (saved.CompletedAt is not null) return false;
                var fresh = await FreshRecipient(recipient.Id, ct);
                var accepted = result.Status is "success" or "accepted" or "duplicate";
                var priorAmbiguity = await db.ReminderDeliveryAttempts.AnyAsync(a => a.ReminderId == id && a.CommandId == saved.CommandId && a.Id != saved.Id && (a.OutcomeAmbiguous || a.CompletedAt == null), ct);
                var ambiguous = !accepted && (priorAmbiguity || result.Status == "busy" || result.Transport is not null && (result.Status is "timeout" or "failed" || result.Status == "unavailable" && result.Transport == "live_websocket"));
                fresh.Result(result.Status, result.Transport, ambiguous, clock.GetUtcNow());
                saved.Complete(result.Status, result.Transport, ambiguous, fresh.Status == "pending" ? fresh.NextAttemptAt : null, clock.GetUtcNow());
                if (accepted) metrics.ReminderDeliveryAccepted.Add(1); else metrics.ReminderDeliveryFailed.Add(1); return true;
            }, ct);
        }
        await store.LockedAsync(id, async r => {
            if (!r.OwnsLease(leaseId) || r.Generation != generation || r.AcknowledgedAt is not null) return false;
            var recipients = await db.Set<ReminderRecipient>().AsNoTracking().Where(p => p.ReminderId == id && p.Generation == generation).ToArrayAsync(ct);
            var accepted = recipients.Any(p => p.Status == "accepted") || r.ObserverPostedAt is not null;
            DateTimeOffset? retry = null;
            if (r.DeliveryPolicy == "bestAvailable" && accepted) { }
            else if (recipients.Any(p => p.Status == "pending" && p.Attempts < 5)) retry = recipients.Where(p => p.Status == "pending" && p.Attempts < 5).Min(p => p.NextAttemptAt);
            else if (recipients.Length == 0 && r.ObserverPostedAt is null)
            {
                var previous = await db.ReminderDeliveryAttempts.Where(a => a.ReminderId == id && a.Generation == generation).OrderByDescending(a => a.Attempt).FirstOrDefaultAsync(ct);
                var number = (previous?.Attempt ?? 0) + 1; var attempt = new ReminderDeliveryAttempt(id, number, null, null, null, clock.GetUtcNow()); attempt.BindRecipient(Guid.Empty, generation);
                retry = number <= Backoff.Length ? clock.GetUtcNow().AddSeconds(Backoff[number - 1]) : null;
                attempt.Complete("no_eligible_node", null, false, retry, clock.GetUtcNow()); db.ReminderDeliveryAttempts.Add(attempt);
            }
            if (retry > (r.DeliveryUntil ?? r.DueAtUtc.AddMinutes(10))) retry = null;
            r.Finish(retry, accepted, clock.GetUtcNow()); if (r.Status == ReminderStatus.Failed) metrics.RemindersFailed.Add(1); return true;
        }, ct);
        return true;
    }
    private async Task<ReminderRecipient> FreshRecipient(Guid id, CancellationToken ct)
    {
        var saved = await db.Set<ReminderRecipient>().AsNoTracking().SingleAsync(p => p.Id == id, ct);
        var tracked = db.ChangeTracker.Entries<ReminderRecipient>().FirstOrDefault(e => e.Entity.Id == id); if (tracked is not null) tracked.State = EntityState.Detached; db.Attach(saved); return saved;
    }
}
