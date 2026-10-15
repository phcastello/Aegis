using System.Text.Json;
using Aegis.Application.Nodes;
using Aegis.Application.Observability;
using Aegis.Application.Reminders;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Nodes;
using Aegis.Infrastructure.Persistence;
using Aegis.Infrastructure.Reminders;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace Aegis.Application.Tests;

public sealed class ReminderCompletionTests
{
    private static DateTimeOffset At(string value) => DateTimeOffset.Parse(value);
    private static ReminderRecurrenceRule Daily(string date = "2026-09-27", string time = "08:00", int interval = 1) => new() { Frequency = "daily", StartDate = date, LocalTime = time, Interval = interval };
    [Fact] public void CalendarSkipsMissingMonthDayAndLastDayIsDistinct()
    {
        var rule = new ReminderRecurrenceRule { Frequency = "monthly", StartDate = "2024-01-01", LocalTime = "09:00", MonthDay = 31 };
        Assert.Equal(At("2024-03-31T09:00Z"), ReminderCalendar.Next(rule, "UTC", At("2024-02-01T00:00Z"))!.DueAt);
        Assert.Equal(At("2024-02-29T09:00Z"), ReminderCalendar.Next(rule with { MonthDay = null, LastDay = true }, "UTC", At("2024-02-01T00:00Z"))!.DueAt);
    }
    [Fact] public void DstGapUsesFirstValidMinuteAndOverlapUsesFirstInstant()
    {
        var gap = ReminderCalendar.Next(Daily("2026-03-08", "02:30"), "America/New_York", At("2026-03-08T00:00Z"));
        Assert.Equal(At("2026-03-08T07:00Z"), gap!.DueAt);
        var overlap = ReminderCalendar.Next(Daily("2026-11-01", "01:30"), "America/New_York", At("2026-11-01T00:00Z"));
        Assert.Equal(At("2026-11-01T05:30Z"), overlap!.DueAt);
        Assert.Equal(At("2026-11-02T06:30Z"), ReminderCalendar.Next(Daily("2026-11-01", "01:30"), "America/New_York", overlap.DueAt)!.DueAt);
    }
    [Fact] public void DailyWallClockDoesNotAddTwentyFourUtcHours()
    {
        var rule = Daily("2026-03-07", "08:00");
        var first = ReminderCalendar.Next(rule, "America/New_York", At("2026-03-07T00:00Z"))!;
        var next = ReminderCalendar.Next(rule, "America/New_York", first.DueAt)!;
        Assert.Equal(TimeSpan.FromHours(23), next.DueAt - first.DueAt);
    }
    [Fact] public void WeeklyIntervalsUseMondayAndSelectedIsoDays()
    {
        var rule = new ReminderRecurrenceRule { Frequency = "weekly", Interval = 2, StartDate = "2026-09-23", LocalTime = "18:00", Weekdays = [1, 3, 5] };
        var first = ReminderCalendar.Next(rule, "UTC", At("2026-09-23T00:00Z"))!;
        Assert.Equal(At("2026-09-23T18:00Z"), first.DueAt);
        var friday = ReminderCalendar.Next(rule, "UTC", first.DueAt)!;
        Assert.Equal(At("2026-09-25T18:00Z"), friday.DueAt);
        Assert.Equal(At("2026-10-05T18:00Z"), ReminderCalendar.Next(rule, "UTC", friday.DueAt)!.DueAt);
    }
    [Fact] public void IntervalsAndEndingsAreBoundedAndValidated()
    {
        var rule = Daily(interval: 3) with { Count = 2 };
        Assert.Equal(At("2026-09-30T08:00Z"), ReminderCalendar.Next(rule, "UTC", At("2026-09-27T09:00Z"))!.DueAt);
        Assert.Null(ReminderCalendar.Next(rule, "UTC", At("2026-09-30T08:00Z")));
        Assert.Null(ReminderCalendar.Next(Daily() with { UntilDate = "2026-09-28" }, "UTC", At("2026-09-28T09:00Z")));
        Assert.Throws<ArgumentException>(() => (Daily() with { Count = 2, UntilDate = "2026-09-29" }).Validate());
        Assert.Throws<ArgumentException>(() => (Daily() with { Interval = 0 }).Validate());
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ReminderRecurrenceRule>("{\"frequency\":\"daily\",\"cron\":\"* * *\"}", ReminderRecurrenceRule.JsonOptions));
    }
    [Fact] public async Task MaterializationSurvivesRestartAndOnlyKeepsOneFutureOccurrence()
    {
        using var f = new Fixture(); var series = await f.Series.CreateAsync(null, "teste", "UTC", Daily("2026-09-27", "14:00"), "allEligible", default);
        for (var i = 0; i < 3; i++) await new ReminderSeriesStore(f.Db, f.Clock).MaterializePendingAsync(default);
        Assert.Single(f.Db.Reminders); var first = Assert.Single(f.Db.Reminders);
        f.Clock.Advance(TimeSpan.FromHours(1)); await f.SeriesStore.MaterializePendingAsync(default);
        Assert.Equal(2, f.Db.Reminders.Count()); Assert.Single(f.Db.Reminders.Where(r => r.DueAtUtc > f.Clock.GetUtcNow()));
        Assert.NotEqual(first.Id, f.Db.Reminders.Single(r => r.DueAtUtc > f.Clock.GetUtcNow()).Id);
    }
    [Fact] public async Task PausingResumingEditingAndSkippingPreservePastAndNeverCatchUpStorm()
    {
        using var f = new Fixture(); var series = await f.Series.CreateAsync(null, "teste", "UTC", Daily("2026-09-27", "14:00"), "allEligible", default);
        await f.Series.ChangeAsync(null, series.Id, 1, "pause", null, null, null, default);
        f.Clock.Advance(TimeSpan.FromDays(7)); await f.SeriesStore.MaterializePendingAsync(default); Assert.Single(f.Db.Reminders);
        series = await f.Series.ChangeAsync(null, series.Id, 2, "resume", null, null, null, default);
        var future = Assert.Single(f.Db.Reminders.Where(r => r.Status == ReminderStatus.Scheduled)); Assert.True(future.DueAtUtc > f.Clock.GetUtcNow());
        series = await f.Series.ChangeAsync(null, series.Id, series.Revision, "update", "novo", Daily("2026-09-27", "15:00"), null, default);
        Assert.Equal("novo", f.Db.Reminders.Single(r => r.Status == ReminderStatus.Scheduled).Text);
        series = await f.Series.ChangeAsync(null, series.Id, series.Revision, "skip", null, null, null, default);
        Assert.Equal(At("2026-10-05T15:00Z"), f.Db.Reminders.Single(r => r.Status == ReminderStatus.Scheduled).DueAtUtc);
    }
    [Fact] public async Task FanoutPartialFailureRetriesOnlyFailedRecipientWithSameAmbiguousCommand()
    {
        using var f = new Fixture(); var android = f.AddNode(NodePlatform.Android); await f.Db.SaveChangesAsync();
        var reminder = await f.Create(); f.Native.Results.Enqueue("success"); f.Native.Results.Enqueue("timeout");
        f.Clock.Advance(TimeSpan.FromHours(1)); await f.Processor.ProcessNextAsync(); Assert.Equal(2, f.Native.Commands.Count);
        Assert.Equal(2, f.Db.Set<ReminderRecipient>().Count()); Assert.Single(f.Db.Set<ReminderRecipient>().Where(r => r.Status == "accepted"));
        var timeoutCommand = f.Native.Commands[1]; f.Native.Results.Enqueue("duplicate"); f.Clock.Advance(TimeSpan.FromSeconds(10)); await f.Processor.ProcessNextAsync();
        Assert.Equal(3, f.Native.Commands.Count); Assert.Equal(timeoutCommand, f.Native.Commands[2]); Assert.Equal(2, f.Db.Set<ReminderRecipient>().Count(r => r.Status == "accepted"));
    }
    [Fact] public async Task BestAvailableIsExplicitAndReceivingPreferenceIsRespected()
    {
        using var f = new Fixture(); f.AddNode(NodePlatform.Android); await f.Db.SaveChangesAsync();
        var reminder = await f.Create(); reminder.SetDeliveryPolicy("bestAvailable"); await f.Db.SaveChangesAsync();
        f.Clock.Advance(TimeSpan.FromHours(1)); await f.Processor.ProcessNextAsync(); Assert.Single(f.Native.Commands);
    }
    [Fact] public async Task AcknowledgementAndSnoozeUseRevisionIdempotencyAndNeverModifySeries()
    {
        using var f = new Fixture(); var series = await f.Series.CreateAsync(null, "teste", "UTC", Daily("2026-09-27", "14:00"), "allEligible", default);
        var reminder = Assert.Single(f.Db.Reminders); f.Clock.Advance(TimeSpan.FromHours(1)); await f.Processor.ProcessNextAsync();
        var actionId = Guid.NewGuid(); var snooze = new ReminderActionRequest(actionId, reminder.Revision, 1, "snooze");
        var first = await f.Actions.ApplyAsync(reminder.Id, snooze, default); Assert.True(first.Applied);
        var replay = await f.Actions.ApplyAsync(reminder.Id, snooze, default); Assert.True(replay.Replayed); Assert.True(replay.Applied); Assert.Equal(2, ((JsonElement)replay.Reminder).GetProperty("generation").GetInt32());
        var stale = await f.Actions.ApplyAsync(reminder.Id, new(Guid.NewGuid(), 1, 1, "acknowledge"), default); Assert.True(stale.Conflict);
        var saved = await f.Db.Reminders.AsNoTracking().SingleAsync(r => r.Id == reminder.Id); Assert.Equal(2, saved.Generation); Assert.Null(saved.AcknowledgedAt);
        await f.SeriesStore.MaterializePendingAsync(default); Assert.Contains(f.Db.Reminders, r => r.DueAtUtc == At("2026-09-28T14:00Z"));
        Assert.Equal(1, (await f.Db.Set<ReminderSeries>().SingleAsync()).Revision);
        f.Clock.Advance(TimeSpan.FromMinutes(10)); await f.Processor.ProcessNextAsync();
        saved = await f.Db.Reminders.AsNoTracking().SingleAsync(r => r.Id == reminder.Id);
        await f.Actions.ApplyAsync(reminder.Id, new(Guid.NewGuid(), saved.Revision, 2, "acknowledge"), default);
        Assert.NotNull((await f.Db.Reminders.AsNoTracking().SingleAsync(r => r.Id == reminder.Id)).AcknowledgedAt);
    }
    [Fact] public async Task OpenDoesNotAcknowledgeAndScopedTokenBindsRevisionGenerationAndExpiry()
    {
        using var f = new Fixture(); var reminder = await f.Create(); f.Clock.Advance(TimeSpan.FromHours(1)); await f.Processor.ProcessNextAsync();
        reminder = await f.Db.Reminders.SingleAsync(); reminder.Open(f.Clock.GetUtcNow()); Assert.Null(reminder.AcknowledgedAt);
        var tokens = new ReminderActionTokens(new EphemeralDataProtectionProvider(), f.Clock);
        var token = tokens.Create(reminder, f.Clock.GetUtcNow().AddMinutes(1)); Assert.True(tokens.Validate(token, reminder.Id, 1, 1));
        Assert.False(tokens.Validate(token, Guid.NewGuid(), 1, 1)); Assert.False(tokens.Validate(token, reminder.Id, 2, 1)); Assert.False(tokens.Validate(token, reminder.Id, 1, 2));
        f.Clock.Advance(TimeSpan.FromMinutes(2)); Assert.False(tokens.Validate(token, reminder.Id, 1, 1));
    }
    [Fact] public async Task OldGeographicEventIsNotBroadcastAndObserverNeverGetsSecondCopy()
    {
        using var f = new Fixture(); var observer = f.AddNode(NodePlatform.Android); await f.Db.SaveChangesAsync();
        var now = f.Clock.GetUtcNow(); var reminder = Reminder.CreateGeographic(Guid.NewGuid(), Guid.NewGuid(), observer.Id, "local", now, "UTC", now, observerPostedAt: now);
        f.Db.Reminders.Add(reminder); await f.Db.SaveChangesAsync(); await f.Processor.ProcessNextAsync(); Assert.Single(f.Native.Commands); Assert.DoesNotContain(f.Native.Commands, c => c.NodeId == observer.Id);
        var stale = Reminder.CreateGeographic(Guid.NewGuid(), Guid.NewGuid(), observer.Id, "stale", now.AddHours(-1), "UTC", now, observerPostedAt: now); f.Db.Reminders.Add(stale); await f.Db.SaveChangesAsync();
        await f.Processor.ProcessNextAsync(); Assert.Single(f.Native.Commands);
    }
    [Fact] public async Task EditingSeriesPreservesCompletedDateAndIndividualCancellation()
    {
        using var f = new Fixture(); var series = await f.Series.CreateAsync(null, "teste", "UTC", Daily("2026-09-27", "14:00"), "allEligible", default);
        f.Clock.Advance(TimeSpan.FromHours(1)); await f.Processor.ProcessNextAsync();
        series = await f.Series.ChangeAsync(null, series.Id, 1, "update", null, Daily("2026-09-27", "15:00"), null, default);
        var upcoming = Assert.Single(f.Db.Reminders.Where(r => r.Status == ReminderStatus.Scheduled));
        Assert.Equal(At("2026-09-28T15:00Z"), upcoming.DueAtUtc);
        upcoming.Cancel(f.Clock.GetUtcNow()); await f.Db.SaveChangesAsync();
        await f.Series.ChangeAsync(null, series.Id, series.Revision, "update", "texto novo", null, null, default);
        Assert.Equal(At("2026-09-29T15:00Z"), f.Db.Reminders.Single(r => r.Status == ReminderStatus.Scheduled).DueAtUtc);
        Assert.Equal("teste", f.Db.Reminders.Single(r => r.Status == ReminderStatus.Triggered).Text);
    }
    [Fact] public async Task GlobalAcknowledgementWithdrawsOtherReachablePresentation()
    {
        using var f = new Fixture(); var node = f.Db.Nodes.Single();
        f.Db.NodeCapabilities.Add(new(node.Id, "reminder.withdraw", 1)); f.Live.Nodes[node.Id] = [new("notification.show", 1), new("reminder.withdraw", 1)]; await f.Db.SaveChangesAsync();
        var reminder = await f.Create(); f.Clock.Advance(TimeSpan.FromHours(1)); await f.Processor.ProcessNextAsync();
        await f.Actions.ApplyAsync(reminder.Id, new(Guid.NewGuid(), 1, 1, "acknowledge"), default);
        var registry = new NodeRegistry(f.Db, f.Clock, f.Live, f.Background); var resolver = new NodeTargetResolver(registry, f.Live, f.Background);
        var dispatcher = new NodeNotificationDispatcher(registry, resolver, f.Live, f.Native, f.Native, f.Background, f.Clock);
        await new ReminderWithdrawalReconciler(f.Db, registry, dispatcher, f.Clock).ReconcileAsync(default);
        var withdrawal = f.Native.Commands.Last().Command; Assert.Equal("reminder.withdraw", withdrawal.Capability);
        Assert.Equal(reminder.Id, withdrawal.Input.ReminderId); Assert.Equal(1, withdrawal.Input.Generation); Assert.Null(withdrawal.Input.Body);
        Assert.NotNull((await f.Db.Set<ReminderRecipient>().SingleAsync()).WithdrawnAt);
    }
    [Fact] public async Task ReceivingPreferenceAndFailedLocalPostAreRespected()
    {
        using var f = new Fixture(); var phone = f.AddNode(NodePlatform.Android); var pc = f.Db.Nodes.Local.Single(n => n.Platform == NodePlatform.Windows);
        pc.SetReceiveReminders(false, f.Clock.GetUtcNow()); await f.Db.SaveChangesAsync();
        var now = f.Clock.GetUtcNow(); f.Db.Reminders.Add(Reminder.CreateGeographic(Guid.NewGuid(), Guid.NewGuid(), phone.Id, "local unknown", now, "UTC", now)); await f.Db.SaveChangesAsync();
        await f.Processor.ProcessNextAsync(); Assert.Equal(phone.Id, Assert.Single(f.Native.Commands).NodeId);
    }
    [Fact] public void OldSeriesBaseIsSkippedDirectlyWhileOrdinalAndCountRemainCorrect()
    {
        var result = ReminderCalendar.Next(Daily("1970-01-01", "08:00", 3), "UTC", At("2026-09-27T00:00Z"));
        Assert.NotNull(result); Assert.True(result.Ordinal > 6000); Assert.True(result.DueAt >= At("2026-09-27T00:00Z"));
        Assert.Null(ReminderCalendar.Next(Daily("1970-01-01", "08:00", 3) with { Count = 2 }, "UTC", At("2026-09-27T00:00Z")));
    }
    private sealed class Fixture : IDisposable
    {
        public ReminderTests.TestClock Clock { get; } = new();
        public ReminderTests.LiveConnections Live { get; } = new();
        public ReminderTests.BackgroundRoutes Background { get; } = new();
        public ReminderTests.NativeDelivery Native { get; } = new();
        public AegisDbContext Db { get; }
        public AegisMetrics Metrics { get; } = new();
        public ReminderSeriesStore SeriesStore { get; }
        public ReminderSeriesService Series { get; }
        public ReminderProcessor Processor { get; }
        public ReminderActionService Actions { get; }
        public Fixture()
        {
            Db = new(new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            AddNode(NodePlatform.Windows); Db.SaveChanges(); var store = new ReminderStore(Db);
            SeriesStore = new(Db, Clock); Series = new(SeriesStore, store, Clock); Actions = new(new ReminderActionStore(Db, store, Clock));
            var registry = new NodeRegistry(Db, Clock, Live, Background); var resolver = new NodeTargetResolver(registry, Live, Background);
            Processor = new(Db, store, resolver, new NodeNotificationDispatcher(registry, resolver, Live, Native, Native, Background, Clock), Clock, Metrics, registry);
        }
        public AegisNode AddNode(NodePlatform platform)
        {
            var node = new AegisNode(Guid.NewGuid(), "fixture", platform, "0.7.0-unstable.11", 1, Clock.GetUtcNow()); Db.Nodes.Add(node); Db.NodeCapabilities.Add(new(node.Id, "notification.show", 1)); Live.Nodes[node.Id] = [new("notification.show", 1)]; return node;
        }
        public async Task<Reminder> Create() { var r = new Reminder("teste", Clock.GetUtcNow().AddHours(1), "UTC", null, Clock.GetUtcNow()); Db.Reminders.Add(r); await Db.SaveChangesAsync(); return r; }
        public void Dispose() { Db.Dispose(); Metrics.Dispose(); }
    }
}
