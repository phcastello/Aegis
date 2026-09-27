using System.Text.Json;
using Aegis.Application.Observability;
using Aegis.Application.Reminders;
using Aegis.Application.Tools;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Persistence;
using Aegis.Infrastructure.Reminders;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class ReminderTests
{
    [Fact]
    public async Task CreationRequiresFunctionalChannelAndFutureAbsoluteTime()
    {
        using var f = new Fixture();
        await Assert.ThrowsAsync<ReminderException>(() => f.Service.CreateAsync(f.Conversation, "ração", f.Due, default));
        Assert.Empty(f.Db.Reminders);
        f.Subscribe(); await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.CreateAsync(f.Conversation, "ração", "2026-10-01T18:00:00", default));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.CreateAsync(f.Conversation, "ração", "2026-09-26T18:00:00-03:00", default));
        f.Push.IsConfigured = false;
        await Assert.ThrowsAsync<ReminderException>(() => f.Service.CreateAsync(f.Conversation, "ração", f.Due, default));
        f.Push.IsConfigured = true;
        var r = await f.Service.CreateAsync(f.Conversation, "ração", f.Due, default);
        Assert.Equal(DateTimeOffset.Parse("2026-09-27T14:00:00Z"), r.DueAtUtc);
        Assert.Equal("America/Sao_Paulo", r.TimeZoneId);
        Assert.Equal(f.Conversation, r.SourceConversationId);
    }
    [Fact]
    public async Task TextAndTimeCanChangeAndCancelIsIdempotent()
    {
        using var f = new Fixture();
        var r = await f.Create();
        r = await f.Service.ChangeAsync(f.Conversation, r.Id, "documento e caneta", "2026-09-27T12:00:00-03:00", false, default);
        Assert.Equal("documento e caneta", r.Text);
        Assert.Equal(DateTimeOffset.Parse("2026-09-27T15:00:00Z"), r.DueAtUtc);
        r = await f.Service.ChangeAsync(f.Conversation, r.Id, null, null, true, default);
        Assert.Equal(ReminderStatus.Cancelled, r.Status);
        var cancelled = r.CancelledAt;
        r = await f.Service.ChangeAsync(f.Conversation, r.Id, null, null, true, default);
        Assert.Equal(cancelled, r.CancelledAt);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.ChangeAsync(f.Conversation, r.Id, "X", null, false, default));
    }
    [Fact]
    public async Task ListIsOrderedBoundedAndFiltersExclusiveUpperBound()
    {
        using var f = new Fixture();
        await f.Create();
        for (var i = 1; i <= 3; i++) await f.Service.CreateAsync(f.Conversation, $"item{i}", $"2026-09-27T{14+i:00}:00:00Z", default);
        var items = await f.Service.ListAsync(f.Conversation, "2026-09-27T15:00:00Z", "2026-09-27T17:00:00Z", 1, default);
        Assert.Equal(2, items.Count); // one extra only to expose hasMore
        Assert.Equal("item1", items[0].Text);
        Assert.Equal("item2", items[1].Text);
        Assert.True(await f.Store.WasObservedAsync(f.Conversation, items[0].Id, f.Clock.GetUtcNow(), default));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.ListAsync(f.Conversation, null, null, 500, default));
    }
    [Fact]
    public async Task ReferencesMustBeObservedInSameConversationAndExpireWithInjectedClock()
    {
        using var f = new Fixture(); var r = await f.Create();
        await Assert.ThrowsAsync<ReminderException>(() => f.Service.ChangeAsync(Guid.NewGuid(), r.Id, "X", null, false, default));
        await Assert.ThrowsAsync<ReminderException>(() => f.Service.ChangeAsync(f.Conversation, Guid.NewGuid(), "X", null, false, default));
        f.Clock.Advance(TimeSpan.FromMinutes(31));
        await Assert.ThrowsAsync<ReminderException>(() => f.Service.ChangeAsync(f.Conversation, r.Id, "X", null, false, default));
        await f.Service.ListAsync(f.Conversation, null, null, 20, default);
        Assert.Equal("X", (await f.Service.ChangeAsync(f.Conversation, r.Id, "X", null, false, default)).Text);
    }
    [Fact]
    public async Task ToolsReturnActualResultsAndRejectUnknownOrInvalidFields()
    {
        using var f = new Fixture(); f.Subscribe(); await f.Db.SaveChangesAsync();
        var context = new ToolExecutionContext(f.Conversation, Guid.NewGuid(), "me lembra");
        var create = new ReminderCreateTool(f.Service);
        Assert.False((await create.ExecuteAsync(JsonSerializer.SerializeToElement(new { text = "X", dueAt = f.Due, unexpected = true }), context)).Success);
        Assert.False((await create.ExecuteAsync(JsonSerializer.SerializeToElement(new { text = 4, dueAt = f.Due }), context)).Success);
        Assert.True((await create.ExecuteAsync(JsonSerializer.SerializeToElement(new { text = "X", dueAt = f.Due }), context)).Success);
        var list = await new ReminderListTool(f.Service).ExecuteAsync(JsonSerializer.SerializeToElement(new { }), context);
        Assert.True(list.Success);
        var id = JsonDocument.Parse(list.Content).RootElement.GetProperty("reminders")[0].GetProperty("reminderId").GetGuid();
        Assert.True((await new ReminderUpdateTool(f.Service).ExecuteAsync(JsonSerializer.SerializeToElement(new { reminderId = id, text = "Y" }), context)).Success);
        Assert.True((await new ReminderCancelTool(f.Service).ExecuteAsync(JsonSerializer.SerializeToElement(new { reminderId = id }), context)).Success);
    }
    [Fact]
    public async Task WorkerWaitsUntilDueThenPushesWithoutModelAndDoesNotRepeat()
    {
        using var f = new Fixture(); var r = await f.Create();
        Assert.False(await f.Processor.ProcessNextAsync()); Assert.Empty(f.Push.Payloads);
        f.Clock.Advance(TimeSpan.FromHours(1));
        Assert.True(await f.Processor.ProcessNextAsync());
        Assert.Single(f.Push.Payloads); Assert.False(await f.Processor.ProcessNextAsync());
        var actual = await f.Db.Reminders.FindAsync(r.Id);
        Assert.Equal(ReminderStatus.Triggered, actual!.Status);
        Assert.Equal(f.Clock.GetUtcNow(), actual.TriggeredAt);
        Assert.NotNull(Assert.Single(f.Db.ReminderDeliveryAttempts).AcceptedAt);
        var payload = JsonDocument.Parse(f.Push.Payloads[0]).RootElement;
        Assert.Equal("ração", payload.GetProperty("text").GetString());
        Assert.False(payload.TryGetProperty("endpoint", out _));
    }
    [Fact]
    public async Task OverdueReminderSurvivesDowntimeAndCancelledReminderNeverFires()
    {
        using var f = new Fixture(); var cancelled = await f.Create();
        await f.Service.ChangeAsync(f.Conversation, cancelled.Id, null, null, true, default);
        var overdue = await f.Service.CreateAsync(f.Conversation, "overdue", f.Due, default);
        f.Clock.Advance(TimeSpan.FromHours(2));
        Assert.True(await f.Processor.ProcessNextAsync()); Assert.Single(f.Push.Payloads);
        Assert.Equal("overdue", JsonDocument.Parse(f.Push.Payloads[0]).RootElement.GetProperty("text").GetString());
        Assert.False(await f.Processor.ProcessNextAsync());
    }
    [Fact]
    public async Task ExpiredLeaseRecoversButActiveLeaseCannotBeClaimedAgain()
    {
        using var f = new Fixture(); await f.Create(); f.Clock.Advance(TimeSpan.FromHours(1));
        Assert.NotNull(await f.Store.ClaimAsync(f.Clock.GetUtcNow(), default));
        Assert.Null(await f.Store.ClaimAsync(f.Clock.GetUtcNow(), default));
        Assert.False(await f.Processor.ProcessNextAsync());
        f.Clock.Advance(TimeSpan.FromMinutes(3));
        Assert.True(await f.Processor.ProcessNextAsync()); Assert.Single(f.Push.Payloads);
    }
    [Fact]
    public async Task RetryPreservesAcceptedDeviceAndBoundsTransientAttempts()
    {
        using var f = new Fixture(); await f.Create(); f.Subscribe(); await f.Db.SaveChangesAsync();
        f.Push.Results.Enqueue(new PushResult(201)); f.Push.Results.Enqueue(new PushResult(503, "push_http_error"));
        f.Clock.Advance(TimeSpan.FromHours(1)); await f.Processor.ProcessNextAsync();
        Assert.Equal(2, f.Push.Payloads.Count);
        Assert.False(await f.Processor.ProcessNextAsync());
        f.Clock.Advance(TimeSpan.FromSeconds(10)); await f.Processor.ProcessNextAsync();
        Assert.Equal(3, f.Push.Payloads.Count);
        Assert.Equal(2, f.Db.ReminderDeliveryAttempts.Count(a => a.AcceptedAt != null));
        Assert.Equal(ReminderStatus.Triggered, Assert.Single(f.Db.Reminders).Status);
    }
    [Fact]
    public async Task GlobalAcknowledgementStopsOtherDeviceRetryAndRetainsHistory()
    {
        using var f = new Fixture(); var reminder = await f.Create(); f.Subscribe(); await f.Db.SaveChangesAsync();
        f.Push.Results.Enqueue(new PushResult(201)); f.Push.Results.Enqueue(new PushResult(503, "push_http_error"));
        f.Clock.Advance(TimeSpan.FromHours(1)); await f.Processor.ProcessNextAsync();
        var before = await f.Db.ReminderDeliveryAttempts.AsNoTracking().OrderBy(a => a.Id).ToListAsync();
        Assert.Equal(2, before.Count); Assert.Single(before, a => a.RetryAt != null);
        await f.Store.LockedAsync(reminder.Id, r => { r.Acknowledge(f.Clock.GetUtcNow()); return Task.FromResult(true); }, default);
        var acknowledged = f.Clock.GetUtcNow(); f.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.False(await f.Processor.ProcessNextAsync()); Assert.Equal(2, f.Push.Payloads.Count);
        var actual = (await f.Db.Reminders.FindAsync(reminder.Id))!;
        Assert.Equal(acknowledged, actual.AcknowledgedAt); Assert.Equal(ReminderStatus.Triggered, actual.Status);
        Assert.Null(actual.CancelledAt); Assert.Null(actual.LeaseId); Assert.False(actual.CanClaim(f.Clock.GetUtcNow()));
        var after = await f.Db.ReminderDeliveryAttempts.AsNoTracking().OrderBy(a => a.Id).ToListAsync();
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
    }
    [Fact]
    public async Task PreviouslyAcknowledgedScheduledRowsCannotBeListedOrClaimed()
    {
        using var f = new Fixture(); var reminder = await f.Create(); f.Clock.Advance(TimeSpan.FromHours(1));
        await f.Processor.ProcessNextAsync();
        await f.Store.LockedAsync(reminder.Id, r => { r.Acknowledge(f.Clock.GetUtcNow()); return Task.FromResult(true); }, default);
        var original = (await f.Db.Reminders.FindAsync(reminder.Id))!.AcknowledgedAt;
        // Reproduce rows acknowledged before ACK stopped scheduling retries.
        f.Db.Entry((await f.Db.Reminders.FindAsync(reminder.Id))!).Property(r => r.Status).CurrentValue = ReminderStatus.Scheduled;
        await f.Db.SaveChangesAsync();
        Assert.Empty(await f.Store.ListAsync(null, null, 20, default));
        Assert.Null(await f.Store.ClaimAsync(f.Clock.GetUtcNow(), default));
        await f.Store.LockedAsync(reminder.Id, r => { r.Acknowledge(f.Clock.GetUtcNow()); return Task.FromResult(true); }, default);
        var actual = (await f.Db.Reminders.FindAsync(reminder.Id))!;
        Assert.Equal(original, actual.AcknowledgedAt); Assert.Equal(ReminderStatus.Triggered, actual.Status);
    }

    [Fact]
    public async Task AcknowledgementDuringProcessingReleasesLeaseWithoutCancelOrRecoverySend()
    {
        using var f = new Fixture(); var reminder = await f.Create(); f.Clock.Advance(TimeSpan.FromHours(1));
        var claim = (await f.Store.ClaimAsync(f.Clock.GetUtcNow(), default))!.Value;
        await f.Store.LockedAsync(reminder.Id, r => {
            r.BeginTrigger(f.Clock.GetUtcNow()); Assert.Equal(ReminderStatus.Processing, r.Status);
            r.Acknowledge(f.Clock.GetUtcNow());
            Assert.False(r.OwnsLease(claim.LeaseId));
            r.Finish(f.Clock.GetUtcNow().AddSeconds(10), false, f.Clock.GetUtcNow());
            Assert.Equal(ReminderStatus.Triggered, r.Status);
            return Task.FromResult(true);
        }, default);
        f.Clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Null(await f.Store.ClaimAsync(f.Clock.GetUtcNow(), default));
        Assert.False(await f.Processor.ProcessNextAsync()); Assert.Empty(f.Push.Payloads);
    }

    [Fact]
    public async Task TransientFailuresExhaustFiveAttemptsWithBackoff()
    {
        using var f = new Fixture(); await f.Create();
        for (var i = 0; i < 6; i++) f.Push.Results.Enqueue(new PushResult(429, "push_http_error"));
        f.Clock.Advance(TimeSpan.FromHours(1));
        foreach (var seconds in new[] { 0, 10, 30, 120, 300 }) { f.Clock.Advance(TimeSpan.FromSeconds(seconds)); Assert.True(await f.Processor.ProcessNextAsync()); }
        Assert.Equal(5, f.Push.Payloads.Count);
        Assert.Equal(ReminderStatus.Failed, Assert.Single(f.Db.Reminders).Status);
        Assert.False(await f.Processor.ProcessNextAsync());
    }
    [Theory]
    [InlineData(404)] [InlineData(410)]
    public async Task PermanentSubscriptionFailureDisablesRatherThanDeletes(int status)
    {
        using var f = new Fixture(); await f.Create(); f.Push.Results.Enqueue(new PushResult(status, "push_http_error"));
        f.Clock.Advance(TimeSpan.FromHours(1)); await f.Processor.ProcessNextAsync();
        Assert.NotNull(Assert.Single(f.Db.PushSubscriptions).DisabledAt);
        Assert.Equal(status, Assert.Single(f.Db.ReminderDeliveryAttempts).HttpStatus);
        Assert.False(await f.Processor.ProcessNextAsync());
    }
    [Fact]
    public async Task AcknowledgementAndOpeningAreDistinctAndIdempotent()
    {
        using var f = new Fixture(); var r = await f.Create();
        Assert.Throws<ArgumentException>(() => r.Acknowledge(f.Clock.GetUtcNow()));
        f.Clock.Advance(TimeSpan.FromHours(1)); await f.Processor.ProcessNextAsync();
        await f.Store.LockedAsync(r.Id, r => { r.Open(f.Clock.GetUtcNow()); return Task.FromResult(true); }, default);
        r = (await f.Db.Reminders.FindAsync(r.Id))!;
        Assert.NotNull(r.OpenedAt); Assert.Null(r.AcknowledgedAt);
        await f.Store.LockedAsync(r.Id, r => { r.Acknowledge(f.Clock.GetUtcNow()); return Task.FromResult(true); }, default);
        r = (await f.Db.Reminders.FindAsync(r.Id))!; var first = r.AcknowledgedAt; var updated = r.UpdatedAt;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        await f.Store.LockedAsync(r.Id, r => { r.Acknowledge(f.Clock.GetUtcNow()); return Task.FromResult(true); }, default);
        Assert.Equal(first, (await f.Db.Reminders.FindAsync(r.Id))!.AcknowledgedAt);
        Assert.Equal(updated, (await f.Db.Reminders.FindAsync(r.Id))!.UpdatedAt);
    }
    [Fact]
    public void SignedInteractionIsBoundToReminderActionAndExpiry()
    {
        var clock = new TestClock(); var tokens = new ReminderInteractionTokens(new EphemeralDataProtectionProvider(), clock);
        var id = Guid.NewGuid(); var token = tokens.Create(id, Guid.NewGuid(), "acknowledge");
        Assert.True(tokens.Validate(token, id, "acknowledge"));
        Assert.False(tokens.Validate(token, id, "open")); Assert.False(tokens.Validate(token, Guid.NewGuid(), "acknowledge"));
        Assert.False(tokens.Validate(token + "tamper", id, "acknowledge"));
        clock.Advance(TimeSpan.FromDays(31)); Assert.False(tokens.Validate(token, id, "acknowledge"));
    }
    [Fact]
    public async Task RecoverySkipsPersistedAcceptanceAndRetainsUnknownAttempt()
    {
        using var f = new Fixture(); var r = await f.Create(); var first = Assert.Single(f.Db.PushSubscriptions); var second = f.Subscribe();
        f.Clock.Advance(TimeSpan.FromHours(1));
        var claim = await f.Store.ClaimAsync(f.Clock.GetUtcNow(), default);
        await f.Store.LockedAsync(r.Id, r => { r.BeginTrigger(f.Clock.GetUtcNow()); return Task.FromResult(true); }, default);
        var accepted = new ReminderDeliveryAttempt(r.Id, first.Id, 1, f.Clock.GetUtcNow()); accepted.Complete(201, null, null, f.Clock.GetUtcNow());
        f.Db.ReminderDeliveryAttempts.Add(accepted);
        f.Db.ReminderDeliveryAttempts.Add(new ReminderDeliveryAttempt(r.Id, second.Id, 1, f.Clock.GetUtcNow()));
        await f.Db.SaveChangesAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(3)); await f.Processor.ProcessNextAsync();
        Assert.Single(f.Push.Payloads);
        Assert.Equal(3, f.Db.ReminderDeliveryAttempts.Count());
        Assert.Single(f.Db.ReminderDeliveryAttempts, a => a.FailureReason == "push_outcome_unknown");
    }
    [Fact]
    public async Task LastListOrderSurvivesTimeUpdateAndTimezoneCanBeAudited()
    {
        using var f = new Fixture(); var first = await f.Create();
        var second = await f.Service.CreateAsync(f.Conversation, "second", "2026-09-27T16:00:00Z", default, "Europe/London");
        Assert.Equal("Europe/London", second.TimeZoneId);
        await f.Service.ListAsync(f.Conversation, null, null, 20, default);
        await f.Service.ChangeAsync(f.Conversation, first.Id, null, "2026-09-27T17:00:00Z", false, default);
        var context = await f.Service.GetContextAsync(f.Conversation, default);
        Assert.NotNull(context);
        Assert.True(context.IndexOf(first.Id.ToString(), StringComparison.Ordinal) < context.IndexOf(second.Id.ToString(), StringComparison.Ordinal));
        f.Clock.Advance(TimeSpan.FromMinutes(31)); Assert.Null(await f.Service.GetContextAsync(f.Conversation, default));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.CreateAsync(f.Conversation, "X", f.Due, default, "Invented/Timezone"));
    }
    [Fact]
    public async Task PushPayloadStaysUnderProtocolLimitEvenWithLongUnicodeTextAndSignedTokens()
    {
        using var f = new Fixture(); f.Subscribe(); await f.Db.SaveChangesAsync();
        await f.Service.CreateAsync(f.Conversation, new string('漢', 600), f.Due, default);
        f.Clock.Advance(TimeSpan.FromHours(1)); await f.Processor.ProcessNextAsync();
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(Assert.Single(f.Push.Payloads)) < 3900);
        Assert.Throws<ArgumentException>(() => new Reminder(new string('\u2028', 600), f.Clock.GetUtcNow().AddHours(1), "America/Sao_Paulo", null, f.Clock.GetUtcNow()));
    }

    [Fact]
    public async Task TriggerDelayMetricMeasuresDowntimeAndCancellationCountsOnlyOneTransition()
    {
        using var f = new Fixture();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        var delays = new List<double>(); var cancellations = new List<long>();
        listener.InstrumentPublished = (instrument, l) => { if (ReferenceEquals(instrument, f.Metrics.ReminderTriggerDelay) || ReferenceEquals(instrument, f.Metrics.RemindersCancelled)) l.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, state) => { if (instrument.Name == "aegis_reminder_trigger_delay_ms") { Assert.Equal(0, tags.Length); delays.Add(value); } });
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) => { if (instrument.Name == "aegis_reminders_cancelled_total") cancellations.Add(value); });
        listener.Start();
        var cancelled = await f.Create();
        await f.Service.ChangeAsync(f.Conversation, cancelled.Id, null, null, true, default);
        await f.Service.ChangeAsync(f.Conversation, cancelled.Id, null, null, true, default);
        await f.Service.CreateAsync(f.Conversation, "late", f.Due, default);
        f.Clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(7)); await f.Processor.ProcessNextAsync();
        Assert.Equal(420000, Assert.Single(delays)); Assert.Equal(1, Assert.Single(cancellations));
        Assert.All(delays, delay => Assert.True(delay >= 0));
    }

    internal sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.Parse("2026-09-27T13:00:00Z");
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
    internal sealed class FakePush : IWebPushClient
    {
        public bool IsConfigured { get; set; } = true;
        public List<string> Payloads { get; } = [];
        public Queue<PushResult> Results { get; } = [];
        public Task<PushResult> SendAsync(PushSubscription subscription, string payload, CancellationToken ct) { Payloads.Add(payload); return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : new PushResult(201)); }
    }
    private sealed class Fixture : IDisposable
    {
        public Guid Conversation { get; } = Guid.NewGuid();
        public string Due => "2026-09-27T11:00:00-03:00";
        public TestClock Clock { get; } = new();
        public FakePush Push { get; } = new();
        public AegisDbContext Db { get; } = new(new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public AegisMetrics Metrics { get; } = new();
        public ReminderStore Store { get; }
        public ReminderService Service { get; }
        public ReminderProcessor Processor { get; }
        public Fixture()
        {
            Store = new ReminderStore(Db); Service = new ReminderService(Store, Push, Clock, Metrics);
            Processor = new ReminderProcessor(Db, Store, Push, new ReminderInteractionTokens(new EphemeralDataProtectionProvider(), Clock), Clock, Metrics);
        }
        public PushSubscription Subscribe() { var s = new PushSubscription("https://fcm.googleapis.com/test/" + Guid.NewGuid(), "key", "auth", Guid.NewGuid(), null, Clock.GetUtcNow()); Db.PushSubscriptions.Add(s); return s; }
        public async Task<Reminder> Create() { if (!Db.PushSubscriptions.Any()) Subscribe(); await Db.SaveChangesAsync(); return await Service.CreateAsync(Conversation, "ração", Due, default); }
        public void Dispose() { Db.Dispose(); Metrics.Dispose(); }
    }
}
