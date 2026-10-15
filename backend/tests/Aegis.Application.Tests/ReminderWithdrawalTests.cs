using Aegis.Application.Nodes;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Nodes;
using Aegis.Infrastructure.Persistence;
using Aegis.Infrastructure.Reminders;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class ReminderWithdrawalTests
{
    [Fact]
    public void WithdrawalIdentityLifetimeAndCumulativeBudgetAreFixedBeforeEveryAttempt()
    {
        var start = DateTimeOffset.Parse("2026-10-15T12:00Z"); var recipient = new ReminderRecipient(Guid.NewGuid(), 1, Guid.NewGuid(), start);
        Guid? commandId = null;
        foreach (var seconds in new[] { 0, 30, 90, 210 })
        {
            Assert.True(recipient.CanBeginWithdrawal(start.AddSeconds(seconds)));
            recipient.BeginWithdrawal(start.AddSeconds(seconds)); commandId ??= recipient.WithdrawalCommandId;
            Assert.Equal(commandId, recipient.WithdrawalCommandId); Assert.Equal(start.AddSeconds(300), recipient.WithdrawalCommandExpiresAt);
            Assert.False(recipient.CanBeginWithdrawal(start.AddSeconds(seconds + 1)));
        }
        Assert.Equal(4, recipient.WithdrawalAttempts); Assert.Null(recipient.WithdrawalNextAttemptAt);
        Assert.False(recipient.CanBeginWithdrawal(start.AddMinutes(10)));
        Assert.Throws<InvalidOperationException>(() => recipient.BeginWithdrawal(start.AddMinutes(10)));
        var late = new ReminderRecipient(Guid.NewGuid(), 1, Guid.NewGuid(), start); late.BeginWithdrawal(start);
        Assert.False(late.CanBeginWithdrawal(start.AddSeconds(300))); // Expiry stops delayed retries even with remaining budget.
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("failed")]
    public async Task LostPushReceiptOr503UsesAtMostFourReplaysThenPerformsNoInventoryOrTransportWork(string result)
    {
        await using var db = new AegisDbContext(new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var clock = new ReminderTests.TestClock(); var (node, reminder, recipient) = Seed(db, clock);
        await db.SaveChangesAsync(); var nodes = new Inventory(node); var native = new Dispatch(result); var reconciler = new ReminderWithdrawalReconciler(db, nodes, native, clock);
        var started = clock.GetUtcNow();
        foreach (var seconds in new[] { 0, 30, 90, 210 })
        {
            clock.Advance(started.AddSeconds(seconds) - clock.GetUtcNow()); await reconciler.ReconcileAsync(default);
            var saved = await db.Set<ReminderRecipient>().AsNoTracking().SingleAsync(); Assert.Null(saved.WithdrawnAt);
            Assert.Equal(result == "accepted", saved.WithdrawalQueuedAt is not null);
            var calls = nodes.Calls; await reconciler.ReconcileAsync(default); Assert.Equal(calls, nodes.Calls);
        }
        Assert.Equal(4, native.Commands.Count); var first = native.Commands[0];
        Assert.All(native.Commands, command => Assert.Equal(first, command)); Assert.Equal(started.AddSeconds(300), first.ExpiresAt);
        var inventory = nodes.Calls; clock.Advance(TimeSpan.FromMinutes(10));
        for (var i = 0; i < 120; i++) await reconciler.ReconcileAsync(default);
        Assert.Equal(inventory, nodes.Calls); Assert.Equal(4, native.Commands.Count);
        Assert.Null(await new ReminderStore(db).ClaimAsync(clock.GetUtcNow(), default)); // ACK stopped reminder delivery independently of withdrawal.
    }

    [Fact]
    public async Task NativeReceiptClosesProviderAcceptedCopyAndStopsSubsequentMaintenance()
    {
        await using var db = new AegisDbContext(new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var clock = new ReminderTests.TestClock(); var (node, _, recipient) = Seed(db, clock); await db.SaveChangesAsync();
        var nodes = new Inventory(node); var native = new Dispatch("accepted"); var reconciler = new ReminderWithdrawalReconciler(db, nodes, native, clock);
        await reconciler.ReconcileAsync(default); var pending = await db.Set<ReminderRecipient>().SingleAsync();
        Assert.NotNull(pending.WithdrawalQueuedAt); Assert.Null(pending.WithdrawnAt);
        clock.Advance(TimeSpan.FromSeconds(5)); pending.Withdraw(clock.GetUtcNow()); await db.SaveChangesAsync(); var received = pending.WithdrawnAt;
        clock.Advance(TimeSpan.FromMinutes(10)); pending.Withdraw(clock.GetUtcNow()); await db.SaveChangesAsync();
        for (var i = 0; i < 120; i++) await reconciler.ReconcileAsync(default);
        Assert.Equal(received, pending.WithdrawnAt); Assert.Single(native.Commands); Assert.Equal(1, nodes.Calls);
    }

    [ReminderPostgresTests.PostgresFact]
    public async Task ConcurrentWithdrawalAndRestartKeepOriginalIdentityExpiryAndBudgetInPostgres()
    {
        var schema = "withdrawal_replay_" + Guid.NewGuid().ToString("N");
        AegisDbContext Db() => new(new DbContextOptionsBuilder<AegisDbContext>().UseNpgsql(Environment.GetEnvironmentVariable("AEGIS_REMINDER_TEST_DATABASE") + ";Search Path=" + schema, o => o.MigrationsHistoryTable("__EFMigrationsHistory", schema)).Options);
        await using var setup = Db(); var clock = new ReminderTests.TestClock();
        try
        {
            await setup.Database.ExecuteSqlRawAsync("CREATE SCHEMA \"" + schema + "\""); await setup.Database.MigrateAsync();
            var (node, _, _) = Seed(setup, clock); await setup.SaveChangesAsync(); var nodes = new Inventory(node); var native = new Dispatch("accepted");
            await using (var first = Db()) await using (var concurrent = Db())
                await Task.WhenAll(new ReminderWithdrawalReconciler(first, nodes, native, clock).ReconcileAsync(default), new ReminderWithdrawalReconciler(concurrent, nodes, native, clock).ReconcileAsync(default));
            Assert.Single(native.Commands); var original = native.Commands[0];
            foreach (var seconds in new[] { 30, 60, 120 })
            {
                clock.Advance(TimeSpan.FromSeconds(seconds)); await using var restarted = Db();
                await new ReminderWithdrawalReconciler(restarted, nodes, native, clock).ReconcileAsync(default);
            }
            Assert.Equal(4, native.Commands.Count); Assert.All(native.Commands, c => Assert.Equal(original, c));
            await using var final = Db(); var saved = await final.Set<ReminderRecipient>().AsNoTracking().SingleAsync();
            Assert.Equal(4, saved.WithdrawalAttempts); Assert.Equal(original.CommandId, saved.WithdrawalCommandId); Assert.Equal(original.ExpiresAt, saved.WithdrawalCommandExpiresAt); Assert.Null(saved.WithdrawnAt);
            var calls = nodes.Calls; clock.Advance(TimeSpan.FromMinutes(10)); await new ReminderWithdrawalReconciler(final, nodes, native, clock).ReconcileAsync(default);
            Assert.Equal(calls, nodes.Calls); Assert.Equal(4, native.Commands.Count);
        }
        finally { await setup.Database.ExecuteSqlRawAsync("DROP SCHEMA IF EXISTS \"" + schema + "\" CASCADE"); }
    }

    private static (AegisNode Node, Reminder Reminder, ReminderRecipient Recipient) Seed(AegisDbContext db, ReminderTests.TestClock clock)
    {
        var now = clock.GetUtcNow(); var node = new AegisNode(Guid.NewGuid(), "Synthetic withdrawal recipient", NodePlatform.Android, "0.7.0-unstable.14", 1, now);
        var reminder = new Reminder("Synthetic acknowledged occurrence", now, "UTC", null, now.AddMinutes(-1));
        reminder.Claim(Guid.NewGuid(), now); reminder.BeginTrigger(now); reminder.Finish(null, true, now); reminder.Acknowledge(now);
        var recipient = ReminderRecipient.LocalPosted(reminder.Id, 1, node.Id, now);
        db.Nodes.Add(node); db.NodeCapabilities.Add(new(node.Id, "reminder.withdraw", 1)); db.Reminders.Add(reminder); db.Set<ReminderRecipient>().Add(recipient);
        return (node, reminder, recipient);
    }
    private sealed class Dispatch(string status) : INodeNotificationDispatcher
    {
        public List<NodeNotificationCommand> Commands { get; } = [];
        public Task<NotificationDispatchResult> DispatchToNodeAsync(Guid nodeId, NodeNotificationCommand command, CancellationToken ct = default)
        { lock (Commands) Commands.Add(command); return Task.FromResult(new NotificationDispatchResult(new(nodeId, "Synthetic"), "backgroundReachable", "fcm", status, command.CommandId)); }
        public Task<NotificationDispatchResult> DispatchAsync(Guid actor, NotificationShowRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private sealed class Inventory(AegisNode node) : INodeRegistry
    {
        private int calls; public int Calls => calls;
        public Task<IReadOnlyList<NodeView>> ListForDeliveryAsync(CancellationToken ct = default) { Interlocked.Increment(ref calls); return Task.FromResult<IReadOnlyList<NodeView>>([NodeView.From(node) with { Capabilities = [new("reminder.withdraw", 1)] }]); }
        public Task<PairingCodeView> CreateCodeAsync(Guid? issuer, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PairNodeReceipt> PairAsync(PairNodeRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<NodeView> FinalizeAsync(FinalizeNodeRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<NodeAuthentication> AuthenticateAsync(string credential, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<NodeView>> ListAsync(Guid actor, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<NodeView> MeAsync(Guid actor, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<NodeView> RenameAsync(Guid actor, Guid target, string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<NodeView> SetEnabledAsync(Guid? actor, Guid target, bool enabled, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<NodeView> RevokeAsync(Guid actor, Guid target, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<NodeView> SetTargetPriorityAsync(Guid actor, Guid target, int priority, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CleanupAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task LegacyDisabledAndThrottledCopiesCannotStarveReachableWithdrawals()
    {
        await using var db = new AegisDbContext(new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        await Exercise(db);
    }

    [ReminderPostgresTests.PostgresFact]
    public async Task WithdrawalBatchFiltersTranslateAndRemainFairInPostgres()
    {
        var schema = "withdrawal_batch_" + Guid.NewGuid().ToString("N");
        await using var db = new AegisDbContext(new DbContextOptionsBuilder<AegisDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("AEGIS_REMINDER_TEST_DATABASE") + ";Search Path=" + schema, o => o.MigrationsHistoryTable("__EFMigrationsHistory", schema)).Options);
        try
        {
            await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA \"" + schema + "\""); await db.Database.MigrateAsync();
            await Exercise(db);
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP SCHEMA IF EXISTS \"" + schema + "\" CASCADE"); }
    }

    private static async Task Exercise(AegisDbContext db)
    {
        var clock = new ReminderTests.TestClock(); var live = new ReminderTests.LiveConnections(); var background = new ReminderTests.BackgroundRoutes(); var native = new ReminderTests.NativeDelivery();
        var reminder = new Reminder("Synthetic acknowledged occurrence", clock.GetUtcNow().AddMinutes(1), "UTC", null, clock.GetUtcNow());
        db.Reminders.Add(reminder); clock.Advance(TimeSpan.FromMinutes(1)); var now = clock.GetUtcNow();
        reminder.Claim(Guid.NewGuid(), now); reminder.BeginTrigger(now); reminder.Finish(null, true, now); reminder.Acknowledge(now);
        var legacy = new List<ReminderRecipient>(); var disabled = new List<(AegisNode Node, ReminderRecipient Recipient)>(); var throttled = new List<ReminderRecipient>();
        for (var i = 0; i < 50; i++)
        {
            legacy.Add(Add(withdraw: false).Recipient);
            disabled.Add(Add(enabled: false));
            var waiting = Add(); waiting.Recipient.BeginWithdrawal(now); waiting.Recipient.QueueWithdrawal(now); background.Nodes.Add(waiting.Node.Id); throttled.Add(waiting.Recipient);
        }
        var reachable = Add(postedAt: now.AddSeconds(1)); live.Nodes[reachable.Node.Id] = [new("reminder.withdraw", 1)]; await db.SaveChangesAsync();
        var registry = new NodeRegistry(db, clock, live, background); var resolver = new NodeTargetResolver(registry, live, background);
        var dispatcher = new NodeNotificationDispatcher(registry, resolver, live, native, native, background, clock);
        var reconciler = new ReminderWithdrawalReconciler(db, registry, dispatcher, clock);

        await reconciler.ReconcileAsync(default);
        Assert.Equal(reachable.Node.Id, Assert.Single(native.Commands).NodeId);
        Assert.NotNull((await db.Set<ReminderRecipient>().AsNoTracking().SingleAsync(r => r.Id == reachable.Recipient.Id)).WithdrawnAt);
        Assert.All(legacy.Concat(throttled), r => Assert.Null(r.WithdrawnAt));
        await reconciler.ReconcileAsync(default); Assert.Single(native.Commands); // No repeated push during its waiting window.

        var returning = await db.Nodes.SingleAsync(n => n.Id == disabled[0].Node.Id); returning.SetEnabled(true, now);
        live.Nodes[returning.Id] = [new("reminder.withdraw", 1)]; await db.SaveChangesAsync();
        await reconciler.ReconcileAsync(default);
        Assert.Equal(returning.Id, native.Commands[1].NodeId); Assert.Equal(2, native.Commands.Count);
        Assert.NotNull((await db.Set<ReminderRecipient>().AsNoTracking().SingleAsync(r => r.Id == disabled[0].Recipient.Id)).WithdrawnAt);
        Assert.Null(await new ReminderStore(db).ClaimAsync(clock.GetUtcNow(), default)); // Global ACK already stopped delivery work.

        (AegisNode Node, ReminderRecipient Recipient) Add(bool withdraw = true, bool enabled = true, DateTimeOffset? postedAt = null)
        {
            var node = new AegisNode(Guid.NewGuid(), "Synthetic recipient", NodePlatform.Windows, "0.7.0-unstable.12", 1, now.AddMinutes(-2));
            node.SetEnabled(enabled, now); db.Nodes.Add(node); db.NodeCapabilities.Add(new(node.Id, withdraw ? "reminder.withdraw" : "notification.show", 1));
            var recipient = ReminderRecipient.LocalPosted(reminder.Id, 1, node.Id, postedAt ?? now.AddMinutes(-2)); db.Set<ReminderRecipient>().Add(recipient);
            return (node, recipient);
        }
    }
}
