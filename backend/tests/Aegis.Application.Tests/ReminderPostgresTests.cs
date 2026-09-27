using Aegis.Application.Observability;
using Aegis.Application.Reminders;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Persistence;
using Aegis.Infrastructure.Reminders;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Aegis.Application.Tests;

// Runs against a disposable PostgreSQL database, never the configured Aegis database.
public sealed class ReminderPostgresTests
{
    public sealed class PostgresFactAttribute : FactAttribute
    {
        public PostgresFactAttribute() { if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AEGIS_REMINDER_TEST_DATABASE"))) Skip = "Set AEGIS_REMINDER_TEST_DATABASE to a disposable PostgreSQL database."; }
    }
    private static readonly string Schema = "reminders_" + Guid.NewGuid().ToString("N");
    private static AegisDbContext CreateDb() => new(new DbContextOptionsBuilder<AegisDbContext>()
        .UseNpgsql(Environment.GetEnvironmentVariable("AEGIS_REMINDER_TEST_DATABASE") + ";Search Path=" + Schema, options => options.MigrationsHistoryTable("__EFMigrationsHistory", Schema)).Options);

    [PostgresFact]
    public async Task MigrationAtomicClaimRecoveryIndependentLifetimeAndDispatchAreRealPostgres()
    {
        var clock = new ReminderTests.TestClock();
        await using var db = CreateDb();
        await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA \"" + Schema + "\"");
        await db.Database.MigrateAsync();
        var conversation = new Conversation();
        db.Conversations.Add(conversation);
        var r = new Reminder("ração", clock.GetUtcNow().AddHours(1), "America/Sao_Paulo", conversation.Id, clock.GetUtcNow());
        db.Reminders.Add(r);
        var subscription = new PushSubscription("https://fcm.googleapis.com/eval", "key", "auth", Guid.NewGuid(), null, clock.GetUtcNow());
        db.PushSubscriptions.Add(subscription); await db.SaveChangesAsync();
        // Physical delete exercises SET NULL, beyond the app's soft-delete behavior.
        db.Conversations.Remove(conversation); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        Assert.Null((await db.Reminders.FindAsync(r.Id))!.SourceConversationId);
        clock.Advance(TimeSpan.FromHours(2));
        // Two different connections race: exactly one receives the reminder.
        await using var first = CreateDb(); await using var second = CreateDb();
        var claims = await Task.WhenAll(new ReminderStore(first).ClaimAsync(clock.GetUtcNow(), default), new ReminderStore(second).ClaimAsync(clock.GetUtcNow(), default));
        Assert.Single(claims, c => c is not null);
        // Restart with a fresh context while the lease is active cannot duplicate it.
        await using var restarted = CreateDb();
        Assert.Null(await new ReminderStore(restarted).ClaimAsync(clock.GetUtcNow(), default));
        clock.Advance(TimeSpan.FromMinutes(3));
        var push = new ReminderTests.FakePush(); using var metrics = new AegisMetrics();
        var processor = new ReminderProcessor(restarted, new ReminderStore(restarted), push,
            new ReminderInteractionTokens(new EphemeralDataProtectionProvider(), clock), clock, metrics);
        Assert.True(await processor.ProcessNextAsync()); Assert.Single(push.Payloads);
        // Yet another process does not resend already completed work.
        await using var final = CreateDb();
        Assert.Null(await new ReminderStore(final).ClaimAsync(clock.GetUtcNow(), default));
        Assert.NotNull((await final.ReminderDeliveryAttempts.SingleAsync(a => a.ReminderId == r.Id)).AcceptedAt);
        // A bounded send holds a PostgreSQL row lock even past nominal lease expiry.
        var lockedReminder = new Reminder("locked", clock.GetUtcNow().AddMinutes(1), "America/Sao_Paulo", null, clock.GetUtcNow());
        final.Reminders.Add(lockedReminder); await final.SaveChangesAsync(); clock.Advance(TimeSpan.FromMinutes(1));
        await using var processing = CreateDb();
        var blocked = new BlockingPush();
        var lockedProcessor = new ReminderProcessor(processing, new ReminderStore(processing), blocked,
            new ReminderInteractionTokens(new EphemeralDataProtectionProvider(), clock), clock, metrics);
        var dispatch = lockedProcessor.ProcessNextAsync();
        await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromMinutes(3));
        try { Assert.Null(await new ReminderStore(final).ClaimAsync(clock.GetUtcNow(), default)); }
        finally { blocked.Release.TrySetResult(); }
        Assert.True(await dispatch);
    }
    private sealed class BlockingPush : IWebPushClient
    {
        public bool IsConfigured => true;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<PushResult> SendAsync(PushSubscription subscription, string payload, CancellationToken ct)
        {
            Started.TrySetResult(); await Release.Task.WaitAsync(ct); return new PushResult(201);
        }

    }
}
