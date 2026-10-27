using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace Aegis.Infrastructure.Proactivity;

public sealed record RecoveryClaim(Guid Id, Guid NodeId, int Attempt);
public sealed record AffectedReminderSummary(Guid Id, string Text, string SyncStatus, string? ErrorCode);
public sealed record InterventionClaim(Guid Id, Guid RuleId, Guid Token, Guid ConversationId, Guid SourceUserMessageId,
    int ActivationEpoch, string ProblemSignature, string Text, string SyncStatus, string? ErrorCode,
    string Diagnosis, string RequiredAction, string RecoveriesJson, IReadOnlyList<ChatMessage> Context,
    int AffectedRuleCount = 1, IReadOnlyList<AffectedReminderSummary>? ReminderSummaries = null);

public sealed class ReminderInterventionStore(AegisDbContext db, TimeProvider clock)
{
    private DbSet<ReminderIntervention> Jobs => db.Set<ReminderIntervention>();
    private DbSet<SharedReminderIncident> Incidents => db.Set<SharedReminderIncident>();
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
    private sealed record RecoveryResult(int Attempt, DateTimeOffset At, string Result);
    private static bool Unreachable(ReminderIntervention job) {
        var results = JsonSerializer.Deserialize<List<RecoveryResult>>(job.RecoveryResultsJson)!;
        return results.Count >= 3 && results.All(x => x.Result == "unavailable");
    }
    private static (string Cause, string Action, bool Shared) Scope(GeographicReminderRule rule, ReminderIntervention job) => rule.ErrorCode switch {
        "background_location_required" or "location_permission_required" or "precise_location_required" or
        "location_services_disabled" or "location_provider_unavailable" => ("location_availability", "restore_location", true),
        "region_limit_exceeded" => ("registration_capacity", "reduce_regions", true),
        // Android installs the complete manifest; these are observer registration failures.
        "native_registration_failed" => ("observer_registration", "restore_monitoring", true),
        "geometry_required" => ("rule_geometry", "repair_geometry", false),
        _ when Unreachable(job) => ("observer_unavailable", "restore_connection", true),
        // The observer acknowledges one complete manifest, not an independent transport per rule.
        _ when rule.SyncStatus == "pending_sync" || rule.ErrorCode == "local_lease_expired" => ("monitoring_confirmation", "verify_observer", true),
        _ => ("activation_unconfirmed", "verify_rule", false)
    };
    private static bool Relevant(GeographicReminderRule r, DateTimeOffset now) => r.Status == "active" && (r.EndsAt is null || r.EndsAt > now);
    private static bool Healthy(GeographicReminderRule r, DateTimeOffset now) => Relevant(r, now) &&
        r.SyncStatus is "installed" or "waiting_initial" or "active" && r.SyncedRevision == r.Revision && r.SyncedAt is not null &&
        !ReminderReliabilityPolicy.LocalLeaseExpired(r, now);
    private static string Signature(IEnumerable<GeographicReminderRule> rules) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join("|", rules.OrderBy(r => r.Id).Select(r => $"{r.Id}:{r.ActivationEpoch}:{r.SyncStatus}:{r.ErrorCode}")))));
    private static void Close(SharedReminderIncident incident, DateTimeOffset now) {
        incident.State = "resolved"; incident.ResolvedAt = now; incident.UpdatedAt = now; incident.LeaseToken = null; incident.LeaseUntil = null;
    }
    // Refreshing correlation is also used immediately before model I/O and message persistence.
    // It never prepares a recovery command; only ReconcileAsync may do that.
    private async Task<(GeographicReminderRule[] Rules, List<ReminderIntervention> Jobs)> RefreshAsync(DateTimeOffset now, CancellationToken ct) {
        var rules = await db.Set<GeographicReminderRule>().ToArrayAsync(ct);
        var activeIds = rules.Where(r => Relevant(r, now)).Select(r => r.Id).ToArray();
        var incidents = await Incidents.Where(i => i.ResolvedAt == null).ToListAsync(ct);
        var openIds = incidents.Select(i => i.Id).ToArray();
        var jobs = await Jobs.Where(j => activeIds.Contains(j.RuleId) || j.SharedIncidentId != null && openIds.Contains(j.SharedIncidentId.Value) ||
            j.State == "recovering" || j.State == "pending" || j.State == "generating" || j.State == "generation_failed").ToListAsync(ct);
        foreach (var job in jobs) {
            var rule = rules.SingleOrDefault(r => r.Id == job.RuleId);
            if (rule is null || rule.ActivationEpoch != job.ActivationEpoch || !ReminderReliabilityPolicy.HasProblem(rule, now)) {
                // Keep historical links so a confirmed healthy period, rather than a sync request, closes communication.
                if (job.State != "notified") job.State = "resolved";
            }
        }
        foreach (var rule in rules.Where(r => ReminderReliabilityPolicy.HasProblem(r, now))) {
            var job = jobs.SingleOrDefault(j => j.RuleId == rule.Id && j.ActivationEpoch == rule.ActivationEpoch);
            if (job is null) {
                job = new() { RuleId = rule.Id, ActivationEpoch = rule.ActivationEpoch, StartedAt = ReminderReliabilityPolicy.StartedAt(rule), NextAttemptAt = ReminderReliabilityPolicy.StartedAt(rule) + ReminderReliabilityPolicy.RecoveryOffsets[0] };
                Jobs.Add(job); jobs.Add(job);
            }
            var existing = incidents.SingleOrDefault(i => i.Id == job.SharedIncidentId)
                ?? incidents.FirstOrDefault(i => i.ObserverNodeId == rule.ObserverNodeId && jobs.Any(j => j.RuleId == rule.Id && j.SharedIncidentId == i.Id));
            (job.Diagnosis, job.RequiredAction) = ReminderReliabilityPolicy.Diagnose(rule, Unreachable(job));
            var scope = Scope(rule, job);
            // A new sync or an unclassified transport symptom is not proof of recovery or of a new human action.
            if (existing is not null && existing.ObserverNodeId == rule.ObserverNodeId && scope.Cause is "activation_unconfirmed" or "observer_registration" or "monitoring_confirmation") {
                scope = (existing.Cause, existing.RequiredActionKey, !existing.GroupKey.EndsWith(rule.Id.ToString("N")));
            }
            var key = $"{rule.ObserverNodeId:N}:geofence:{scope.Cause}:{scope.Action}" + (scope.Shared ? "" : $":{rule.Id:N}");
            var incident = incidents.SingleOrDefault(i => i.GroupKey == key);
            // Pending rules join a known continuous location outage without restarting its episode.
            if (incident is null && scope.Cause == "monitoring_confirmation")
                incident = incidents.SingleOrDefault(i => i.ObserverNodeId == rule.ObserverNodeId && i.Cause == "location_availability");
            if (incident is null && !ReminderReliabilityPolicy.ShouldIntervene(rule, job.StartedAt, now)) continue;
            if (incident is null) {
                incident = new() { GroupKey = key, ObserverNodeId = rule.ObserverNodeId, Cause = scope.Cause, RequiredActionKey = scope.Action,
                    StartedAt = job.StartedAt, UpdatedAt = now, NextAttemptAt = now };
                incidents.Add(incident); Incidents.Add(incident);
            }
            var previousIncidentId = job.SharedIncidentId;
            job.SharedIncidentId = incident.Id;
            // Only an unlinked legacy communication can seed an episode. A newly discovered
            // independent cause must not inherit the old episode's message, even within one epoch.
            if (job.State == "notified" && previousIncidentId is null) {
                incident.State = "notified"; incident.MessageId ??= job.MessageId;
                incident.LeaseToken = null; incident.LeaseUntil = null;
            } else if (job.State != "notified" || previousIncidentId != incident.Id) {
                job.State = ReminderReliabilityPolicy.ShouldIntervene(rule, job.StartedAt, now) ? "correlated" : "recovering";
                job.LeaseToken = null; job.LeaseUntil = null;
            }
        }
        foreach (var incident in incidents) {
            var members = rules.Where(r => r.ObserverNodeId == incident.ObserverNodeId && Relevant(r, now) &&
                (jobs.SingleOrDefault(j => j.RuleId == r.Id && j.ActivationEpoch == r.ActivationEpoch)
                    ?? jobs.Where(j => j.RuleId == r.Id).OrderByDescending(j => j.ActivationEpoch).FirstOrDefault())?.SharedIncidentId == incident.Id).ToArray();
            var affected = members.Where(r => ReminderReliabilityPolicy.HasProblem(r, now)).ToArray();
            incident.AffectedRuleCount = affected.Length; incident.UpdatedAt = now;
            if (members.Length == 0) { Close(incident, now); continue; }
            if (members.All(r => Healthy(r, now))) {
                incident.HealthySince ??= now;
                if (now - incident.HealthySince >= ReminderReliabilityPolicy.ScanInterval) Close(incident, now);
                continue;
            }
            incident.HealthySince = null;
            if (affected.Length == 0) continue;
            var diagnosed = affected.OrderByDescending(r => r.SyncStatus == "recoverable_error" && r.ErrorCode is not null)
                .ThenByDescending(r => r.SyncedAt ?? r.SyncStartedAt).ThenBy(r => r.Id).First();
            var diagnosedJob = jobs.Single(j => j.RuleId == diagnosed.Id && j.ActivationEpoch == diagnosed.ActivationEpoch);
            (incident.Diagnosis, incident.RequiredAction) = ReminderReliabilityPolicy.Diagnose(diagnosed, Unreachable(diagnosedJob));
            diagnosedJob.Diagnosis = incident.Diagnosis; diagnosedJob.RequiredAction = incident.RequiredAction;
            incident.ProblemSignature = Signature(affected);
            if (incident.ConversationId is null) {
                var sources = affected.Where(r => r.SourceConversationId != null).Select(r => r.SourceConversationId!.Value).ToArray();
                var valid = await db.Conversations.Where(c => sources.Contains(c.Id) && c.DeletedAt == null).Select(c => c.Id).ToArrayAsync(ct);
                incident.ConversationId = affected.Where(r => r.SourceConversationId != null && valid.Contains(r.SourceConversationId.Value))
                    .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).Select(r => r.SourceConversationId).FirstOrDefault();
                if (incident.ConversationId is null && incident.State != "notified") { incident.State = "source_unavailable"; incident.LastError = "source_conversation_unavailable"; }
                else if (incident.State == "source_unavailable") { incident.State = "pending"; incident.NextAttemptAt = now; }
            }
        }
        return (rules, jobs);
    }
    public Task<IReadOnlyList<RecoveryClaim>> ReconcileAsync(CancellationToken ct) => Locked<IReadOnlyList<RecoveryClaim>>(async () => {
        var now = clock.GetUtcNow();
        foreach (var rule in await db.Set<GeographicReminderRule>().Where(r => r.Status == "active").ToArrayAsync(ct))
            if (ReminderReliabilityPolicy.LocalLeaseExpired(rule, now) && rule.SyncStatus is not ("pending_sync" or "recoverable_error"))
                rule.Confirm(rule.Revision, "recoverable_error", "local_lease_expired", now);
        var (rules, jobs) = await RefreshAsync(now, ct);
        var claims = new List<RecoveryClaim>();
        foreach (var job in jobs.Where(j => j.State == "recovering")) {
            var rule = rules.Single(r => r.Id == job.RuleId);
            if (job.NextAttemptAt > now || job.RecoveryAttempts >= ReminderReliabilityPolicy.RecoveryOffsets.Length) continue;
            var attempt = ++job.RecoveryAttempts;
            var results = JsonSerializer.Deserialize<List<RecoveryResult>>(job.RecoveryResultsJson)!;
            results.Add(new(attempt, now, "prepared_outcome_unknown")); job.RecoveryResultsJson = JsonSerializer.Serialize(results);
            job.NextAttemptAt = attempt < ReminderReliabilityPolicy.RecoveryOffsets.Length
                ? new[] { now.AddSeconds(30), job.StartedAt + ReminderReliabilityPolicy.RecoveryOffsets[attempt] }.Max()
                : new[] { now.AddSeconds(30), job.StartedAt + ReminderReliabilityPolicy.EscalationAfter }.Max();
            claims.Add(new(job.Id, rule.ObserverNodeId, attempt));
        }
        return claims;
    }, ct);
    public Task<bool> RecoveryCompletedAsync(RecoveryClaim claim, string result, CancellationToken ct) => Locked(async () => {
        var job = await Jobs.SingleAsync(j => j.Id == claim.Id, ct);
        var entries = JsonSerializer.Deserialize<List<RecoveryResult>>(job.RecoveryResultsJson)!;
        var index = entries.FindIndex(x => x.Attempt == claim.Attempt);
        if (index >= 0) entries[index] = entries[index] with { Result = result is "success" or "accepted" or "unavailable" or "failed" or "expired" or "unsupported" or "permission_denied" ? result : "unconfirmed" };
        job.RecoveryResultsJson = JsonSerializer.Serialize(entries); return true;
    }, ct);
    private static GeographicReminderRule[] Affected(SharedReminderIncident incident, GeographicReminderRule[] rules, List<ReminderIntervention> jobs, DateTimeOffset now) {
        var ids = jobs.Where(j => j.SharedIncidentId == incident.Id && rules.Any(r => r.Id == j.RuleId && r.ActivationEpoch == j.ActivationEpoch)).Select(j => j.RuleId).ToArray();
        return rules.Where(r => ids.Contains(r.Id) && r.ObserverNodeId == incident.ObserverNodeId && ReminderReliabilityPolicy.HasProblem(r, now)).ToArray();
    }
    public Task<InterventionClaim?> ClaimAsync(CancellationToken ct) => Locked<InterventionClaim?>(async () => {
        var now = clock.GetUtcNow(); var (rules, jobs) = await RefreshAsync(now, ct);
        // Include just-created tracked incidents before SaveChanges at transaction end.
        var incident = db.ChangeTracker.Entries<SharedReminderIncident>().Select(e => e.Entity)
            .Where(i => i.ResolvedAt == null && (i.State is "pending" or "generation_failed" || i.State == "generating" && i.LeaseUntil <= now) && i.NextAttemptAt <= now)
            .Where(i => Affected(i, rules, jobs, now).Any(r => ReminderReliabilityPolicy.ShouldIntervene(r,
                jobs.Single(j => j.RuleId == r.Id && j.ActivationEpoch == r.ActivationEpoch).StartedAt, now)))
            .OrderBy(i => i.NextAttemptAt).ThenBy(i => i.Id).FirstOrDefault();
        if (incident is null) return null;
        var affected = Affected(incident, rules, jobs, now);
        if (affected.Length == 0) return null;
        if (!affected.Any(r => ReminderReliabilityPolicy.ShouldIntervene(r, jobs.Single(j => j.RuleId == r.Id && j.ActivationEpoch == r.ActivationEpoch).StartedAt, now))) return null;
        var conversation = await db.Conversations.SingleOrDefaultAsync(c => c.Id == incident.ConversationId && c.DeletedAt == null, ct);
        if (conversation is null) { incident.State = "source_unavailable"; incident.LastError = "source_conversation_unavailable"; return null; }
        var rule = affected.Where(r => r.SourceConversationId == conversation.Id).OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).FirstOrDefault()
            ?? affected.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).First();
        var context = await db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == conversation.Id).OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id).Take(12).ToArrayAsync(ct);
        var source = await db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == conversation.Id && m.Role == "user" && m.CreatedAt <= rule.CreatedAt).OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id).FirstOrDefaultAsync(ct)
            ?? context.FirstOrDefault(m => m.Role == "user");
        if (source is null) { incident.State = "source_unavailable"; incident.LastError = "source_request_unavailable"; return null; }
        incident.State = "generating"; incident.GenerationAttempts++; incident.LeaseToken = Guid.NewGuid(); incident.LeaseUntil = now.AddMinutes(2);
        incident.NextAttemptAt = incident.LeaseUntil.Value + ReminderReliabilityPolicy.ModelRetryDelay(incident.GenerationAttempts);
        var relevant = context.Where(m => m.Id != source.Id).Take(11).Append(source).OrderBy(m => m.CreatedAt).ToArray();
        var job = jobs.Single(j => j.RuleId == rule.Id && j.ActivationEpoch == rule.ActivationEpoch);
        return new(incident.Id, rule.Id, incident.LeaseToken.Value, conversation.Id, source.Id, rule.ActivationEpoch, incident.ProblemSignature,
            rule.Text, rule.SyncStatus, rule.ErrorCode, incident.Diagnosis, incident.RequiredAction, job.RecoveryResultsJson, relevant,
            affected.Length, affected.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).Take(5)
                .Select(r => new AffectedReminderSummary(r.Id, r.Text.Length > 300 ? r.Text[..300] : r.Text, r.SyncStatus, r.ErrorCode)).ToArray());
    }, ct);
    private async Task<SharedReminderIncident?> ValidateAsync(InterventionClaim claim, DateTimeOffset now, CancellationToken ct) {
        var (rules, jobs) = await RefreshAsync(now, ct);
        var incident = await Incidents.SingleOrDefaultAsync(i => i.Id == claim.Id, ct);
        if (incident is null || incident.LeaseToken != claim.Token || incident.State != "generating" || incident.LeaseUntil <= now || incident.ResolvedAt != null) return null;
        var affected = Affected(incident, rules, jobs, now);
        if (affected.Length == 0) { incident.State = "pending"; incident.LeaseToken = null; incident.LeaseUntil = null; incident.NextAttemptAt = now; return null; }
        if (incident.ProblemSignature != claim.ProblemSignature ||
            !affected.Any(r => ReminderReliabilityPolicy.ShouldIntervene(r, jobs.Single(j => j.RuleId == r.Id && j.ActivationEpoch == r.ActivationEpoch).StartedAt, now))) {
            incident.State = "pending"; incident.LeaseToken = null; incident.LeaseUntil = null; incident.NextAttemptAt = now; return null;
        }
        if (!await db.Conversations.AnyAsync(c => c.Id == claim.ConversationId && c.DeletedAt == null, ct)) {
            incident.State = "source_unavailable"; incident.LeaseToken = null; incident.LeaseUntil = null; incident.LastError = "source_conversation_unavailable"; return null;
        }
        return incident;
    }
    public Task<bool> RevalidateAsync(InterventionClaim claim, CancellationToken ct) => Locked(async () => await ValidateAsync(claim, clock.GetUtcNow(), ct) is not null, ct);
    public Task<bool> CompleteAsync(InterventionClaim claim, GeneratedIntervention result, CancellationToken ct) => Locked(async () => {
        var now = clock.GetUtcNow(); var incident = await ValidateAsync(claim, now, ct);
        if (incident is null) return false;
        // Serialize with regular chat/rename/delete after the place locks.
        var conversation = db.Database.IsNpgsql()
            ? await db.Conversations.FromSqlInterpolated($"SELECT * FROM conversations WHERE \"Id\" = {claim.ConversationId} FOR UPDATE").SingleOrDefaultAsync(ct)
            : await db.Conversations.SingleOrDefaultAsync(c => c.Id == claim.ConversationId, ct);
        if (conversation is null || conversation.DeletedAt is not null) { incident.State = "source_unavailable"; incident.LeaseToken = null; incident.LeaseUntil = null; return false; }
        var message = conversation.AddProactiveMessage(result.Content, now); message.AttachAuditData(result.Model, null, null, result.MetadataJson); db.ChatMessages.Add(message);
        var audit = result.Audit;
        db.LlmRequestAudits.Add(new(claim.ConversationId, claim.SourceUserMessageId, message.Id, audit.Provider, audit.Model, audit.Success, audit.DurationMilliseconds, audit.RequestPayloadJson, audit.HttpStatusCode, audit.ResponseBody, audit.FailureReason, audit.ErrorType));
        db.Set<ProactiveNotification>().Add(new() { MessageId = message.Id, ConversationId = conversation.Id, CreatedAt = now, NextAttemptAt = now });
        incident.MessageId = message.Id; incident.State = "notified"; incident.LeaseToken = null; incident.LeaseUntil = null;
        return true;
    }, ct);
    public Task<bool> FailAsync(InterventionClaim claim, CancellationToken ct, Aegis.Application.Llm.LlmRequestAuditData? audit = null) => Locked(async () => {
        var incident = await Incidents.SingleAsync(i => i.Id == claim.Id, ct);
        if (incident.LeaseToken != claim.Token || incident.State != "generating" || incident.ResolvedAt != null) return false;
        if (audit is not null && await db.Conversations.AnyAsync(c => c.Id == claim.ConversationId && c.DeletedAt == null, ct))
            db.LlmRequestAudits.Add(new(claim.ConversationId, claim.SourceUserMessageId, null, audit.Provider, audit.Model, audit.Success, audit.DurationMilliseconds, audit.RequestPayloadJson, audit.HttpStatusCode, audit.ResponseBody, audit.FailureReason, audit.ErrorType));
        incident.LastError = "model_unavailable"; incident.LeaseToken = null; incident.LeaseUntil = null;
        incident.State = incident.GenerationAttempts <= ReminderReliabilityPolicy.ModelRetryDelays.Length ? "pending" : "generation_failed";
        incident.NextAttemptAt = clock.GetUtcNow() + ReminderReliabilityPolicy.ModelRetryDelay(incident.GenerationAttempts); return true;
    }, ct);
}
