using System.Text.Json;
using System.Text.Encodings.Web;
using Aegis.Application.Observability;
using Aegis.Application.Reminders;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aegis.Infrastructure.Reminders;

// Specific reminder dispatch, with no model client or automation framework dependency.
public sealed class ReminderProcessor(AegisDbContext db, ReminderStore store, IWebPushClient push,
    IReminderInteractionTokens tokens, TimeProvider clock, AegisMetrics metrics)
{
    private static readonly JsonSerializerOptions PushJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)];
    public async Task<bool> ProcessNextAsync(CancellationToken ct = default)
    {
        if (!push.IsConfigured) return false;
        var claim = await store.ClaimAsync(clock.GetUtcNow(), ct);
        if (claim is null) return false;
        var (id, leaseId) = claim.Value;
        var started = await store.LockedAsync(id, r =>
        {
            if (r.AcknowledgedAt is not null || !r.OwnsLease(leaseId)) return Task.FromResult(false);
            if (r.BeginTrigger(clock.GetUtcNow()))
            {
                metrics.RemindersTriggered.Add(1);
                metrics.ReminderTriggerDelay.Record(Math.Max(0, (clock.GetUtcNow() - r.DueAtUtc).TotalMilliseconds));
            }
            return Task.FromResult(true);
        }, ct);
        if (!started) return true;
        var subscriptions = await db.PushSubscriptions.Where(s => s.DisabledAt == null).OrderBy(s => s.Id).ToListAsync(ct);
        foreach (var subscription in subscriptions)
        {
            // Persist the attempt before network I/O. An interrupted attempt has an
            // unknown outcome; recovery retries it within the same bounded budget.
            var attempt = await store.LockedAsync<ReminderDeliveryAttempt?>(id, async r =>
            {
                if (r.AcknowledgedAt is not null || !r.OwnsLease(leaseId)) return null;
                var last = await db.ReminderDeliveryAttempts.Where(a => a.ReminderId == id && a.PushSubscriptionId == subscription.Id).OrderByDescending(a => a.Attempt).FirstOrDefaultAsync(ct);
                if (last is not null && (last.AcceptedAt is not null ||
                    last.FailureReason is not null && last.RetryAt is null || last.RetryAt > clock.GetUtcNow())) return null;
                if (last is not null && last.FailureReason is null)
                    last.Complete(null, "push_outcome_unknown", last.Attempt <= Backoff.Length ? clock.GetUtcNow() : null, clock.GetUtcNow());
                if (last?.Attempt > Backoff.Length) return null;
                var next = new ReminderDeliveryAttempt(id, subscription.Id, (last?.Attempt ?? 0) + 1, clock.GetUtcNow());
                db.ReminderDeliveryAttempts.Add(next);
                r.RenewLease(clock.GetUtcNow());
                return next;
            }, ct);
            if (attempt is null) continue;
            await store.LockedAsync(id, async r =>
            {
                // ACK may have committed after we persisted the attempt and before this lock.
                if (r.AcknowledgedAt is not null)
                {
                    attempt.Complete(null, "push_skipped_acknowledged", null, clock.GetUtcNow());
                    return false;
                }
                if (!r.OwnsLease(leaseId)) return false;
                var payload = JsonSerializer.Serialize(new {
                    type = "reminder", reminderId = id, text = r.Text,
                    acknowledgeToken = tokens.Create(id, subscription.Id, "acknowledge"),
                    openToken = tokens.Create(id, subscription.Id, "open")
                }, PushJson);
                metrics.PushAttempts.Add(1);
                // A DB row lock stays held across this bounded network operation. A second
                // instance skips the row even if the lease expires during the request.
                var result = await push.SendAsync(subscription, payload, ct);
                var now = clock.GetUtcNow();
                var retryAt = !result.Accepted && result.Transient && attempt.Attempt <= Backoff.Length
                    ? now.Add(Backoff[attempt.Attempt - 1]) : (DateTimeOffset?)null;
                attempt.Complete(result.HttpStatus, result.FailureReason, retryAt, now);
                if (result.Accepted) { subscription.Accepted(now); metrics.PushAccepted.Add(1); }
                else { metrics.PushFailed.Add(1); if (result.PermanentSubscriptionFailure) subscription.Disable(now); }
                r.RenewLease(now);
                return true;
            }, ct);
        }
        await store.LockedAsync(id, async r =>
        {
            if (r.AcknowledgedAt is not null || !r.OwnsLease(leaseId)) return false;
            var attempts = await db.ReminderDeliveryAttempts.Where(a => a.ReminderId == id).ToListAsync(ct);
            var latest = attempts.GroupBy(a => a.PushSubscriptionId).Select(g => g.MaxBy(a => a.Attempt)!).ToList();
            var active = await db.PushSubscriptions.Where(s => s.DisabledAt == null).Select(s => s.Id).ToListAsync(ct);
            var retry = latest.Where(a => a.RetryAt is not null && active.Contains(a.PushSubscriptionId)).Select(a => a.RetryAt).Min();
            r.Finish(retry, attempts.Any(a => a.AcceptedAt is not null), clock.GetUtcNow());
            if (r.Status == ReminderStatus.Failed) metrics.RemindersFailed.Add(1);
            return true;
        }, ct);
        return true;
    }
}
