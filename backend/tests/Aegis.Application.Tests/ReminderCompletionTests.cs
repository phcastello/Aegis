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
        var recipientId = f.Db.Nodes.Single().Id;
        var token = tokens.Create(reminder, f.Clock.GetUtcNow().AddMinutes(1), recipientId); Assert.True(tokens.Validate(token, reminder.Id, 1, 1));
        Assert.True(tokens.TryValidateRecipient(token, reminder.Id, 1, 1, out var boundRecipient)); Assert.Equal(recipientId, boundRecipient);
        Assert.Throws<ArgumentException>(() => tokens.Create(reminder, f.Clock.GetUtcNow().AddMinutes(1), Guid.Empty));
        Assert.False(tokens.Validate(token, Guid.NewGuid(), 1, 1)); Assert.False(tokens.Validate(token, reminder.Id, 2, 1)); Assert.False(tokens.Validate(token, reminder.Id, 1, 2));
        f.Clock.Advance(TimeSpan.FromMinutes(2)); Assert.False(tokens.Validate(token, reminder.Id, 1, 1));
    }
    [Fact] public async Task OldGeographicEventIsNotBroadcastAndObserverNeverGetsSecondCopy()
    {
        using var f = new Fixture(); var observer = f.AddNode(NodePlatform.Android); await f.Db.SaveChangesAsync();
        var now = f.Clock.GetUtcNow(); var reminder = Reminder.CreateGeographic(Guid.NewGuid(), Guid.NewGuid(), observer.Id, "local", now, "UTC", now, observerPostedAt: now);
        f.Db.Reminders.Add(reminder); await f.Db.SaveChangesAsync(); await f.Processor.ProcessNextAsync(); Assert.Single(f.Native.Commands); Assert.DoesNotContain(f.Native.Commands, c => c.NodeId == observer.Id);
        var stale = Reminder.CreateGeographic(Guid.NewGuid(), Guid.NewGuid(), observer.Id, "stale", now.AddMinutes(-6), "UTC", now, observerPostedAt: now.AddMinutes(-6)); f.Db.Reminders.Add(stale); await f.Db.SaveChangesAsync();
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeriesEditsPreserveTheExplicitOccurrenceSnapshotAndItsOriginalDate(bool changeSchedule)
    {
        using var f = new Fixture(); f.Clock.Advance(TimeSpan.FromHours(-5));
        var series = await f.Series.CreateAsync(null, "texto original", "UTC", Daily(time: "09:00"), "allEligible", default);
        var exception = Assert.Single(f.Db.Reminders); var id = exception.Id;
        exception.Update(null, At("2026-09-27T11:00Z"), f.Clock.GetUtcNow()); await f.Db.SaveChangesAsync();
        var revision = exception.Revision;
        await f.Series.ChangeAsync(null, series.Id, series.Revision, "update", "texto da série alterado", changeSchedule ? Daily(time: "10:00") : null, null, default);
        var preserved = await f.Db.Reminders.AsNoTracking().SingleAsync(r => r.Id == id);
        Assert.True(preserved.IsException); Assert.Equal(ReminderStatus.Scheduled, preserved.Status);
        Assert.Equal(At("2026-09-27T11:00Z"), preserved.DueAtUtc); Assert.Equal("texto original", preserved.Text); Assert.Equal(revision, preserved.Revision);
        var next = Assert.Single(await f.Db.Reminders.Where(r => !r.IsException && r.Status == ReminderStatus.Scheduled).ToArrayAsync());
        Assert.Equal(At(changeSchedule ? "2026-09-28T10:00Z" : "2026-09-28T09:00Z"), next.DueAtUtc);
        Assert.Equal("texto da série alterado", next.Text);
        Assert.DoesNotContain(f.Db.Reminders, r => r.Id != id && r.Status == ReminderStatus.Scheduled && r.DueAtUtc.Date == At("2026-09-27T00:00Z").Date);
    }
    [Fact]
    public async Task PauseResumeKeepsFutureExceptionsAndExpiresMissedOnesWithoutCatchUp()
    {
        using var f = new Fixture(); f.Clock.Advance(TimeSpan.FromHours(-5)); var now = f.Clock.GetUtcNow();
        var series = await f.Series.CreateAsync(null, "padrão original", "UTC", Daily(time: "09:00"), "allEligible", default);
        var elapsed = Assert.Single(f.Db.Reminders); elapsed.Update("exceção vencida na pausa", At("2026-09-27T11:00Z"), now);
        var future = Reminder.ForSeries(series, new(At("2026-09-29T09:00Z"), "2026-09-29T09:00", 3), now);
        future.Update("exceção futura", At("2026-09-29T11:00Z"), now); f.Db.Reminders.Add(future); await f.Db.SaveChangesAsync();
        series = await f.Series.ChangeAsync(null, series.Id, series.Revision, "pause", null, null, null, default);
        Assert.All(f.Db.Reminders, r => Assert.Equal(ReminderStatus.Paused, r.Status));
        Assert.All(f.Db.Reminders, r => Assert.False(r.CanClaim(now.AddDays(2))));
        var pausedRevision = future.Revision;
        series = await f.Series.ChangeAsync(null, series.Id, series.Revision, "update", "novo padrão", null, null, default);
        Assert.Equal(pausedRevision, future.Revision); Assert.Equal("exceção futura", future.Text);
        f.Clock.Advance(TimeSpan.FromDays(2)); Assert.False(await f.Processor.ProcessNextAsync());
        await f.Series.ChangeAsync(null, series.Id, series.Revision, "resume", null, null, null, default);
        var expired = await f.Db.Reminders.AsNoTracking().SingleAsync(r => r.Id == elapsed.Id);
        Assert.Equal(ReminderStatus.Cancelled, expired.Status); Assert.True(expired.IsException);
        var preserved = await f.Db.Reminders.AsNoTracking().SingleAsync(r => r.Id == future.Id);
        Assert.Equal(ReminderStatus.Scheduled, preserved.Status); Assert.True(preserved.IsException);
        Assert.Equal("exceção futura", preserved.Text); Assert.Equal(At("2026-09-29T11:00Z"), preserved.DueAtUtc);
        var normal = Assert.Single(await f.Db.Reminders.Where(r => !r.IsException && r.Status == ReminderStatus.Scheduled).ToArrayAsync());
        Assert.Equal(At("2026-09-30T09:00Z"), normal.DueAtUtc); Assert.Equal("novo padrão", normal.Text);
        Assert.DoesNotContain(f.Db.Reminders, r => r.Status == ReminderStatus.Scheduled && r.DueAtUtc <= f.Clock.GetUtcNow());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitSeriesCancellationCancelsScheduledOrPausedOverridesWithoutErasingTheirTombstone(bool pauseFirst)
    {
        using var f = new Fixture(); var series = await f.Series.CreateAsync(null, "original", "UTC", Daily(time: "14:00"), "allEligible", default);
        var occurrence = Assert.Single(f.Db.Reminders); occurrence.Update("exceção", f.Clock.GetUtcNow().AddHours(2), f.Clock.GetUtcNow()); await f.Db.SaveChangesAsync();
        if (pauseFirst) series = await f.Series.ChangeAsync(null, series.Id, series.Revision, "pause", null, null, null, default);
        await f.Series.ChangeAsync(null, series.Id, series.Revision, "cancel", null, null, null, default);
        var stored = await f.Db.Reminders.AsNoTracking().SingleAsync(r => r.Id == occurrence.Id);
        Assert.Equal(ReminderStatus.Cancelled, stored.Status); Assert.True(stored.IsException); Assert.Equal("exceção", stored.Text);
        await f.SeriesStore.MaterializePendingAsync(default); Assert.Single(f.Db.Reminders);
    }
    [Fact]
    public async Task PausingSeriesDoesNotAcknowledgeOrWithdrawAnAlreadyTriggeredOccurrence()
    {
        using var f = new Fixture(); var series = await f.Series.CreateAsync(null, "original", "UTC", Daily(time: "14:00"), "allEligible", default);
        f.Clock.Advance(TimeSpan.FromHours(1)); Assert.True(await f.Processor.ProcessNextAsync());
        var triggered = await f.Db.Reminders.AsNoTracking().SingleAsync(); var revision = triggered.Revision;
        await f.SeriesStore.MaterializePendingAsync(default);
        await f.Series.ChangeAsync(null, series.Id, series.Revision, "pause", null, null, null, default);
        var stored = await f.Db.Reminders.AsNoTracking().SingleAsync(r => r.Id == triggered.Id);
        Assert.Equal(ReminderStatus.Triggered, stored.Status); Assert.Equal(revision, stored.Revision); Assert.Null(stored.AcknowledgedAt);
        Assert.Single(f.Db.Reminders.Where(r => r.Status == ReminderStatus.Paused));
    }
    [Fact]
    public async Task IndividualCancellationDuringPauseKeepsTheSlotSkippedAfterResumeAndEdit()
    {
        using var f = new Fixture(); f.Clock.Advance(TimeSpan.FromHours(-5));
        var series = await f.Series.CreateAsync(null, "original", "UTC", Daily(time: "09:00"), "allEligible", default);
        var occurrence = Assert.Single(f.Db.Reminders);
        series = await f.Series.ChangeAsync(null, series.Id, series.Revision, "pause", null, null, null, default);
        occurrence.Cancel(f.Clock.GetUtcNow()); await f.Db.SaveChangesAsync();
        series = await f.Series.ChangeAsync(null, series.Id, series.Revision, "resume", null, null, null, default);
        await f.Series.ChangeAsync(null, series.Id, series.Revision, "update", "novo texto", Daily(time: "10:00"), null, default);
        var tombstone = await f.Db.Reminders.AsNoTracking().SingleAsync(r => r.Id == occurrence.Id);
        Assert.Equal(ReminderStatus.Cancelled, tombstone.Status); Assert.True(tombstone.IsException);
        var next = Assert.Single(await f.Db.Reminders.Where(r => r.Status == ReminderStatus.Scheduled).ToArrayAsync());
        Assert.Equal(At("2026-09-28T10:00Z"), next.DueAtUtc);
    }
    [Fact]
    public void PauseResumePreservesFutureSnoozeAndExpiresAnElapsedOne()
    {
        var now = At("2026-09-27T13:00Z"); var series = new ReminderSeries("original", "UTC", Daily(time: "14:00"), null, now);
        var occurrence = Reminder.ForSeries(series, new(now.AddHours(1), "2026-09-27T14:00", 1), now);
        var fired = now.AddHours(1); occurrence.Claim(Guid.NewGuid(), fired); occurrence.BeginTrigger(fired); occurrence.Finish(null, true, fired);
        occurrence.Snooze(fired, 60); var due = occurrence.DueAtUtc;
        occurrence.PauseFutureForSeries(fired); Assert.Equal(ReminderStatus.Paused, occurrence.Status); Assert.False(occurrence.CanClaim(due));
        occurrence.ResumeFutureForSeries(fired.AddMinutes(30)); Assert.Equal(ReminderStatus.Scheduled, occurrence.Status);
        Assert.Equal(due, occurrence.DueAtUtc); Assert.Equal(2, occurrence.Generation); Assert.False(occurrence.IsException);
        occurrence.PauseFutureForSeries(fired.AddMinutes(30)); occurrence.ResumeFutureForSeries(due.AddMinutes(1));
        Assert.Equal(ReminderStatus.Cancelled, occurrence.Status); Assert.False(occurrence.CanClaim(due.AddMinutes(1)));
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
    [Fact] public void GeographicObserverReplayKeepsEventIdentityAndOriginalExpiryAfterDefiniteFailure()
    {
        var now = At("2026-10-15T12:00Z"); var eventId = Guid.NewGuid(); var expires = now.AddMinutes(5);
        var recipient = new ReminderRecipient(Guid.NewGuid(), 1, Guid.NewGuid(), now);
        recipient.Begin(now.AddMinutes(1), eventId, expires); recipient.Result("unavailable", null, false, now.AddMinutes(1));
        recipient.Begin(now.AddMinutes(2), eventId, expires);
        Assert.Equal(eventId, recipient.CommandId); Assert.Equal(expires, recipient.CommandExpiresAt);
        recipient.Result("timeout", "live_websocket", true, now.AddMinutes(2)); recipient.Begin(now.AddMinutes(3), eventId, expires);
        Assert.Equal(eventId, recipient.CommandId); Assert.Equal(expires, recipient.CommandExpiresAt);
        // A command attempted by an older backend remains conservative during upgrade.
        var existing = new ReminderRecipient(Guid.NewGuid(), 1, Guid.NewGuid(), now); existing.Begin(now); var attempted = existing.CommandId; var lifetime = existing.CommandExpiresAt;
        existing.Result("timeout", "live_websocket", true, now); existing.Begin(now.AddMinutes(1), eventId, expires);
        Assert.Equal(attempted, existing.CommandId); Assert.Equal(lifetime, existing.CommandExpiresAt);
    }
    [Fact] public void NewCommandsCannotOutliveDeliveryWindowAndAmbiguousReplayKeepsItsPersistedLifetime()
    {
        var now = At("2026-10-15T12:09Z"); var deadline = now.AddMinutes(1);
        var recipient = new ReminderRecipient(Guid.NewGuid(), 1, Guid.NewGuid(), now);
        recipient.Begin(now, notAfter: deadline); var id = recipient.CommandId;
        Assert.Equal(deadline, recipient.CommandExpiresAt);
        recipient.Result("timeout", "live_websocket", true, now);
        recipient.Begin(now.AddSeconds(10), notAfter: now.AddSeconds(20));
        Assert.Equal(id, recipient.CommandId); Assert.Equal(deadline, recipient.CommandExpiresAt);
        recipient.Result("unavailable", null, false, now.AddSeconds(10)); recipient.Begin(now.AddSeconds(20), notAfter: deadline);
        Assert.NotEqual(id, recipient.CommandId); Assert.Equal(deadline, recipient.CommandExpiresAt);
        var legacy = new ReminderRecipient(Guid.NewGuid(), 1, Guid.NewGuid(), now); legacy.Begin(now);
        Assert.Equal(now.AddMinutes(5), legacy.CommandExpiresAt);
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
