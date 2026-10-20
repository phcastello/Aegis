using Aegis.Application.Reminders;
using Aegis.Application.Nodes;
using Aegis.Application.Observability;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Nodes;
using Aegis.Infrastructure.Persistence;
using Aegis.Infrastructure.Reminders;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Xunit;
namespace Aegis.Application.Tests;

// Every test creates its own disposable schema; no production identity or reminder is used.
public sealed class ReminderCompletionPostgresTests
{
    public sealed class PostgresTheoryAttribute : TheoryAttribute
    {
        public PostgresTheoryAttribute() { if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AEGIS_REMINDER_TEST_DATABASE"))) Skip = "Set AEGIS_REMINDER_TEST_DATABASE to a disposable PostgreSQL database."; }
    }
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
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EditingSeriesAfterAnIndividualOverridePreservesTheOverrideAcrossRestart(bool changeSchedule)
    {
        var schema = "series_override_edit_" + Guid.NewGuid().ToString("N"); await using var setup = Db(schema); var clock = new ReminderTests.TestClock(); clock.Advance(TimeSpan.FromHours(-5));
        try
        {
            await setup.Database.ExecuteSqlRawAsync("CREATE SCHEMA \"" + schema + "\""); await setup.Database.MigrateAsync();
            var series = new ReminderSeries("original", "UTC", new() { StartDate = "2026-09-27", LocalTime = "09:00" }, null, clock.GetUtcNow());
            await new ReminderSeriesStore(setup, clock).AddAsync(series, default);
            var exception = await setup.Reminders.SingleAsync(); var due = DateTimeOffset.Parse("2026-09-27T11:00Z");
            exception.Update(null, due, clock.GetUtcNow()); await setup.SaveChangesAsync(); var exceptionRevision = exception.Revision;
            await using (var edit = Db(schema)) await new ReminderSeriesStore(edit, clock).ChangeAsync(series.Id, series.Revision, "update", "novo texto", changeSchedule ? new() { StartDate = "2026-09-27", LocalTime = "10:00" } : null, null, default);
            await using var first = Db(schema); await using var second = Db(schema);
            await Task.WhenAll(new ReminderSeriesStore(first, clock).MaterializePendingAsync(default), new ReminderSeriesStore(second, clock).MaterializePendingAsync(default));
            await using var restarted = Db(schema);
            var preserved = await restarted.Reminders.SingleAsync(r => r.Id == exception.Id);
            Assert.Equal(due, preserved.DueAtUtc); Assert.Equal("original", preserved.Text); Assert.True(preserved.IsException); Assert.Equal(exceptionRevision, preserved.Revision);
            var next = await restarted.Reminders.SingleAsync(r => !r.IsException && r.Status == ReminderStatus.Scheduled);
            Assert.Equal(DateTimeOffset.Parse(changeSchedule ? "2026-09-28T10:00Z" : "2026-09-28T09:00Z"), next.DueAtUtc); Assert.Equal("novo texto", next.Text);
            Assert.Equal(2, await restarted.Reminders.CountAsync());
        }
        finally { await setup.Database.ExecuteSqlRawAsync("DROP SCHEMA IF EXISTS \"" + schema + "\" CASCADE"); }
    }
    [ReminderPostgresTests.PostgresFact]
    public async Task PausedExceptionsSurviveRestartAndOnlyFutureOnesResume()
    {
        var schema = "series_override_pause_" + Guid.NewGuid().ToString("N"); await using var setup = Db(schema); var clock = new ReminderTests.TestClock(); var now = clock.GetUtcNow();
        try
        {
            await setup.Database.ExecuteSqlRawAsync("CREATE SCHEMA \"" + schema + "\""); await setup.Database.MigrateAsync();
            var series = new ReminderSeries("original", "UTC", new() { StartDate = "2026-09-27", LocalTime = "14:00" }, null, now);
            await new ReminderSeriesStore(setup, clock).AddAsync(series, default);
            var elapsed = await setup.Reminders.SingleAsync(); elapsed.Update("expired override", now.AddHours(2), now);
            var future = Reminder.ForSeries(series, new(now.AddDays(3).AddHours(1), "2026-09-30T14:00", 4), now); future.Update("future override", now.AddDays(3).AddHours(2), now);
            setup.Reminders.Add(future); await setup.SaveChangesAsync();
            var paused = await new ReminderSeriesStore(setup, clock).ChangeAsync(series.Id, series.Revision, "pause", null, null, null, default);
            clock.Advance(TimeSpan.FromDays(2));
            await using (var resume = Db(schema))
            {
                var rows = await resume.Reminders.ToArrayAsync(); Assert.All(rows, r => Assert.Equal(ReminderStatus.Paused, r.Status)); Assert.All(rows, r => Assert.False(r.CanClaim(clock.GetUtcNow())));
                await new ReminderSeriesStore(resume, clock).ChangeAsync(series.Id, paused.Revision, "resume", null, null, null, default);
            }
            await using var restarted = Db(schema); await new ReminderSeriesStore(restarted, clock).MaterializePendingAsync(default);
            var expired = await restarted.Reminders.SingleAsync(r => r.Id == elapsed.Id); Assert.Equal(ReminderStatus.Cancelled, expired.Status); Assert.True(expired.IsException);
            var preserved = await restarted.Reminders.SingleAsync(r => r.Id == future.Id); Assert.Equal(ReminderStatus.Scheduled, preserved.Status); Assert.True(preserved.IsException);
            Assert.Equal("future override", preserved.Text); Assert.Equal(now.AddDays(3).AddHours(2), preserved.DueAtUtc);
            Assert.Single(await restarted.Reminders.Where(r => !r.IsException && r.Status == ReminderStatus.Scheduled).ToArrayAsync());
            Assert.DoesNotContain(await restarted.Reminders.ToArrayAsync(), r => r.Status == ReminderStatus.Scheduled && r.DueAtUtc <= clock.GetUtcNow());
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
    public async Task GeographicObserverUnknownLocalOutcomeReplaysItsEventAcrossRestartAndSnoozeGetsANewPresentation()
    {
        var schema = "geographic_replay_" + Guid.NewGuid().ToString("N"); await using var setup = Db(schema); var clock = new ReminderTests.TestClock();
        try
        {
            await setup.Database.ExecuteSqlRawAsync("CREATE SCHEMA \"" + schema + "\""); await setup.Database.MigrateAsync();
            var observer = Guid.NewGuid(); var desktop = Guid.NewGuid(); var eventId = Guid.NewGuid(); var occurredAt = clock.GetUtcNow(); var expiry = occurredAt.AddMinutes(5);
            var live = new ReminderTests.LiveConnections(); var background = new ReminderTests.BackgroundRoutes();
            foreach (var node in new[] { observer, desktop })
            {
                setup.Nodes.Add(new(node, "Synthetic replay recipient", node == observer ? NodePlatform.Android : NodePlatform.Windows, "0.7.0-unstable.12", 1, occurredAt));
                setup.NodeCapabilities.Add(new(node, "reminder.notify", 1)); live.Nodes[node] = [new("reminder.notify", 1)];
            }
            clock.Advance(TimeSpan.FromMinutes(2));
            var reminder = Reminder.CreateGeographic(Guid.NewGuid(), eventId, observer, "Synthetic unknown local result", occurredAt, "UTC", clock.GetUtcNow());
            setup.Reminders.Add(reminder); await setup.SaveChangesAsync();
            var native = new GeographicReplayNative(observer, eventId); using var metrics = new AegisMetrics();
            var tokens = new ReminderActionTokens(new EphemeralDataProtectionProvider(), clock);
            ReminderProcessor Processor(AegisDbContext db)
            {
                var registry = new NodeRegistry(db, clock, live, background); var resolver = new NodeTargetResolver(registry, live, background);
                return new(db, new(db), resolver, new NodeNotificationDispatcher(registry, resolver, live, native, native, background, clock), clock, metrics, registry, tokens);
            }
            await using (var first = Db(schema)) Assert.True(await Processor(first).ProcessNextAsync());
            var original = Assert.Single(native.Commands, c => c.NodeId == observer).Command;
            Assert.Equal(eventId, original.CommandId); Assert.Equal(expiry, original.ExpiresAt);
            Assert.NotEqual(eventId, Assert.Single(native.Commands, c => c.NodeId == desktop).Command.CommandId);
            Assert.Equal(expiry, Assert.Single(native.Commands, c => c.NodeId == desktop).Command.ExpiresAt); // Only three minutes remain in the geographic window.
            clock.Advance(TimeSpan.FromSeconds(10));
            await using (var restarted = Db(schema))
            {
                Assert.True(await Processor(restarted).ProcessNextAsync());
                var attempts = await restarted.ReminderDeliveryAttempts.Where(a => a.NodeId == observer).OrderBy(a => a.Attempt).ToArrayAsync();
                Assert.Equal(2, attempts.Length); Assert.All(attempts, a => { Assert.Equal(eventId, a.CommandId); Assert.Equal(expiry, a.CommandExpiresAt); });
                Assert.Equal("timeout", attempts[0].Result); Assert.Equal("duplicate", attempts[1].Result);
                var stored = await restarted.Reminders.AsNoTracking().SingleAsync(); Assert.Null(stored.ObserverPostedAt);
                Assert.Null((await restarted.Set<ReminderRecipient>().AsNoTracking().SingleAsync(r => r.NodeId == observer)).PostedAt);
                Assert.Equal(0, native.ObserverNewPosts); // The simulated durable renderer already posted EventId locally.
                var snoozed = await new ReminderActionService(new ReminderActionStore(restarted, new(restarted), clock)).ApplyAsync(reminder.Id, new(Guid.NewGuid(), 1, 1, "snooze"), default);
                Assert.True(snoozed.Applied);
            }
            clock.Advance(TimeSpan.FromMinutes(10));
            await using (var snoozed = Db(schema)) Assert.True(await Processor(snoozed).ProcessNextAsync());
            var presentation = native.Commands.Last(c => c.NodeId == observer).Command;
            Assert.NotEqual(eventId, presentation.CommandId); Assert.Equal(2, presentation.Input.Generation);
            Assert.Equal(clock.GetUtcNow().AddMinutes(5), presentation.ExpiresAt); Assert.Equal(1, native.ObserverNewPosts);
            Assert.Equal(2, native.Commands.Where(c => c.NodeId == desktop).Select(c => c.Command.CommandId).Distinct().Count());
        }
        finally { await setup.Database.ExecuteSqlRawAsync("DROP SCHEMA IF EXISTS \"" + schema + "\" CASCADE"); }
    }

    private sealed class GeographicReplayNative(Guid observer, Guid localEventId) : INodeLiveNotifications, INodePushNotifications
    {
        private readonly HashSet<(Guid NodeId, Guid CommandId)> posted = [(observer, localEventId)];
        private bool loseFirstObserverResponse = true;
        public List<(Guid NodeId, NodeNotificationCommand Command)> Commands { get; } = [];
        public int ObserverNewPosts { get; private set; }
        private string Send(Guid nodeId, NodeNotificationCommand command)
        {
            Commands.Add((nodeId, command));
            if (nodeId == observer && loseFirstObserverResponse) { loseFirstObserverResponse = false; return "timeout"; }
            if (!posted.Add((nodeId, command.CommandId))) return "duplicate";
            if (nodeId == observer) ObserverNewPosts++;
            return "success";
        }
        Task<NodeLiveNotificationResult> INodeLiveNotifications.SendAsync(Guid nodeId, NodeNotificationCommand command, CancellationToken ct) => Task.FromResult(new NodeLiveNotificationResult(Send(nodeId, command)));
        Task<string> INodePushNotifications.SendAsync(Guid nodeId, NodeNotificationCommand command, CancellationToken ct) => Task.FromResult(Send(nodeId, command));
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
