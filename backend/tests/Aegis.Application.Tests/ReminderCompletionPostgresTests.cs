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
    public async Task FutureOccurrencesCannotStarveTheHundredAndFirstSeries()
    {
        var schema = "recurrence_batch_" + Guid.NewGuid().ToString("N"); await using var setup = Db(schema); var clock = new ReminderTests.TestClock();
        try
        {
            await setup.Database.ExecuteSqlRawAsync("CREATE SCHEMA \"" + schema + "\""); await setup.Database.MigrateAsync();
            var now = clock.GetUtcNow();
            for (var i = 0; i < 100; i++)
            {
                var older = new ReminderSeries("Synthetic future series", "UTC", new() { StartDate = "2026-09-28", LocalTime = "14:00" }, null, now.AddDays(-1));
                var next = older.Next(now)!; older.Materialized(next, now.AddDays(-1));
                setup.Set<ReminderSeries>().Add(older); setup.Reminders.Add(Reminder.ForSeries(older, next, now.AddDays(-1)));
            }
            var pending = new ReminderSeries("Synthetic pending series", "UTC", new() { StartDate = "2026-09-27", LocalTime = "14:00" }, null, now);
            await new ReminderSeriesStore(setup, clock).AddAsync(pending, default);
            clock.Advance(TimeSpan.FromHours(1));
            var original = await setup.Reminders.SingleAsync(r => r.SeriesId == pending.Id);
            original.Claim(Guid.NewGuid(), clock.GetUtcNow()); original.BeginTrigger(clock.GetUtcNow()); original.Finish(null, true, clock.GetUtcNow());
            original.Snooze(clock.GetUtcNow(), 10); await setup.SaveChangesAsync();

            await using var restarted = Db(schema); await new ReminderSeriesStore(restarted, clock).MaterializePendingAsync(default);
            var upcoming = await restarted.Reminders.Where(r => r.SeriesId == pending.Id && r.Generation == 1 && r.Status == ReminderStatus.Scheduled).SingleAsync();
            Assert.Equal(DateTimeOffset.Parse("2026-09-28T14:00Z"), upcoming.DueAtUtc);
            Assert.Equal(102, await restarted.Reminders.CountAsync());
            Assert.Single(await restarted.Reminders.Where(r => r.SeriesId == pending.Id && r.Generation == 2).ToArrayAsync());

            upcoming.Cancel(clock.GetUtcNow()); await restarted.SaveChangesAsync();
            await new ReminderSeriesStore(restarted, clock).MaterializePendingAsync(default);
            Assert.Equal(DateTimeOffset.Parse("2026-09-29T14:00Z"), (await restarted.Reminders.SingleAsync(r => r.SeriesId == pending.Id && r.Generation == 1 && r.Status == ReminderStatus.Scheduled)).DueAtUtc);
        }
        finally { await setup.Database.ExecuteSqlRawAsync("DROP SCHEMA IF EXISTS \"" + schema + "\" CASCADE"); }
    }
    [ReminderPostgresTests.PostgresFact]
    public async Task MovingOnlyOneOccurrencePreservesItsIdentityAndTheFollowingDailySchedule()
    {
        var schema = "recurrence_exception_" + Guid.NewGuid().ToString("N"); await using var setup = Db(schema); var clock = new ReminderTests.TestClock();
        try
        {
            await setup.Database.ExecuteSqlRawAsync("CREATE SCHEMA \"" + schema + "\""); await setup.Database.MigrateAsync();
            var series = new ReminderSeries("Synthetic original series", "UTC", new() { StartDate = "2026-09-27", LocalTime = "14:00" }, null, clock.GetUtcNow());
            await new ReminderSeriesStore(setup, clock).AddAsync(series, default);
            var moved = await setup.Reminders.SingleAsync(); var movedId = moved.Id; var movedDueAt = moved.DueAtUtc.AddDays(7);
            moved.Update("Synthetic individual exception", movedDueAt, clock.GetUtcNow()); await setup.SaveChangesAsync();

            await using var first = Db(schema); await using var second = Db(schema);
            await Task.WhenAll(new ReminderSeriesStore(first, clock).MaterializePendingAsync(default), new ReminderSeriesStore(second, clock).MaterializePendingAsync(default));
            await using var restarted = Db(schema);
            var preserved = await restarted.Reminders.SingleAsync(r => r.Id == movedId);
            Assert.True(preserved.IsException); Assert.Equal(movedDueAt, preserved.DueAtUtc); Assert.Equal("Synthetic individual exception", preserved.Text);
            var next = await restarted.Reminders.SingleAsync(r => r.SeriesId == series.Id && !r.IsException && r.Status == ReminderStatus.Scheduled);
            Assert.Equal(DateTimeOffset.Parse("2026-09-28T14:00Z"), next.DueAtUtc); Assert.Equal(series.Text, next.Text);
            Assert.Equal(2, await restarted.Reminders.CountAsync());

            clock.Advance(TimeSpan.FromHours(25)); next.Claim(Guid.NewGuid(), clock.GetUtcNow()); next.BeginTrigger(clock.GetUtcNow()); next.Finish(null, true, clock.GetUtcNow()); await restarted.SaveChangesAsync();
            await new ReminderSeriesStore(restarted, clock).MaterializePendingAsync(default);
            Assert.Equal(DateTimeOffset.Parse("2026-09-29T14:00Z"), (await restarted.Reminders.SingleAsync(r => r.SeriesId == series.Id && !r.IsException && r.Status == ReminderStatus.Scheduled)).DueAtUtc);
            Assert.Equal(3, await restarted.Reminders.CountAsync());
            Assert.Equal(movedDueAt, (await restarted.Reminders.AsNoTracking().SingleAsync(r => r.Id == movedId)).DueAtUtc);
        }
        finally { await setup.Database.ExecuteSqlRawAsync("DROP SCHEMA IF EXISTS \"" + schema + "\" CASCADE"); }
    }
    [ReminderPostgresTests.PostgresFact]
    public async Task SeriesPagesObserveOnlyTheirIdsAndTheHundredAndFirstSeriesCanBeChangedThroughTools()
    {
        var schema = "recurrence_pages_" + Guid.NewGuid().ToString("N"); await using var setup = Db(schema); var clock = new ReminderTests.TestClock();
        try
        {
            await setup.Database.ExecuteSqlRawAsync("CREATE SCHEMA \"" + schema + "\""); await setup.Database.MigrateAsync();
            var conversation = new Conversation(); setup.Conversations.Add(conversation);
            for (var i = 0; i < 101; i++) setup.Set<ReminderSeries>().Add(new("Synthetic paged series " + i, "UTC", new() { StartDate = "2026-09-28", LocalTime = "14:00" }, null, clock.GetUtcNow()));
            await setup.SaveChangesAsync();
            var store = new ReminderSeriesStore(setup, clock); var reminders = new ReminderStore(setup); var service = new ReminderSeriesService(store, reminders, clock);
            var first = await store.PageAsync(0, default); Assert.Equal(100, first.Series.Count); Assert.True(first.HasMore); Assert.Equal(100, first.NextOffset);
            var lastPage = await store.PageAsync(100, default); var last = Assert.Single(lastPage.Series); Assert.False(lastPage.HasMore); Assert.Null(lastPage.NextOffset);
            Assert.DoesNotContain(first.Series, s => s.Id == last.Id);
            var instant = last.Next(clock.GetUtcNow())!; setup.Reminders.Add(Reminder.ForSeries(last, instant, clock.GetUtcNow())); await setup.SaveChangesAsync();
            var context = new Aegis.Application.Tools.ToolExecutionContext(conversation.Id, Guid.NewGuid(), "Synthetic paged request");
            var list = new ReminderSeriesListTool(service); var change = new ReminderSeriesChangeTool(service);
            var pageOne = await list.ExecuteAsync(System.Text.Json.JsonSerializer.SerializeToElement(new { }), context); Assert.True(pageOne.Success);
            Assert.False(await store.WasObservedAsync(conversation.Id, last.Id, clock.GetUtcNow(), default));
            var arguments = System.Text.Json.JsonSerializer.SerializeToElement(new { seriesId = last.Id, expectedRevision = 1, operation = "update", text = "Synthetic changed observed series" });
            Assert.False((await change.ExecuteAsync(arguments, context)).Success);
            var pageTwo = await list.ExecuteAsync(System.Text.Json.JsonSerializer.SerializeToElement(new { offset = 100 }), context); Assert.True(pageTwo.Success);
            var payload = System.Text.Json.JsonDocument.Parse(pageTwo.Content).RootElement;
            Assert.Equal(last.Id, Assert.Single(payload.GetProperty("series").EnumerateArray()).GetProperty("seriesId").GetGuid());
            Assert.Equal(instant.DueAt, payload.GetProperty("series")[0].GetProperty("nextDueAt").GetDateTimeOffset());
            Assert.False(payload.GetProperty("hasMore").GetBoolean()); Assert.Equal(System.Text.Json.JsonValueKind.Null, payload.GetProperty("nextOffset").ValueKind);
            Assert.True((await change.ExecuteAsync(arguments, context)).Success);
            Assert.Equal("Synthetic changed observed series", (await setup.Set<ReminderSeries>().AsNoTracking().SingleAsync(s => s.Id == last.Id)).Text);
            Assert.False((await list.ExecuteAsync(System.Text.Json.JsonSerializer.SerializeToElement(new { offset = -1 }), context)).Success);
            Assert.False((await list.ExecuteAsync(System.Text.Json.JsonSerializer.SerializeToElement(new { offset = 1_000_001 }), context)).Success);
            Assert.Equal(first.Series.Select(s => s.Id), (await store.PageAsync(0, default)).Series.Select(s => s.Id));

            var actions = new ReminderActionService(new ReminderActionStore(setup, reminders, clock));
            var controller = new Aegis.Api.Controllers.RemindersController(setup, reminders, actions, clock, store);
            var result = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.List(default, offset: 0, seriesOffset: 100));
            var snapshot = System.Text.Json.JsonSerializer.SerializeToElement(result.Value, ReminderRecurrenceRule.JsonOptions);
            Assert.Equal(last.Id, Assert.Single(snapshot.GetProperty("series").EnumerateArray()).GetProperty("seriesId").GetGuid());
            Assert.NotEqual(System.Text.Json.JsonValueKind.Null, snapshot.GetProperty("series")[0].GetProperty("nextDueAt").ValueKind);
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
