using System.Text.Json;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace Aegis.Infrastructure.Proactivity;

public sealed record RecoveryClaim(Guid Id, Guid NodeId, int Attempt);
public sealed record InterventionClaim(Guid Id, Guid RuleId, Guid Token, Guid ConversationId, Guid SourceUserMessageId,
    int ActivationEpoch, string ProblemSignature, string Text, string SyncStatus, string? ErrorCode,
    string Diagnosis, string RequiredAction, string RecoveriesJson, IReadOnlyList<ChatMessage> Context);

public sealed class ReminderInterventionStore(AegisDbContext db, TimeProvider clock)
{
    private DbSet<ReminderIntervention> Jobs => db.Set<ReminderIntervention>();
    // Same lock order as PlaceService. No transaction/lock spans model or transport I/O.
    public async Task<T> Locked<T>(Func<Task<T>> action, CancellationToken ct) {
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        if (db.Database.IsNpgsql()) {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7070003)", ct);
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7070011)", ct);
        }
        db.ChangeTracker.Clear();
        var result = await action(); await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return result;
    }
    public Task<IReadOnlyList<RecoveryClaim>> ReconcileAsync(CancellationToken ct) => Locked<IReadOnlyList<RecoveryClaim>>(async () => {
        var now = clock.GetUtcNow();
        var rules = await db.Set<GeographicReminderRule>().Where(r => r.Status == "active" || r.Status == "paused").ToArrayAsync(ct);
        var jobs = await Jobs.Where(j => j.State == "recovering" || j.State == "pending" || j.State == "generating").ToListAsync(ct);
        foreach (var job in jobs) {
            var rule = rules.SingleOrDefault(r => r.Id == job.RuleId);
            if (rule is null || rule.ActivationEpoch != job.ActivationEpoch || !ReminderReliabilityPolicy.HasProblem(rule, now)) job.State = "resolved";
        }
        var claims = new List<RecoveryClaim>();
        // A single observer sync replaces its complete manifest; one send per device per scan.
        foreach (var rule in rules) {
            if (rule.Status == "active" && ReminderReliabilityPolicy.LocalLeaseExpired(rule, now) && rule.SyncStatus is not ("pending_sync" or "recoverable_error"))
                rule.Confirm(rule.Revision, "recoverable_error", "local_lease_expired", now);
            if (!ReminderReliabilityPolicy.HasProblem(rule, now)) continue;
            var job = jobs.SingleOrDefault(j => j.RuleId == rule.Id && j.ActivationEpoch == rule.ActivationEpoch)
                ?? await Jobs.SingleOrDefaultAsync(j => j.RuleId == rule.Id && j.ActivationEpoch == rule.ActivationEpoch, ct);
            if (job is null) {
                job = new() { RuleId = rule.Id, ActivationEpoch = rule.ActivationEpoch, StartedAt = ReminderReliabilityPolicy.StartedAt(rule), NextAttemptAt = ReminderReliabilityPolicy.StartedAt(rule) + ReminderReliabilityPolicy.RecoveryOffsets[0] };
                Jobs.Add(job); jobs.Add(job);
            }
            if (job.State != "recovering" || job.NextAttemptAt > now) continue;
            if (job.RecoveryAttempts < ReminderReliabilityPolicy.RecoveryOffsets.Length) {
                var attempt = ++job.RecoveryAttempts;
                var results = JsonSerializer.Deserialize<List<RecoveryResult>>(job.RecoveryResultsJson)!;
                results.Add(new(attempt, now, "prepared_outcome_unknown")); job.RecoveryResultsJson = JsonSerializer.Serialize(results);
                job.NextAttemptAt = attempt < ReminderReliabilityPolicy.RecoveryOffsets.Length
                    ? new[] { now.AddSeconds(30), job.StartedAt + ReminderReliabilityPolicy.RecoveryOffsets[attempt] }.Max()
                    : new[] { now.AddSeconds(30), job.StartedAt + ReminderReliabilityPolicy.PendingSyncLimit }.Max();
                claims.Add(new(job.Id, rule.ObserverNodeId, attempt));
            } else if (now >= job.StartedAt + ReminderReliabilityPolicy.PendingSyncLimit) {
                (job.Diagnosis, job.RequiredAction) = Diagnose(rule, job);
                job.State = rule.SourceConversationId is null ? "source_unavailable" : "pending";
            }
        }
        return claims;
    }, ct);
    private static (string Diagnosis, string Action) Diagnose(GeographicReminderRule rule, ReminderIntervention job) {
        var results = JsonSerializer.Deserialize<List<RecoveryResult>>(job.RecoveryResultsJson)!;
        return ReminderReliabilityPolicy.Diagnose(rule, results.Count >= 3 && results.All(x => x.Result == "unavailable"));
    }
    private sealed record RecoveryResult(int Attempt, DateTimeOffset At, string Result);
    public Task<bool> RecoveryCompletedAsync(RecoveryClaim claim, string result, CancellationToken ct) => Locked(async () => {
        var job = await Jobs.SingleAsync(j => j.Id == claim.Id, ct);
        var entries = JsonSerializer.Deserialize<List<RecoveryResult>>(job.RecoveryResultsJson)!;
        var index = entries.FindIndex(x => x.Attempt == claim.Attempt);
        if (index >= 0) entries[index] = entries[index] with { Result = result is "success" or "accepted" or "unavailable" or "failed" or "expired" or "unsupported" or "permission_denied" ? result : "unconfirmed" };
        job.RecoveryResultsJson = JsonSerializer.Serialize(entries); return true;
    }, ct);
    private static string Signature(GeographicReminderRule r) => r.SyncStatus + ":" + r.ErrorCode;
    public Task<InterventionClaim?> ClaimAsync(CancellationToken ct) => Locked<InterventionClaim?>(async () => {
        var now = clock.GetUtcNow();
        var job = await Jobs.Where(j => (j.State == "pending" || j.State == "generating" && j.LeaseUntil <= now) && j.NextAttemptAt <= now)
            .OrderBy(j => j.NextAttemptAt).FirstOrDefaultAsync(ct);
        if (job is null) return null;
        var rule = await db.Set<GeographicReminderRule>().SingleAsync(r => r.Id == job.RuleId, ct);
        var conversation = await db.Conversations.SingleOrDefaultAsync(c => c.Id == rule.SourceConversationId && c.DeletedAt == null, ct);
        if (conversation is null || rule.ActivationEpoch != job.ActivationEpoch || !ReminderReliabilityPolicy.HasProblem(rule, now)) { job.State = "resolved"; return null; }
        if (job.GenerationAttempts >= 5) { job.State = "generation_failed"; return null; }
        var context = await db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == conversation.Id).OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id).Take(12).ToArrayAsync(ct);
        var source = await db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == conversation.Id && m.Role == "user" && m.CreatedAt <= rule.CreatedAt).OrderByDescending(m => m.CreatedAt).FirstOrDefaultAsync(ct)
            ?? context.FirstOrDefault(m => m.Role == "user");
        if (source is null) { job.State = "source_unavailable"; return null; }
        job.State = "generating"; job.GenerationAttempts++; job.LeaseToken = Guid.NewGuid(); job.LeaseUntil = now.AddMinutes(2);
        (job.Diagnosis, job.RequiredAction) = Diagnose(rule, job);
        // Always include the actual origin request, even after a long subsequent conversation.
        var relevant = context.Where(m => m.Id != source.Id).Take(11).Append(source).OrderBy(m => m.CreatedAt).ToArray();
        return new(job.Id, rule.Id, job.LeaseToken.Value, conversation.Id, source.Id, job.ActivationEpoch, Signature(rule), rule.Text, rule.SyncStatus, rule.ErrorCode, job.Diagnosis, job.RequiredAction, job.RecoveryResultsJson, relevant);
    }, ct);
    public Task<bool> CompleteAsync(InterventionClaim claim, GeneratedIntervention result, CancellationToken ct) => Locked(async () => {
        var job = await Jobs.SingleAsync(j => j.Id == claim.Id, ct); var now = clock.GetUtcNow();
        if (job.LeaseToken != claim.Token || job.State != "generating" || job.LeaseUntil <= now) return false;
        var rule = await db.Set<GeographicReminderRule>().SingleAsync(r => r.Id == job.RuleId, ct);
        // Serialize with regular chat/rename/delete at the conversation row, after the place locks.
        var conversation = db.Database.IsNpgsql()
            ? await db.Conversations.FromSqlInterpolated($"SELECT * FROM conversations WHERE \"Id\" = {claim.ConversationId} FOR UPDATE").SingleOrDefaultAsync(ct)
            : await db.Conversations.SingleOrDefaultAsync(c => c.Id == claim.ConversationId, ct);
        if (conversation is null || conversation.DeletedAt is not null || rule.ActivationEpoch != claim.ActivationEpoch || !ReminderReliabilityPolicy.HasProblem(rule, now)) { job.State = "resolved"; return false; }
        if (Signature(rule) != claim.ProblemSignature) { job.State = "pending"; job.NextAttemptAt = now; return false; }
        var message = conversation.AddProactiveMessage(result.Content, now);
        message.AttachAuditData(result.Model, null, null, result.MetadataJson);
        db.ChatMessages.Add(message);
        var audit = result.Audit;
        db.LlmRequestAudits.Add(new(claim.ConversationId, claim.SourceUserMessageId, message.Id, audit.Provider, audit.Model, audit.Success, audit.DurationMilliseconds, audit.RequestPayloadJson, audit.HttpStatusCode, audit.ResponseBody, audit.FailureReason, audit.ErrorType));
        db.Set<ProactiveNotification>().Add(new() { MessageId = message.Id, ConversationId = conversation.Id, CreatedAt = now, NextAttemptAt = now });
        job.MessageId = message.Id; job.State = "notified"; job.LeaseToken = null; job.LeaseUntil = null; return true;
    }, ct);
    public Task<bool> FailAsync(InterventionClaim claim, CancellationToken ct, Aegis.Application.Llm.LlmRequestAuditData? audit = null) => Locked(async () => {
        var job = await Jobs.SingleAsync(j => j.Id == claim.Id, ct);
        if (job.LeaseToken != claim.Token || job.State != "generating") return false;
        if (audit is not null && await db.Conversations.AnyAsync(c => c.Id == claim.ConversationId && c.DeletedAt == null, ct))
            db.LlmRequestAudits.Add(new(claim.ConversationId, claim.SourceUserMessageId, null, audit.Provider, audit.Model, audit.Success, audit.DurationMilliseconds, audit.RequestPayloadJson, audit.HttpStatusCode, audit.ResponseBody, audit.FailureReason, audit.ErrorType));
        job.LastError = "model_unavailable"; job.LeaseToken = null; job.LeaseUntil = null;
        job.State = job.GenerationAttempts <= ReminderReliabilityPolicy.ModelRetryDelays.Length ? "pending" : "generation_failed";
        job.NextAttemptAt = clock.GetUtcNow() + ReminderReliabilityPolicy.ModelRetryDelays[Math.Min(job.GenerationAttempts - 1, ReminderReliabilityPolicy.ModelRetryDelays.Length - 1)]; return true;
    }, ct);
}
