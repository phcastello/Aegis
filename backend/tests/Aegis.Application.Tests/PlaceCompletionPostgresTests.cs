using System.Text.Json;
using Aegis.Application.Nodes;
using Aegis.Application.Places;
using Aegis.Application.Reminders;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Nodes;
using Aegis.Infrastructure.Persistence;
using Aegis.Infrastructure.Places;
using Aegis.Infrastructure.Reminders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Aegis.Application.Tests;

// Synthetic places, disposable identities and a random schema in the guarded test database.
public sealed class PlaceCompletionPostgresTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-10-15T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static PlaceService Places(AegisDbContext db, Clock clock) => new(db, clock, Options.Create(new LocationContextOptions()));
    private static PlaceCircle Circle(int index = 0) => new(Guid.NewGuid(), 10 + index * .000123456789, 20 + index * .000987654321, 100 + index);
    private static async Task WithSchema(Func<Func<AegisDbContext>, Task> test, bool migrate = true)
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("AEGIS_NODE_TEST_DATABASE"));
        if (connection.Database != "aegis_nodes_test") throw new InvalidOperationException("Only the disposable aegis_nodes_test database is accepted.");
        var schema = "places_" + Guid.NewGuid().ToString("N"); connection.SearchPath = schema;
        AegisDbContext Create() => new(new DbContextOptionsBuilder<AegisDbContext>().UseNpgsql(connection.ConnectionString,
            o => o.MigrationsHistoryTable("__EFMigrationsHistory", schema)).Options);
        await using var initial = Create(); await initial.Database.ExecuteSqlRawAsync("CREATE SCHEMA \"" + schema + "\"");
        try { if (migrate) await initial.Database.MigrateAsync(); await test(Create); }
        finally { await initial.Database.ExecuteSqlRawAsync("DROP SCHEMA IF EXISTS \"" + schema + "\" CASCADE"); }
    }
    private static async Task<(Guid Id, string Credential)> Pair(AegisDbContext db, Clock clock, string platform = "android")
    {
        var registry = new NodeRegistry(db, clock);
        var request = NodeIdentityTests.Request((await registry.CreateCodeAsync(null)).Code, platform);
        var receipt = await registry.PairAsync(request); var node = await registry.FinalizeAsync(new(request.AttemptId, receipt.Credential));
        return (node.Id, receipt.Credential);
    }
    private static async Task ConfigureObserver(AegisDbContext db, Clock clock, Guid id)
    {
        db.NodeCapabilities.Add(new(id, "geofence.monitor", 1)); db.NodeCapabilities.Add(new(id, "location.current", 1)); await db.SaveChangesAsync();
        await Places(db, clock).ConfigureAsync(new(NodeId: id, Mode: "dynamic"));
    }
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [NodePostgresTests.PostgresFact]
    public Task Stage07UpgradeKeepsNodeReminderIdsAndAcceptedHistoryAndEnablesReceivingByDefault() => WithSchema(async create => {
        await using var db = create(); await db.GetService<IMigrator>().MigrateAsync("20261007184246_Stage07NativeReminderDelivery");
        var nodeId = Guid.NewGuid(); var reminderId = Guid.NewGuid(); var attemptId = Guid.NewGuid(); var commandId = Guid.NewGuid();
        var now = DateTimeOffset.Parse("2026-10-15T12:00:00Z"); var due = now.AddMinutes(-1);
        // Seed only the columns that really exist in the accepted Stage07 schema.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO aegis_nodes ("Id","Name","Platform","Enabled","AppVersion","ProtocolVersion","PairedAt","CreatedAt","UpdatedAt","TargetPriority")
            VALUES ({nodeId}, 'Synthetic upgrade node', 'Android', TRUE, '0.7.0-unstable.11', 1, {now}, {now}, {now}, 7)
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO reminders ("Id","Text","DueAtUtc","TimeZoneId","Status","TriggeredAt","AcknowledgedAt","NextAttemptAt","CreatedAt","UpdatedAt")
            VALUES ({reminderId}, 'Synthetic preserved history', {due}, 'UTC', 'Triggered', {due}, {now}, {due}, {due}, {now})
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO reminder_node_delivery_attempts ("Id","ReminderId","NodeId","CommandId","CommandExpiresAt","Attempt","AttemptedAt","CompletedAt","AcceptedAt","Result","Transport","OutcomeAmbiguous","CreatedAt","UpdatedAt")
            VALUES ({attemptId}, {reminderId}, {nodeId}, {commandId}, {now.AddMinutes(4)}, 1, {due}, {now}, {now}, 'accepted', 'fcm', FALSE, {due}, {now})
            """);
        await db.Database.MigrateAsync();
        await using var restarted = create(); var node = await restarted.Nodes.SingleAsync(); var reminder = await restarted.Reminders.SingleAsync(); var attempt = await restarted.ReminderDeliveryAttempts.SingleAsync();
        Assert.Equal(nodeId, node.Id); Assert.True(node.ReceiveReminders); Assert.Equal(7, node.TargetPriority);
        Assert.Equal(reminderId, reminder.Id); Assert.Equal(ReminderStatus.Triggered, reminder.Status); Assert.Equal(now, reminder.AcknowledgedAt);
        Assert.Equal("allEligible", reminder.DeliveryPolicy); Assert.Equal(1, reminder.Revision); Assert.Equal(1, reminder.Generation); Assert.Null(reminder.SeriesId);
        Assert.Equal(attemptId, attempt.Id); Assert.Equal(nodeId, attempt.NodeId); Assert.Equal(commandId, attempt.CommandId); Assert.Equal(now, attempt.AcceptedAt); Assert.Equal("fcm", attempt.Transport);
        Assert.Empty(await restarted.Set<Place>().ToArrayAsync()); Assert.Empty(await restarted.Set<ReminderSeries>().ToArrayAsync());
    }, migrate: false);

    [NodePostgresTests.PostgresFact]
    public Task PersonalSourceSwitchAndConcurrentSelectionsKeepExactlyOneSourceAndDatabaseEnforcesIt() => WithSchema(async create => {
        var clock = new Clock(); Guid a; Guid b; Guid c;
        await using (var initial = create()) {
            a = (await Pair(initial, clock)).Id; b = (await Pair(initial, clock)).Id; c = (await Pair(initial, clock)).Id;
            await ConfigureObserver(initial, clock, a); await ConfigureObserver(initial, clock, b); await ConfigureObserver(initial, clock, c);
            await Places(initial, clock).ConfigureAsync(new(SourceNodeId: a));
            await Places(initial, clock).ConfigureAsync(new(SourceNodeId: b));
            Assert.Equal(b, (await Places(initial, clock).SettingsAsync()).SourceNodeId);
        }
        async Task Select(Guid id) { await using var db = create(); await Places(db, clock).ConfigureAsync(new(SourceNodeId: id)); }
        await Task.WhenAll(Select(a), Select(b));
        await using (var restarted = create()) {
            var source = Assert.Single(await restarted.Set<NodeLocationProfile>().Where(p => p.IsUserSource).ToArrayAsync()); Assert.Contains(source.NodeId, new[] { a, b });
            Assert.Equal(3, await restarted.Set<NodeLocationProfile>().CountAsync());
        }
        await using (var bypass = create()) {
            var profile = await bypass.Set<NodeLocationProfile>().SingleAsync(p => p.NodeId == c); profile.Select(true);
            await Assert.ThrowsAsync<DbUpdateException>(() => bypass.SaveChangesAsync());
        }
        await using var final = create(); Assert.Equal(1, await final.Set<NodeLocationProfile>().CountAsync(p => p.IsUserSource));
    });

    [NodePostgresTests.PostgresFact]
    public Task CrossedHierarchyEditsCannotCreateCycleAndReferencedGeometrySurvivesRejectedMutation() => WithSchema(async create => {
        var clock = new Clock(); PlaceView first; PlaceView second;
        await using (var initial = create()) {
            first = await Places(initial, clock).SaveAsync(null, new("Synthetic A", "property", null, [Circle()], []));
            second = await Places(initial, clock).SaveAsync(null, new("Synthetic B", "property", null, [Circle(1)], []));
        }
        async Task<string> Parent(PlaceView item, Guid parent) {
            await using var db = create(); try { await Places(db, clock).SaveAsync(item.Id, new(item.Name, item.Type, parent, item.Circles, item.Aliases, item.Revision)); return "saved"; }
            catch (NodeException e) { return e.Code; }
        }
        var outcomes = await Task.WhenAll(Parent(first, second.Id), Parent(second, first.Id));
        Assert.Single(outcomes, s => s == "saved"); Assert.Single(outcomes, s => s == "place_cycle");
        await using var restarted = create(); var all = await restarted.Set<Place>().AsNoTracking().ToDictionaryAsync(p => p.Id);
        Assert.All(all.Values, p => Assert.False(PlaceGeometry.WouldCycle(p.Id, p.ParentId, all.ToDictionary(k => k.Key, v => v.Value.ParentId))));
        var parentPlace = all.Values.Single(p => p.ParentId is null); var child = all.Values.Single(p => p.ParentId is not null);
        await Assert.ThrowsAsync<NodeException>(() => Places(restarted, clock).DeleteAsync(parentPlace.Id, parentPlace.Revision));
        var phone = await Pair(restarted, clock); await ConfigureObserver(restarted, clock, phone.Id);
        var room = await Places(restarted, clock).SaveAsync(null, new("Synthetic inherited outdoor area", "outdoor", parentPlace.Id, [], []));
        await Places(restarted, clock).SaveRuleAsync(null, new("Synthetic task", [room.Id], phone.Id, "exit"));
        await Assert.ThrowsAsync<NodeException>(() => Places(restarted, clock).SaveAsync(parentPlace.Id, new(parentPlace.Name, parentPlace.Type, null, [], [], parentPlace.Revision)));
        // Reuse the same unit of work after rejection: a later valid write must not commit rejected tracked changes.
        await Places(restarted, clock).SaveAsync(null, new("Synthetic follow-up", "property", null, [Circle(8)], []));
        await using var final = create(); Assert.NotEmpty((await final.Set<Place>().SingleAsync(p => p.Id == parentPlace.Id)).Circles);
        Assert.Equal(parentPlace.Id, (await final.Set<Place>().SingleAsync(p => p.Id == room.Id)).ParentId);
        Assert.NotEqual(Guid.Empty, child.Id);
    });

    [NodePostgresTests.PostgresFact]
    public Task GeographicEventConcurrentReplayAndRestartCreateOneOccurrenceThenLocalAcknowledgementWins() => WithSchema(async create => {
        var clock = new Clock(); Guid phoneId; GeographicRuleView rule;
        await using (var initial = create()) {
            phoneId = (await Pair(initial, clock)).Id; await ConfigureObserver(initial, clock, phoneId);
            var place = await Places(initial, clock).SaveAsync(null, new("Synthetic boundary", "property", null, [Circle()], []));
            rule = await Places(initial, clock).SaveRuleAsync(null, new("Synthetic offline task", [place.Id], phoneId, "exit", true));
        }
        clock.Now += TimeSpan.FromSeconds(2);
        var request = new GeographicEventWrite(Guid.NewGuid(), Guid.NewGuid(), rule.Id, rule.Revision, "exit", clock.Now.AddSeconds(-1), 1, clock.Now);
        async Task<JsonElement> Event(GeographicEventWrite input) { await using var db = create(); return Json(await Places(db, clock).EventAsync(phoneId, input)); }
        var outcomes = await Task.WhenAll(Event(request), Event(request)); Assert.Single(outcomes, p => p.GetProperty("duplicate").GetBoolean());
        Assert.True((await Event(request)).GetProperty("duplicate").GetBoolean());
        await using (var restarted = create()) {
            Assert.Single(await restarted.Set<GeographicReminderEvent>().ToArrayAsync()); var reminder = Assert.Single(await restarted.Reminders.ToArrayAsync());
            Assert.Equal(request.OccurrenceId, reminder.Id); Assert.Equal(request.EventId, reminder.SourceEventId); Assert.Equal(request.PostedAt, reminder.ObserverPostedAt); Assert.Null(reminder.AcknowledgedAt);
            var recipient = Assert.Single(await restarted.Set<ReminderRecipient>().ToArrayAsync()); Assert.Equal("android_local", recipient.Transport); Assert.Equal(request.PostedAt, recipient.PostedAt); Assert.Null(recipient.ProviderAcceptedAt);
            var action = new ReminderActionRequest(Guid.NewGuid(), 1, 1, "acknowledge");
            var applied = await new ReminderActionStore(restarted, new(restarted), clock).ApplyAsync(reminder.Id, action, default); Assert.True(applied.Applied);
        }
        await using (var final = create()) {
            var reminder = await final.Reminders.SingleAsync(); Assert.Equal(clock.Now, reminder.AcknowledgedAt); Assert.Equal(2, reminder.Revision); Assert.Equal(1, reminder.Generation);
            var conflict = await Assert.ThrowsAsync<NodeException>(() => Places(final, clock).EventAsync(phoneId, request with { EventId = Guid.NewGuid(), OccurrenceId = Guid.NewGuid() })); Assert.Equal("event_conflict", conflict.Code);
        }
    });

    [NodePostgresTests.PostgresFact]
    public Task ExpiredLocalLeaseIsRecoverableUntilFreshNativeManifestConfirmation() => WithSchema(async create => {
        var clock = new Clock(); Guid phoneId; Guid ruleId;
        await using (var initial = create()) {
            phoneId = (await Pair(initial, clock)).Id; await ConfigureObserver(initial, clock, phoneId);
            var place = await Places(initial, clock).SaveAsync(null, new("Synthetic lease place", "property", null, [Circle()], []));
            var rule = await Places(initial, clock).SaveRuleAsync(null, new("Synthetic lease task", [place.Id], phoneId, "enter", true)); ruleId = rule.Id;
            var manifest = Json(await Places(initial, clock).ManifestAsync(phoneId));
            await Places(initial, clock).ConfirmAsync(phoneId, new(manifest.GetProperty("revision").GetString()!, [new(rule.Id, rule.Revision, "active")]));
            Assert.Equal("active", (await Places(initial, clock).RulesAsync()).Single().SyncStatus);
        }
        clock.Now += TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1);
        await using var restarted = create(); var stale = (await Places(restarted, clock).RulesAsync()).Single();
        Assert.Equal("recoverable_error", stale.SyncStatus); Assert.Equal("local_lease_expired", stale.ErrorCode);
        var fresh = Json(await Places(restarted, clock).ManifestAsync(phoneId));
        // Fetch/renewal is not proof that Android installed the renewed native registrations.
        Assert.Equal("recoverable_error", (await Places(restarted, clock).RulesAsync()).Single().SyncStatus);
        await Places(restarted, clock).ConfirmAsync(phoneId, new(fresh.GetProperty("revision").GetString()!, [new(ruleId, stale.Revision, "active")]));
        var current = (await Places(restarted, clock).RulesAsync()).Single(); Assert.Equal("active", current.SyncStatus); Assert.Null(current.ErrorCode); Assert.True(current.LeaseUntil > clock.Now);
    });

    [NodePostgresTests.PostgresFact]
    public Task RemovingSelectedObserverInvalidatesIdentityAndNullsHistoryReferencesWithoutDeletingOccurrences() => WithSchema(async create => {
        var clock = new Clock(); Guid actor; Guid observer; string observerCredential; Guid occurrenceId;
        await using (var initial = create()) {
            actor = (await Pair(initial, clock, "windows")).Id; var phone = await Pair(initial, clock); observer = phone.Id; observerCredential = phone.Credential;
            await ConfigureObserver(initial, clock, observer); await Places(initial, clock).ConfigureAsync(new(SourceNodeId: observer));
            var place = await Places(initial, clock).SaveAsync(null, new("Synthetic retained place", "property", null, [Circle()], []));
            var rule = await Places(initial, clock).SaveRuleAsync(null, new("Synthetic retained task", [place.Id], observer, "enter", true)); clock.Now += TimeSpan.FromSeconds(2);
            occurrenceId = Guid.NewGuid(); await Places(initial, clock).EventAsync(observer, new(Guid.NewGuid(), occurrenceId, rule.Id, 1, "enter", clock.Now.AddSeconds(-1), 1, clock.Now));
            initial.ReminderDeliveryAttempts.Add(new(occurrenceId, 1, observer, Guid.NewGuid(), clock.Now.AddMinutes(5), clock.Now));
            initial.NodePushRegistrations.Add(new(observer, "isolated-not-a-provider-token", new byte[32], clock.Now)); await initial.SaveChangesAsync();
        }
        await using (var removal = create()) await new NodeRegistry(removal, clock).RemoveAsync(actor, observer);
        await using var restarted = create();
        Assert.False(await restarted.Nodes.AnyAsync(n => n.Id == observer)); Assert.False(await restarted.NodeCredentials.AnyAsync(n => n.NodeId == observer)); Assert.False(await restarted.NodePushRegistrations.AnyAsync(n => n.NodeId == observer));
        Assert.Empty(await restarted.Set<NodeLocationProfile>().Where(p => p.IsUserSource || p.NodeId == observer).ToArrayAsync());
        Assert.Equal(occurrenceId, (await restarted.Reminders.SingleAsync()).Id); Assert.Null((await restarted.ReminderDeliveryAttempts.SingleAsync()).NodeId); Assert.Null((await restarted.Set<ReminderRecipient>().SingleAsync()).NodeId); Assert.Null((await restarted.Set<GeographicReminderEvent>().SingleAsync()).ObserverNodeId);
        Assert.Equal("cancelled", (await restarted.Set<GeographicReminderRule>().SingleAsync()).Status); Assert.Equal("node_authentication_required", (await new NodeRegistry(restarted, clock).AuthenticateAsync(observerCredential)).Error);
        Assert.DoesNotContain(await new NodeRegistry(restarted, clock).ListAsync(actor), n => n.Id == observer);
    });
}
