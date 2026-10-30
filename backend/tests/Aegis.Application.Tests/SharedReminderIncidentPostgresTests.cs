using Aegis.Application.Nodes;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Nodes;
using Aegis.Infrastructure.Persistence;
using Aegis.Infrastructure.Proactivity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Aegis.Application.Tests;

// Each test migrates a fresh schema in a database whose disposable name is enforced
// by NodePostgresTests. No personal database, transport, or configured model is used.
public sealed class SharedReminderIncidentPostgresTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-10-27T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed record Fixture(Guid Android, Guid Windows, Guid[] Conversations, Guid[] Rules);
    private static GeneratedIntervention Generated() => new("Ative a localização do celular para restabelecer seus lembretes por lugar.", "fixture-model", "{}",
        new("openai", "fixture-model", true, 1, "{\"purpose\":\"reminder_intervention\"}", 200, null, null, null));

    private static async Task<Fixture> Seed(AegisDbContext db, Clock clock, int rules = 8, string code = "location_services_disabled")
    {
        var android = new AegisNode(Guid.NewGuid(), "Android sintético", NodePlatform.Android, "0.7.2", 1, clock.Now);
        var windows = new AegisNode(Guid.NewGuid(), "Windows sintético", NodePlatform.Windows, "0.7.2", 1, clock.Now);
        db.Nodes.AddRange(android, windows);
        db.NodeCapabilities.AddRange(new(android.Id, NotificationContract.ConversationCapability, 1), new(windows.Id, NotificationContract.ConversationCapability, 1));
        var conversations = Enumerable.Range(0, 5).Select(i => {
            var conversation = new Conversation($"Origem sintética {i}");
            conversation.AddMessage("user", $"Crie um lembrete sintético {i}.", clock.Now.AddMinutes(-10 + i));
            return conversation;
        }).ToArray();
        db.Conversations.AddRange(conversations);
        var reminders = Enumerable.Range(0, rules).Select(i => {
            var rule = new GeographicReminderRule($"Tarefa sintética {i}", [Guid.NewGuid()], android.Id, "enter", false, null, null, "UTC", clock.Now.AddSeconds(i), conversations[i % 5].Id);
            rule.Confirm(rule.Revision, "recoverable_error", code, clock.Now.AddSeconds(i));
            return rule;
        }).ToArray();
        db.Set<GeographicReminderRule>().AddRange(reminders);
        await db.SaveChangesAsync();
        clock.Now = clock.Now.AddSeconds(10);
        return new(android.Id, windows.Id, conversations.Select(c => c.Id).ToArray(), reminders.Select(r => r.Id).ToArray());
    }

    private static async Task<InterventionClaim> Claim(AegisDbContext db, Clock clock)
    {
        var store = new ReminderInterventionStore(db, clock);
        await store.ReconcileAsync(default);
        return Assert.IsType<InterventionClaim>(await store.ClaimAsync(default));
    }
    private static async Task ConfirmAll(AegisDbContext db, Clock clock, string status, string? code = null)
    {
        foreach (var rule in await db.Set<GeographicReminderRule>().ToListAsync()) rule.Confirm(rule.Revision, status, code, clock.Now);
        await db.SaveChangesAsync();
    }

    [NodePostgresTests.PostgresFact]
    public Task EightRulesAcrossFiveOriginsCreateOneCommunicationAndOneIntentPerEligibleNode() => NodePostgresTests.WithDatabase(async create => {
        var clock = new Clock(); Fixture ids;
        await using (var seed = create()) ids = await Seed(seed, clock);
        await using (var generation = create()) {
            var store = new ReminderInterventionStore(generation, clock);
            var claim = await Claim(generation, clock);
            Assert.Equal(8, claim.AffectedRuleCount);
            Assert.InRange(Assert.IsAssignableFrom<IReadOnlyList<AffectedReminderSummary>>(claim.ReminderSummaries).Count, 1, 5);
            Assert.Equal(ids.Conversations[2], claim.ConversationId); // newest affected origin: rule 7
            Assert.True(await store.CompleteAsync(claim, Generated(), default));
            Assert.Null(await store.ClaimAsync(default));
        }
        var dispatch = new Dispatcher();
        async Task<bool> Process() {
            await using var db = create();
            return await new ProactiveNotificationProcessor(db, new(db, clock), new NodeRegistry(db, clock), dispatch, clock).ProcessNextAsync(default);
        }
        // Two independent processors race both expansion and delivery against real row/index locks.
        await Task.WhenAll(Process(), Process());
        for (var i = 0; i < 5; i++) await Process();
        await using var check = create();
        Assert.Equal(8, await check.Set<ReminderIntervention>().CountAsync());
        Assert.Equal(8, await check.Set<GeographicReminderRule>().CountAsync(r => r.ErrorCode == "location_services_disabled"));
        Assert.Equal(8, await check.Set<ReminderIntervention>().CountAsync(r => r.Diagnosis != null && r.RequiredAction != null));
        var incident = Assert.Single(await check.Set<SharedReminderIncident>().ToListAsync());
        Assert.Equal(8, incident.AffectedRuleCount);
        Assert.Equal(1, incident.GenerationAttempts);
        var message = Assert.Single(await check.ChatMessages.Where(m => m.IsProactive).ToListAsync());
        Assert.Equal(ids.Conversations[2], message.ConversationId);
        Assert.Equal(2, dispatch.Commands.Count);
        Assert.All(dispatch.Commands, command => Assert.Equal(message.Id, command.Input.MessageId));
        var notifications = await check.Set<ProactiveNotification>().ToListAsync();
        Assert.Single(notifications, n => n.NodeId is null);
        Assert.Equal(2, notifications.Count(n => n.NodeId is not null));
        Assert.Equal(2, notifications.Where(n => n.NodeId is not null).Select(n => n.NodeId).Distinct().Count());
        Assert.Equal(4, await check.Conversations.CountAsync(c => c.Id != message.ConversationId && c.LastProactiveSequence == 0));
    });

    [NodePostgresTests.PostgresFact]
    public Task NewPendingRuleJoinsExistingEpisodeWithoutSuppressingIndividualRecoveryOrReplacingConfirmedDiagnosis() => NodePostgresTests.WithDatabase(async create => {
        var clock = new Clock(); await using var db = create(); var ids = await Seed(db, clock);
        var first = await Claim(db, clock); var store = new ReminderInterventionStore(db, clock);
        Assert.True(await store.CompleteAsync(first, Generated(), default));
        var started = clock.Now;
        var pending = new GeographicReminderRule("Nova regra ainda sincronizando", [Guid.NewGuid()], ids.Android, "enter", false, null, null, "UTC", started, ids.Conversations[4]);
        db.Set<GeographicReminderRule>().Add(pending); await db.SaveChangesAsync();
        Assert.Empty(await store.ReconcileAsync(default));
        foreach (var seconds in new[] { 30, 90, 180 }) {
            clock.Now = started.AddSeconds(seconds);
            var recovery = Assert.Single(await store.ReconcileAsync(default));
            var job = await db.Set<ReminderIntervention>().SingleAsync(j => j.Id == recovery.Id);
            Assert.Equal(pending.Id, job.RuleId); Assert.Equal(first.Id, job.SharedIncidentId);
            await store.RecoveryCompletedAsync(recovery, "accepted", default);
            Assert.Null(await store.ClaimAsync(default));
        }
        var incident = await db.Set<SharedReminderIncident>().SingleAsync();
        Assert.Equal(first.Id, incident.Id); Assert.Equal(9, incident.AffectedRuleCount);
        Assert.Contains("desligados", incident.Diagnosis);
        Assert.Equal(3, (await db.Set<ReminderIntervention>().SingleAsync(j => j.RuleId == pending.Id)).RecoveryAttempts);
        Assert.Single(await db.ChatMessages.Where(m => m.IsProactive).ToListAsync());
    });

    [NodePostgresTests.PostgresFact]
    public Task RuleReclassifiedAsGeometryCannotRemainInGlobalCountOrBlockGlobalRecovery() => NodePostgresTests.WithDatabase(async create => {
        var clock = new Clock(); await using var db = create(); var ids = await Seed(db, clock);
        var store = new ReminderInterventionStore(db, clock); var first = await Claim(db, clock);
        Assert.True(await store.CompleteAsync(first, Generated(), default));
        var rule = await db.Set<GeographicReminderRule>().SingleAsync(r => r.Id == ids.Rules[7]);
        clock.Now = clock.Now.AddSeconds(5);
        rule.InvalidateGeometry(clock.Now); // new operational revision and activation epoch
        rule.Confirm(rule.Revision, "recoverable_error", "geometry_required", clock.Now);
        await db.SaveChangesAsync(); await store.ReconcileAsync(default);
        var geometry = Assert.IsType<InterventionClaim>(await store.ClaimAsync(default));
        Assert.Equal(1, geometry.AffectedRuleCount); Assert.Contains("área válida", geometry.Diagnosis);
        Assert.NotEqual(first.Id, geometry.Id);
        var global = await db.Set<SharedReminderIncident>().SingleAsync(i => i.Id == first.Id);
        Assert.Equal(7, global.AffectedRuleCount); Assert.Contains("desligados", global.Diagnosis);
        Assert.True(await store.CompleteAsync(geometry, Generated(), default));
        foreach (var healthy in await db.Set<GeographicReminderRule>().Where(r => r.Id != rule.Id).ToListAsync()) healthy.Confirm(healthy.Revision, "installed", null, clock.Now);
        await db.SaveChangesAsync(); await store.ReconcileAsync(default);
        clock.Now = clock.Now.AddSeconds(30); await store.ReconcileAsync(default);
        Assert.NotNull((await db.Set<SharedReminderIncident>().SingleAsync(i => i.Id == first.Id)).ResolvedAt);
        Assert.Null((await db.Set<SharedReminderIncident>().SingleAsync(i => i.Id == geometry.Id)).ResolvedAt);
        Assert.Null(await store.ClaimAsync(default));
    });

    [NodePostgresTests.PostgresFact]
    public Task DiagnosticChangesAndNewRulesUpdateOpenIncidentWithoutAnotherGenerationEvenDaysLater() => NodePostgresTests.WithDatabase(async create => {
        var clock = new Clock(); Fixture ids; Guid incidentId; Guid messageId;
        await using (var seed = create()) {
            ids = await Seed(seed, clock, code: "background_location_required");
            var claim = await Claim(seed, clock); incidentId = claim.Id;
            Assert.True(await new ReminderInterventionStore(seed, clock).CompleteAsync(claim, Generated(), default));
            messageId = (await seed.ChatMessages.SingleAsync(m => m.IsProactive)).Id;
        }
        clock.Now = clock.Now.AddMinutes(1);
        await using (var changed = create()) {
            await ConfirmAll(changed, clock, "recoverable_error", "location_services_disabled");
            var additional = new GeographicReminderRule("Nova tarefa sintética", [Guid.NewGuid()], ids.Android, "exit", false, null, null, "UTC", clock.Now, ids.Conversations[4]);
            additional.Confirm(additional.Revision, "recoverable_error", "location_services_disabled", clock.Now);
            changed.Set<GeographicReminderRule>().Add(additional); await changed.SaveChangesAsync();
            var restarted = new ReminderInterventionStore(changed, clock); await restarted.ReconcileAsync(default);
            Assert.Null(await restarted.ClaimAsync(default));
        }
        clock.Now = clock.Now.AddDays(3);
        await using (var restarted = create()) {
            // A fresh confirmed diagnosis also keeps monitoring leases current.
            foreach (var rule in await restarted.Set<GeographicReminderRule>().ToListAsync()) rule.Renew(clock.Now);
            await ConfirmAll(restarted, clock, "recoverable_error", "location_services_disabled");
            var store = new ReminderInterventionStore(restarted, clock); await store.ReconcileAsync(default);
            Assert.Null(await store.ClaimAsync(default));
        }
        await using var check = create();
        var incident = Assert.Single(await check.Set<SharedReminderIncident>().ToListAsync());
        Assert.Equal(incidentId, incident.Id); Assert.Equal(messageId, incident.MessageId);
        Assert.Equal(9, incident.AffectedRuleCount); Assert.Equal(1, incident.GenerationAttempts);
        Assert.Contains("desligados", incident.Diagnosis);
        Assert.Equal(9, await check.Set<ReminderIntervention>().CountAsync());
        Assert.Single(await check.ChatMessages.Where(m => m.IsProactive).ToListAsync());
    });

    [NodePostgresTests.PostgresFact]
    public Task RuleGeometryAndGlobalLocationRemainIndependent() => NodePostgresTests.WithDatabase(async create => {
        var clock = new Clock(); await using var db = create(); var ids = await Seed(db, clock);
        var independent = new GeographicReminderRule("Área sintética inválida", [Guid.NewGuid()], ids.Android, "enter", false, null, null, "UTC", clock.Now, ids.Conversations[3]);
        independent.Confirm(independent.Revision, "recoverable_error", "geometry_required", clock.Now);
        db.Set<GeographicReminderRule>().Add(independent); await db.SaveChangesAsync();
        var store = new ReminderInterventionStore(db, clock); await store.ReconcileAsync(default);
        var claims = new[] { (await store.ClaimAsync(default))!, (await store.ClaimAsync(default))! };
        Assert.All(claims, Assert.NotNull); Assert.Equal(2, claims.Select(c => c.Id).Distinct().Count());
        Assert.Contains(claims, c => c.AffectedRuleCount == 8 && c.Diagnosis.Contains("desligados"));
        Assert.Contains(claims, c => c.AffectedRuleCount == 1 && c.Diagnosis.Contains("área válida"));
        foreach (var claim in claims) Assert.True(await store.CompleteAsync(claim, Generated(), default));
        Assert.Equal(2, await db.Set<SharedReminderIncident>().CountAsync());
        Assert.Equal(2, await db.ChatMessages.CountAsync(m => m.IsProactive));
        Assert.Null(await store.ClaimAsync(default));
    });

    [NodePostgresTests.PostgresFact]
    public Task AcceptedSyncAndOneHealthyScanCannotCloseEpisodeButStableInstallationPermitsNewFailure() => NodePostgresTests.WithDatabase(async create => {
        var clock = new Clock(); Guid firstId;
        await using (var seed = create()) {
            await Seed(seed, clock); var claim = await Claim(seed, clock); firstId = claim.Id;
            Assert.True(await new ReminderInterventionStore(seed, clock).CompleteAsync(claim, Generated(), default));
        }
        clock.Now = clock.Now.AddSeconds(10);
        await using (var sync = create()) {
            foreach (var rule in await sync.Set<GeographicReminderRule>().ToListAsync()) rule.InvalidateMonitoring(clock.Now);
            await sync.SaveChangesAsync();
            var store = new ReminderInterventionStore(sync, clock);
            foreach (var recovery in await store.ReconcileAsync(default)) await store.RecoveryCompletedAsync(recovery, "accepted", default);
            Assert.Null((await sync.Set<SharedReminderIncident>().SingleAsync()).ResolvedAt);
        }
        clock.Now = clock.Now.AddSeconds(30);
        await using (var installed = create()) {
            await ConfirmAll(installed, clock, "installed");
            var store = new ReminderInterventionStore(installed, clock); await store.ReconcileAsync(default);
            Assert.Null((await installed.Set<SharedReminderIncident>().SingleAsync()).ResolvedAt);
            clock.Now = clock.Now.AddSeconds(29); await store.ReconcileAsync(default);
            Assert.Null((await installed.Set<SharedReminderIncident>().SingleAsync()).ResolvedAt);
        }
        clock.Now = clock.Now.AddSeconds(1);
        await using (var stable = create()) {
            await new ReminderInterventionStore(stable, clock).ReconcileAsync(default);
            Assert.NotNull((await stable.Set<SharedReminderIncident>().SingleAsync()).ResolvedAt);
            clock.Now = clock.Now.AddSeconds(1); await ConfirmAll(stable, clock, "recoverable_error", "location_services_disabled");
            var second = await Claim(stable, clock); Assert.NotEqual(firstId, second.Id);
            Assert.True(await new ReminderInterventionStore(stable, clock).CompleteAsync(second, Generated(), default));
            Assert.Equal(2, await stable.Set<SharedReminderIncident>().CountAsync());
            Assert.Equal(2, await stable.ChatMessages.CountAsync(m => m.IsProactive));
        }
    });

    [NodePostgresTests.PostgresFact]
    public Task TransientHealthyAcknowledgementThenSameFailureDoesNotStartAnotherEpisode() => NodePostgresTests.WithDatabase(async create => {
        var clock = new Clock(); await using var db = create(); await Seed(db, clock);
        var first = await Claim(db, clock); var store = new ReminderInterventionStore(db, clock);
        Assert.True(await store.CompleteAsync(first, Generated(), default));
        clock.Now = clock.Now.AddSeconds(10); await ConfirmAll(db, clock, "installed"); await store.ReconcileAsync(default);
        clock.Now = clock.Now.AddSeconds(10); await ConfirmAll(db, clock, "recoverable_error", "location_services_disabled"); await store.ReconcileAsync(default);
        Assert.Null(await store.ClaimAsync(default));
        Assert.Equal(first.Id, (await db.Set<SharedReminderIncident>().SingleAsync()).Id);
        Assert.Single(await db.ChatMessages.Where(m => m.IsProactive).ToListAsync());
    });

    [NodePostgresTests.PostgresFact]
    public Task PartialCancellationUpdatesCountAndFullCancellationInvalidatesAlreadyClaimedGeneration() => NodePostgresTests.WithDatabase(async create => {
        var clock = new Clock(); await using var db = create(); await Seed(db, clock);
        var claimed = await Claim(db, clock); var store = new ReminderInterventionStore(db, clock);
        foreach (var rule in (await db.Set<GeographicReminderRule>().ToListAsync()).Take(3)) rule.SetStatus("cancel", clock.Now);
        await db.SaveChangesAsync(); await store.ReconcileAsync(default);
        Assert.Equal(5, (await db.Set<SharedReminderIncident>().SingleAsync()).AffectedRuleCount);
        foreach (var rule in await db.Set<GeographicReminderRule>().Where(r => r.Status == "active").ToListAsync()) rule.SetStatus("pause", clock.Now);
        await db.SaveChangesAsync(); await store.ReconcileAsync(default);
        Assert.NotNull((await db.Set<SharedReminderIncident>().SingleAsync()).ResolvedAt);
        Assert.False(await store.CompleteAsync(claimed, Generated(), default)); Assert.Null(await store.ClaimAsync(default));
        Assert.Empty(await db.ChatMessages.Where(m => m.IsProactive).ToListAsync());
        Assert.Equal(8, await db.Set<ReminderIntervention>().CountAsync());
    });

    [NodePostgresTests.PostgresFact]
    public Task ConcurrentReconciliationClaimsExpiryAndRepeatedCommitPreserveOnePersistentGeneration() => NodePostgresTests.WithDatabase(async create => {
        var clock = new Clock(); await using (var seed = create()) await Seed(seed, clock);
        async Task Reconcile() { await using var db = create(); await new ReminderInterventionStore(db, clock).ReconcileAsync(default); }
        await Task.WhenAll(Reconcile(), Reconcile());
        async Task<InterventionClaim?> Next() { await using var db = create(); return await new ReminderInterventionStore(db, clock).ClaimAsync(default); }
        var first = Assert.Single(await Task.WhenAll(Next(), Next()), c => c is not null)!;
        DateTimeOffset retry; DateTimeOffset expiredLease;
        await using (var check = create()) {
            var incident = await check.Set<SharedReminderIncident>().SingleAsync();
            retry = incident.NextAttemptAt; expiredLease = incident.LeaseUntil!.Value.AddSeconds(1);
            Assert.Equal(1, incident.GenerationAttempts);
        }
        clock.Now = expiredLease;
        Assert.Null(await Next()); // Expiration alone cannot bypass the persisted retry schedule.
        await using (var stale = create()) Assert.False(await new ReminderInterventionStore(stale, clock).CompleteAsync(first, Generated(), default));
        clock.Now = retry;
        var renewed = Assert.Single(await Task.WhenAll(Next(), Next()), c => c is not null)!;
        Assert.Equal(first.Id, renewed.Id); Assert.NotEqual(first.Token, renewed.Token);
        async Task<bool> Complete(InterventionClaim claim) { await using var db = create(); return await new ReminderInterventionStore(db, clock).CompleteAsync(claim, Generated(), default); }
        var writes = await Task.WhenAll(Complete(renewed), Complete(renewed), Complete(first));
        Assert.Single(writes, success => success);
        await using var final = create(); Assert.Single(await final.Set<SharedReminderIncident>().ToListAsync());
        Assert.Single(await final.ChatMessages.Where(m => m.IsProactive).ToListAsync());
        Assert.Single(await final.Set<ProactiveNotification>().ToListAsync());
        Assert.Null(await Next());
    });

    [NodePostgresTests.PostgresFact]
    public Task DestinationExcludesDeletedOriginsAndCannotBeRedirectedAfterSelection() => NodePostgresTests.WithDatabase(async create => {
        var clock = new Clock(); await using var db = create(); var ids = await Seed(db, clock);
        (await db.Conversations.SingleAsync(c => c.Id == ids.Conversations[2])).Delete(clock.Now); await db.SaveChangesAsync();
        var claim = await Claim(db, clock);
        Assert.Equal(ids.Conversations[1], claim.ConversationId); // newest still-valid origin: rule 6
        (await db.Conversations.SingleAsync(c => c.Id == claim.ConversationId)).Delete(clock.Now); await db.SaveChangesAsync();
        var store = new ReminderInterventionStore(db, clock);
        Assert.False(await store.CompleteAsync(claim, Generated(), default));
        Assert.Empty(await db.ChatMessages.Where(m => m.IsProactive).ToListAsync());
        Assert.Null(await store.ClaimAsync(default));
        Assert.Equal(5, await db.Conversations.CountAsync());
    });

    [NodePostgresTests.PostgresFact]
    public Task DatabaseRejectsDuplicateOpenEpisodeAndDuplicateMessageNodeIntent() => NodePostgresTests.WithDatabase(async create => {
        var clock = new Clock(); SharedReminderIncident incident; ProactiveNotification notification;
        await using (var db = create()) {
            await Seed(db, clock); var claim = await Claim(db, clock); await new ReminderInterventionStore(db, clock).CompleteAsync(claim, Generated(), default);
            incident = await db.Set<SharedReminderIncident>().AsNoTracking().SingleAsync(); notification = await db.Set<ProactiveNotification>().AsNoTracking().SingleAsync();
        }
        await using (var duplicate = create()) {
            incident.Id = Guid.NewGuid(); incident.MessageId = null; duplicate.Set<SharedReminderIncident>().Add(incident);
            var failure = await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(failure.InnerException).SqlState);
        }
        await using (var duplicate = create()) {
            notification.Id = Guid.NewGuid(); duplicate.Set<ProactiveNotification>().Add(notification);
            var failure = await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(failure.InnerException).SqlState);
        }
    });

    [NodePostgresTests.PostgresFact]
    public Task PreviouslyNotifiedIndividualIncidentsAreAdoptedWithoutReplayingHistoricalMessages() => NodePostgresTests.WithDatabase(async create => {
        var clock = new Clock();
        await using (var legacy = create()) {
            await Seed(legacy, clock);
            foreach (var rule in await legacy.Set<GeographicReminderRule>().ToListAsync()) {
                var conversation = await legacy.Conversations.SingleAsync(c => c.Id == rule.SourceConversationId);
                var message = conversation.AddProactiveMessage("Aviso histórico sintético já enviado.", clock.Now);
                legacy.ChatMessages.Add(message);
                legacy.Set<ReminderIntervention>().Add(new() { RuleId = rule.Id, ActivationEpoch = rule.ActivationEpoch, StartedAt = rule.CreatedAt,
                    NextAttemptAt = clock.Now, State = "notified", GenerationAttempts = 1, MessageId = message.Id });
            }
            await legacy.SaveChangesAsync();
        }
        await using var restart = create(); var store = new ReminderInterventionStore(restart, clock);
        await store.ReconcileAsync(default); Assert.Null(await store.ClaimAsync(default));
        var incident = Assert.Single(await restart.Set<SharedReminderIncident>().ToListAsync());
        Assert.Equal("notified", incident.State); Assert.NotNull(incident.MessageId); Assert.Equal(0, incident.GenerationAttempts);
        Assert.Equal(8, incident.AffectedRuleCount);
        Assert.Equal(8, await restart.ChatMessages.CountAsync(m => m.IsProactive));
        Assert.Equal(8, await restart.Set<ReminderIntervention>().CountAsync(j => j.SharedIncidentId == incident.Id && j.State == "notified"));
        Assert.Empty(await restart.Set<ProactiveNotification>().ToListAsync());
        // A historical location message cannot acknowledge a newly independent geometry problem.
        var changed = await restart.Set<GeographicReminderRule>().OrderBy(r => r.Id).FirstAsync();
        changed.Confirm(changed.Revision, "recoverable_error", "geometry_required", clock.Now);
        await restart.SaveChangesAsync(); await store.ReconcileAsync(default);
        var geometry = Assert.IsType<InterventionClaim>(await store.ClaimAsync(default));
        Assert.NotEqual(incident.Id, geometry.Id); Assert.Equal(1, geometry.AffectedRuleCount);
        Assert.Contains("área válida", geometry.Diagnosis);
        Assert.True(await store.CompleteAsync(geometry, Generated(), default));
        Assert.Equal(9, await restart.ChatMessages.CountAsync(m => m.IsProactive));
    });

    [NodePostgresTests.PostgresFact]
    public Task DeletedOriginsAreRecordedWithoutInventingConversationOrCallingGenerator() => NodePostgresTests.WithDatabase(async create => {
        var clock = new Clock(); await using var db = create(); await Seed(db, clock);
        foreach (var conversation in await db.Conversations.ToListAsync()) conversation.Delete(clock.Now);
        await db.SaveChangesAsync(); var store = new ReminderInterventionStore(db, clock);
        await store.ReconcileAsync(default); Assert.Null(await store.ClaimAsync(default));
        var incident = Assert.Single(await db.Set<SharedReminderIncident>().ToListAsync());
        Assert.Equal("source_unavailable", incident.State); Assert.Null(incident.ConversationId);
        Assert.Equal(0, incident.GenerationAttempts); Assert.Equal("source_conversation_unavailable", incident.LastError);
        Assert.Equal(5, await db.Conversations.CountAsync()); Assert.Empty(await db.ChatMessages.Where(m => m.IsProactive).ToListAsync());
    });

    [NodePostgresTests.PostgresFact]
    public Task DifferentObserversCannotBeCorrelatedByMatchingErrorOrTimestamp() => NodePostgresTests.WithDatabase(async create => {
        var clock = new Clock(); await using var db = create(); var ids = await Seed(db, clock);
        var second = new AegisNode(Guid.NewGuid(), "Outro Android sintético", NodePlatform.Android, "0.7.2", 1, clock.Now);
        db.Nodes.Add(second);
        var rule = new GeographicReminderRule("Tarefa no segundo celular", [Guid.NewGuid()], second.Id, "enter", false, null, null, "UTC", clock.Now, ids.Conversations[0]);
        rule.Confirm(rule.Revision, "recoverable_error", "location_services_disabled", clock.Now); db.Set<GeographicReminderRule>().Add(rule); await db.SaveChangesAsync();
        var store = new ReminderInterventionStore(db, clock); await store.ReconcileAsync(default);
        var first = Assert.IsType<InterventionClaim>(await store.ClaimAsync(default));
        var other = Assert.IsType<InterventionClaim>(await store.ClaimAsync(default));
        Assert.NotEqual(first.Id, other.Id); Assert.Equal(new[] { 1, 8 }, new[] { first.AffectedRuleCount, other.AffectedRuleCount }.Order().ToArray());
        Assert.Equal(2, await db.Set<SharedReminderIncident>().Select(i => i.ObserverNodeId).Distinct().CountAsync());
    });

    [NodePostgresTests.PostgresFact]
    public Task AcceptedTransportWithoutInstallationAcknowledgementGroupsObserverConfirmationWithoutInventingCause() => NodePostgresTests.WithDatabase(async create => {
        var clock = new Clock(); await using var db = create(); await Seed(db, clock);
        foreach (var rule in await db.Set<GeographicReminderRule>().ToListAsync()) rule.InvalidateMonitoring(clock.Now);
        await db.SaveChangesAsync(); var started = clock.Now; var store = new ReminderInterventionStore(db, clock);
        foreach (var seconds in new[] { 30, 90, 180 }) {
            clock.Now = started.AddSeconds(seconds);
            var recoveries = await store.ReconcileAsync(default); Assert.Equal(8, recoveries.Count);
            foreach (var recovery in recoveries) await store.RecoveryCompletedAsync(recovery, "accepted", default);
            Assert.Null(await store.ClaimAsync(default));
        }
        clock.Now = started.AddSeconds(270); await store.ReconcileAsync(default);
        var claim = Assert.IsType<InterventionClaim>(await store.ClaimAsync(default));
        Assert.Equal(8, claim.AffectedRuleCount); Assert.Contains("causa exata não foi identificada", claim.Diagnosis);
        Assert.DoesNotContain("desligado", claim.Diagnosis); Assert.DoesNotContain("instalada", claim.Diagnosis);
        Assert.True(await store.CompleteAsync(claim, Generated(), default)); Assert.Null(await store.ClaimAsync(default));
        Assert.Single(await db.Set<SharedReminderIncident>().ToListAsync());
        Assert.Equal(8, await db.Set<ReminderIntervention>().CountAsync(j => j.RecoveryAttempts == 3));
        Assert.Single(await db.ChatMessages.Where(m => m.IsProactive).ToListAsync());
    });

    [NodePostgresTests.PostgresFact]
    public Task RevalidationBeforeModelAndBeforeCommitRejectsObsoleteDiagnosisWithoutPublishingTwice() => NodePostgresTests.WithDatabase(async create => {
        var clock = new Clock(); await using var db = create(); await Seed(db, clock, code: "background_location_required");
        var store = new ReminderInterventionStore(db, clock); var stale = await Claim(db, clock);
        await ConfirmAll(db, clock, "recoverable_error", "location_services_disabled");
        Assert.False(await store.RevalidateAsync(stale, default));
        var current = Assert.IsType<InterventionClaim>(await store.ClaimAsync(default));
        Assert.Equal(stale.Id, current.Id); Assert.Contains("desligados", current.Diagnosis);
        Assert.True(await store.RevalidateAsync(current, default));
        Assert.False(await store.CompleteAsync(stale, Generated(), default));
        Assert.True(await store.CompleteAsync(current, Generated(), default));
        Assert.Single(await db.ChatMessages.Where(m => m.IsProactive).ToListAsync());
    });

    [NodePostgresTests.PostgresFact]
    public Task IneligibleTransientIncidentCannotBlockIndependentImmediateGeometryIntervention() => NodePostgresTests.WithDatabase(async create => {
        var clock = new Clock(); await using var db = create(); var ids = await Seed(db, clock, code: "background_location_required");
        var store = new ReminderInterventionStore(db, clock); await store.ReconcileAsync(default);
        await ConfirmAll(db, clock, "recoverable_error", "native_registration_failed");
        var geometry = new GeographicReminderRule("Outro lugar inválido", [Guid.NewGuid()], ids.Android, "enter", false, null, null, "UTC", clock.Now, ids.Conversations[3]);
        geometry.Confirm(geometry.Revision, "recoverable_error", "geometry_required", clock.Now);
        db.Set<GeographicReminderRule>().Add(geometry); await db.SaveChangesAsync(); await store.ReconcileAsync(default);
        var claim = Assert.IsType<InterventionClaim>(await store.ClaimAsync(default));
        Assert.Equal(geometry.Id, claim.RuleId); Assert.Equal(1, claim.AffectedRuleCount);
        Assert.Contains("área válida", claim.Diagnosis);
        Assert.True(await store.CompleteAsync(claim, Generated(), default)); Assert.Null(await store.ClaimAsync(default));
        Assert.Equal(2, await db.Set<SharedReminderIncident>().CountAsync());
    });

    [NodePostgresTests.PostgresFact]
    public Task RetriesAfterAmbiguousTransportKeepPersistentCommandAndMessageIdentity() => NodePostgresTests.WithDatabase(async create => {
        var clock = new Clock(); await using (var seed = create()) { await Seed(seed, clock); var claim = await Claim(seed, clock); await new ReminderInterventionStore(seed, clock).CompleteAsync(claim, Generated(), default); }
        var dispatch = new Dispatcher { Status = "unconfirmed" };
        async Task Process() { await using var db = create(); await new ProactiveNotificationProcessor(db, new(db, clock), new NodeRegistry(db, clock), dispatch, clock).ProcessNextAsync(default); }
        await Process(); await Process(); await Process();
        var first = dispatch.Commands.ToArray(); Assert.Equal(2, first.Length);
        clock.Now = clock.Now.AddSeconds(10); dispatch.Status = "duplicate";
        await Process(); await Process();
        Assert.Equal(4, dispatch.Commands.Count);
        foreach (var command in first) {
            Assert.Equal(2, dispatch.Commands.Count(c => c.CommandId == command.CommandId && c.Input.MessageId == command.Input.MessageId));
        }
        await using var check = create();
        Assert.Equal(2, await check.Set<ProactiveNotification>().CountAsync(n => n.NodeId != null && n.State == "accepted"));
        Assert.Single(await check.ChatMessages.Where(m => m.IsProactive).ToListAsync());
    });

    private sealed class Dispatcher : INodeNotificationDispatcher
    {
        public string Status = "success";
        public readonly System.Collections.Concurrent.ConcurrentBag<NodeNotificationCommand> Commands = [];
        public Task<NotificationDispatchResult> DispatchAsync(Guid actor, NotificationShowRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<NotificationDispatchResult> DispatchToNodeAsync(Guid id, NodeNotificationCommand command, CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.FromResult(new NotificationDispatchResult(new(id, "synthetic"), "online", "live_websocket", Status, command.CommandId));
        }
    }
}
