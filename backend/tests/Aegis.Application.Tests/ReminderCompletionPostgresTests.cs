using Aegis.Application.Reminders;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Persistence;
using Aegis.Infrastructure.Reminders;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace Aegis.Application.Tests;

// Every test creates its own disposable schema; no production identity or reminder is used.
public sealed class ReminderCompletionPostgresTests
{
    private static AegisDbContext Db(string schema) => new(new DbContextOptionsBuilder<AegisDbContext>()
        .UseNpgsql(Environment.GetEnvironmentVariable("AEGIS_REMINDER_TEST_DATABASE") + ";Search Path=" + schema,
            o => o.MigrationsHistoryTable("__EFMigrationsHistory", schema)).Options);
    [ReminderPostgresTests.PostgresFact]
    public async Task MaterializationConcurrentRestartHasPersistentUniquenessAndOneUpcomingOccurrence()
    {
        var schema = "recurrence_" + Guid.NewGuid().ToString("N"); await using var setup = Db(schema); var clock = new ReminderTests.TestClock();
        try
        {
            await setup.Database.ExecuteSqlRawAsync("CREATE SCHEMA \"" + schema + "\""); await setup.Database.MigrateAsync();
            var series = new ReminderSeries("isolated", "UTC", new() { StartDate = "2026-09-27", LocalTime = "14:00" }, null, clock.GetUtcNow());
            await new ReminderSeriesStore(setup, clock).AddAsync(series, default); clock.Advance(TimeSpan.FromHours(1));
            await using var first = Db(schema); await using var second = Db(schema);
            await Task.WhenAll(new ReminderSeriesStore(first, clock).MaterializePendingAsync(default), new ReminderSeriesStore(second, clock).MaterializePendingAsync(default));
            await using var restarted = Db(schema); await new ReminderSeriesStore(restarted, clock).MaterializePendingAsync(default);
            Assert.Equal(2, await restarted.Reminders.CountAsync()); Assert.Single(await restarted.Reminders.Where(r => r.DueAtUtc > clock.GetUtcNow()).ToArrayAsync());
            var saved = await restarted.Set<ReminderSeries>().SingleAsync();
            var duplicate = Reminder.ForSeries(saved, new(DateTimeOffset.Parse("2026-09-28T14:00Z"), "2026-09-28T14:00", 2), clock.GetUtcNow());
            restarted.Reminders.Add(duplicate); await Assert.ThrowsAsync<DbUpdateException>(() => restarted.SaveChangesAsync());
        }
        finally { await setup.Database.ExecuteSqlRawAsync("DROP SCHEMA IF EXISTS \"" + schema + "\" CASCADE"); }
    }
    [ReminderPostgresTests.PostgresFact]
    public async Task ConcurrentActionsOnSameRevisionHaveOneWinnerAndReplayRemainsIdempotent()
    {
        var schema = "actions_" + Guid.NewGuid().ToString("N"); await using var setup = Db(schema); var clock = new ReminderTests.TestClock();
        try
        {
            await setup.Database.ExecuteSqlRawAsync("CREATE SCHEMA \"" + schema + "\""); await setup.Database.MigrateAsync();
            var reminder = new Reminder("isolated", clock.GetUtcNow().AddMinutes(1), "UTC", null, clock.GetUtcNow()); setup.Reminders.Add(reminder); await setup.SaveChangesAsync();
            clock.Advance(TimeSpan.FromMinutes(1)); reminder.Claim(Guid.NewGuid(), clock.GetUtcNow()); reminder.BeginTrigger(clock.GetUtcNow()); reminder.Finish(null, true, clock.GetUtcNow()); await setup.SaveChangesAsync();
            await using var first = Db(schema); await using var second = Db(schema);
            var ack = new ReminderActionRequest(Guid.NewGuid(), 1, 1, "acknowledge"); var snooze = new ReminderActionRequest(Guid.NewGuid(), 1, 1, "snooze");
            var outcomes = await Task.WhenAll(new ReminderActionStore(first, new(first), clock).ApplyAsync(reminder.Id, ack, default), new ReminderActionStore(second, new(second), clock).ApplyAsync(reminder.Id, snooze, default));
            Assert.Single(outcomes, r => r.Applied); Assert.Single(outcomes, r => r.Conflict);
            await using var restarted = Db(schema); var replay = await new ReminderActionStore(restarted, new(restarted), clock).ApplyAsync(reminder.Id, ack, default);
            Assert.True(replay.Replayed); Assert.Equal(outcomes[0].Applied, replay.Applied); Assert.Equal(2, await restarted.Set<ReminderActionReceipt>().CountAsync());
        }
        finally { await setup.Database.ExecuteSqlRawAsync("DROP SCHEMA IF EXISTS \"" + schema + "\" CASCADE"); }
    }
}
