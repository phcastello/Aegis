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
            var waiting = Add(); waiting.Recipient.QueueWithdrawal(now); background.Nodes.Add(waiting.Node.Id); throttled.Add(waiting.Recipient);
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
