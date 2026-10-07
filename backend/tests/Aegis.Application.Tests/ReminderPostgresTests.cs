using Aegis.Application.Nodes;
using Aegis.Application.Observability;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Nodes;
using Aegis.Infrastructure.Persistence;
using Aegis.Infrastructure.Reminders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Aegis.Application.Tests;

// Runs against disposable schemas in a disposable PostgreSQL database, never production.
public sealed class ReminderPostgresTests
{
    public sealed class PostgresFactAttribute : FactAttribute
    {
        public PostgresFactAttribute() { if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AEGIS_REMINDER_TEST_DATABASE"))) Skip = "Set AEGIS_REMINDER_TEST_DATABASE to a disposable PostgreSQL database."; }
    }
    private static AegisDbContext CreateDb(string schema) => new(new DbContextOptionsBuilder<AegisDbContext>()
        .UseNpgsql(Environment.GetEnvironmentVariable("AEGIS_REMINDER_TEST_DATABASE") + ";Search Path=" + schema,
            options => options.MigrationsHistoryTable("__EFMigrationsHistory", schema)).Options);
    private static async Task SeedNode(AegisDbContext db, Guid id, DateTimeOffset now)
    {
        db.Nodes.Add(new(id, "fixture", NodePlatform.Windows, "0.7.0-unstable.11", 1, now));
        db.NodeCapabilities.Add(new(id, "notification.show", 1)); await db.SaveChangesAsync();
    }
    private static ReminderProcessor Processor(AegisDbContext db, ReminderTests.TestClock clock, ReminderTests.LiveConnections live,
        INodeLiveNotifications delivery, AegisMetrics metrics)
    {
        var background = new ReminderTests.BackgroundRoutes(); var registry = new NodeRegistry(db, clock, live, background);
        var resolver = new NodeTargetResolver(registry, live, background);
        return new(db, new(db), resolver, new NodeNotificationDispatcher(registry, resolver, live, delivery,
            new ReminderTests.NativeDelivery(), background, clock), clock, metrics);
    }
    [PostgresFact]
    public async Task MigrationAtomicClaimRecoveryIndependentLifetimeAndDispatchAreRealPostgres()
    {
        var schema = "reminders_" + Guid.NewGuid().ToString("N"); var clock = new ReminderTests.TestClock();
        await using var db = CreateDb(schema);
        try
        {
            await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA \"" + schema + "\""); await db.Database.MigrateAsync();
            var conversation = new Conversation(); db.Conversations.Add(conversation);
            var r = new Reminder("ração", clock.GetUtcNow().AddHours(1), "America/Sao_Paulo", conversation.Id, clock.GetUtcNow());
            db.Reminders.Add(r); var nodeId = Guid.NewGuid(); await SeedNode(db, nodeId, clock.GetUtcNow());
            db.Conversations.Remove(conversation); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            Assert.Null((await db.Reminders.FindAsync(r.Id))!.SourceConversationId);
            clock.Advance(TimeSpan.FromHours(2));
            await using var first = CreateDb(schema); await using var second = CreateDb(schema);
            var claims = await Task.WhenAll(new ReminderStore(first).ClaimAsync(clock.GetUtcNow(), default), new ReminderStore(second).ClaimAsync(clock.GetUtcNow(), default));
            Assert.Single(claims, c => c is not null);
            await using var restarted = CreateDb(schema);
            Assert.Null(await new ReminderStore(restarted).ClaimAsync(clock.GetUtcNow(), default));
            var commandId = Guid.NewGuid();
            first.ReminderDeliveryAttempts.Add(new(r.Id, 1, nodeId, commandId, clock.GetUtcNow().AddSeconds(300), clock.GetUtcNow())); await first.SaveChangesAsync();
            clock.Advance(TimeSpan.FromMinutes(3));
            var live = new ReminderTests.LiveConnections(); live.Nodes[nodeId] = [new("notification.show", 1)];
            var native = new ReminderTests.NativeDelivery(); native.Results.Enqueue("duplicate"); using var metrics = new AegisMetrics();
            Assert.True(await Processor(restarted, clock, live, native, metrics).ProcessNextAsync());
            Assert.Equal(commandId, Assert.Single(native.Commands).Command.CommandId);
            await using var final = CreateDb(schema);
            Assert.Null(await new ReminderStore(final).ClaimAsync(clock.GetUtcNow(), default));
            var attempts = await final.ReminderDeliveryAttempts.OrderBy(a => a.Attempt).ToListAsync();
            Assert.Equal("outcome_unknown", attempts[0].Result); Assert.Equal("duplicate", attempts[1].Result); Assert.NotNull(attempts[1].AcceptedAt);
            // The database enforces one attempt number per reminder, including after restart.
            final.ReminderDeliveryAttempts.Add(new(r.Id, 2, nodeId, commandId, clock.GetUtcNow().AddSeconds(60), clock.GetUtcNow()));
            await Assert.ThrowsAsync<DbUpdateException>(() => final.SaveChangesAsync());
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP SCHEMA IF EXISTS \"" + schema + "\" CASCADE"); }
    }
    [PostgresFact]
    public async Task NetworkSendDoesNotHoldReminderLockAndStaleWorkerCannotOverwriteRecovery()
    {
        var schema = "network_" + Guid.NewGuid().ToString("N"); var clock = new ReminderTests.TestClock();
        await using var setup = CreateDb(schema);
        try
        {
            await setup.Database.ExecuteSqlRawAsync("CREATE SCHEMA \"" + schema + "\""); await setup.Database.MigrateAsync();
            var reminder = new Reminder("lock", clock.GetUtcNow().AddMinutes(1), "America/Sao_Paulo", null, clock.GetUtcNow());
            setup.Reminders.Add(reminder); var nodeId = Guid.NewGuid(); await SeedNode(setup, nodeId, clock.GetUtcNow());
            clock.Advance(TimeSpan.FromMinutes(1)); var live = new ReminderTests.LiveConnections(); live.Nodes[nodeId] = [new("notification.show", 1)];
            var blocked = new BlockingNative(); using var metrics = new AegisMetrics(); await using var processing = CreateDb(schema);
            var dispatch = Processor(processing, clock, live, blocked, metrics).ProcessNextAsync();
            await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            try
            {
                await using var another = CreateDb(schema); var store = new ReminderStore(another);
                // An interaction can commit while the network is blocked; no row/advisory transaction survives I/O.
                await store.LockedAsync(reminder.Id, r => { r.Open(clock.GetUtcNow()); return Task.FromResult(true); }, default).WaitAsync(TimeSpan.FromSeconds(5));
                clock.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));
                Assert.NotNull(await store.ClaimAsync(clock.GetUtcNow(), default).WaitAsync(TimeSpan.FromSeconds(5)));
                // Recovery on yet another process reuses the command. Its accepted outcome wins.
                clock.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));
                var native = new ReminderTests.NativeDelivery(); native.Results.Enqueue("duplicate");
                await using var recovered = CreateDb(schema); await Processor(recovered, clock, live, native, metrics).ProcessNextAsync();
                Assert.Equal(blocked.Command!.CommandId, Assert.Single(native.Commands).Command.CommandId);
            }
            finally { blocked.Release.TrySetResult(); }
            Assert.True(await dispatch);
            await using var actual = CreateDb(schema);
            Assert.Equal(ReminderStatus.Triggered, (await actual.Reminders.SingleAsync()).Status);
            Assert.Equal("outcome_unknown", (await actual.ReminderDeliveryAttempts.OrderBy(a => a.Attempt).FirstAsync()).Result);
            Assert.Equal("duplicate", (await actual.ReminderDeliveryAttempts.OrderBy(a => a.Attempt).LastAsync()).Result);
        }
        finally { await setup.Database.ExecuteSqlRawAsync("DROP SCHEMA IF EXISTS \"" + schema + "\" CASCADE"); }
    }
    [PostgresFact]
    public async Task LegacyMigrationRetainsAuditAndAvoidsReplayingAcceptedWebPush()
    {
        var schema = "legacy_" + Guid.NewGuid().ToString("N"); var clock = new ReminderTests.TestClock();
        await using var db = CreateDb(schema);
        try
        {
            await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA \"" + schema + "\"");
            await db.GetService<IMigrator>().MigrateAsync("20261003210008_Stage06NodePushRegistration");
            var reminder = new Reminder("legacy", clock.GetUtcNow().AddMinutes(1), "America/Sao_Paulo", null, clock.GetUtcNow());
            db.Reminders.Add(reminder); var subscription = new PushSubscription("https://fcm.googleapis.com/legacy", "key", "auth", Guid.NewGuid(), null, clock.GetUtcNow());
            db.PushSubscriptions.Add(subscription); await db.SaveChangesAsync(); clock.Advance(TimeSpan.FromMinutes(1));
            reminder.Claim(Guid.NewGuid(), clock.GetUtcNow()); reminder.BeginTrigger(clock.GetUtcNow()); reminder.Finish(clock.GetUtcNow().AddSeconds(10), true, clock.GetUtcNow()); await db.SaveChangesAsync();
            var auditId = Guid.NewGuid(); var now = clock.GetUtcNow();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO reminder_delivery_attempts ("Id", "ReminderId", "PushSubscriptionId", "Attempt", "AttemptedAt", "AcceptedAt", "HttpStatus", "CreatedAt", "UpdatedAt")
                VALUES ({auditId}, {reminder.Id}, {subscription.Id}, 1, {now}, {now}, 201, {now}, {now})
                """);
            await db.Database.MigrateAsync(); db.ChangeTracker.Clear();
            Assert.Equal(ReminderStatus.Triggered, (await db.Reminders.SingleAsync()).Status);
            Assert.Null(await new ReminderStore(db).ClaimAsync(now.AddMinutes(10), default));
            Assert.Equal(1, await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM reminder_delivery_attempts").SingleAsync());
            Assert.Empty(await db.ReminderDeliveryAttempts.ToListAsync()); Assert.Single(await db.PushSubscriptions.ToListAsync());
            // Downgrade/reapply keeps old audit and allows a clean model transition.
            await db.GetService<IMigrator>().MigrateAsync("20261003210008_Stage06NodePushRegistration"); await db.Database.MigrateAsync();
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP SCHEMA IF EXISTS \"" + schema + "\" CASCADE"); }
    }
    private sealed class BlockingNative : INodeLiveNotifications
    {
        public NodeNotificationCommand? Command { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<NodeLiveNotificationResult> SendAsync(Guid id, NodeNotificationCommand command, CancellationToken ct)
        {
            Command = command; Started.TrySetResult(); await Release.Task.WaitAsync(ct); return new("success");
        }
    }
}
