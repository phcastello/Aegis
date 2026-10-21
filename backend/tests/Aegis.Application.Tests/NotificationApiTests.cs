using System.Text.Json;
using Aegis.Api.Controllers;
using Aegis.Domain.Entities;
using Aegis.Application.Reminders;
using Aegis.Application.Observability;
using Aegis.Infrastructure;
using Aegis.Infrastructure.Persistence;
using Aegis.Infrastructure.Reminders;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class NotificationApiTests
{
    [Fact]
    public async Task RetiredRegistrationNeverCreatesOrRefreshesSubscriptionsAndReturnsStableTerminalResponse()
    {
        using var f = new Fixture();
        var historical = new PushSubscription("https://fcm.googleapis.com/historical", "old", "old", Guid.NewGuid(), null, f.Clock.GetUtcNow());
        f.Db.PushSubscriptions.Add(historical); await f.Db.SaveChangesAsync();
        var request = new NotificationsController.SubscriptionRequest(Guid.NewGuid(), "https://fcm.googleapis.com/new", "key", "auth", "test");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = Assert.IsType<ObjectResult>(f.Api.Register(request));
            Assert.Equal(410, result.StatusCode);
            var body = JsonSerializer.SerializeToElement(result.Value);
            Assert.Equal("web_push_retired", body.GetProperty("code").GetString());
            Assert.False(body.GetProperty("retryable").GetBoolean());
            Assert.Contains("pareado", body.GetProperty("error").GetString());
        }
        var stored = Assert.Single(f.Db.PushSubscriptions);
        Assert.Equal(historical.Endpoint, stored.Endpoint); Assert.Null(stored.DisabledAt);
        var configuration = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(f.Api.Configuration()).Value);
        Assert.False(configuration.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, configuration.GetProperty("publicKey").ValueKind);
        Assert.True(configuration.GetProperty("retired").GetBoolean());
        var status = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(f.Api.Status(stored.Id, new("old-management-token"))).Value);
        Assert.False(status.GetProperty("active").GetBoolean());
        Assert.IsType<NoContentResult>(f.Api.Disable(stored.Id, new("old-management-token")));
        Assert.Null(stored.DisabledAt); // Retiring the transport does not rewrite history.
    }

    [Fact]
    public async Task WebChatCanStillCreateReminderForEligibleNodeWithoutWebPush()
    {
        using var f = new Fixture(); using var metrics = new AegisMetrics();
        var service = new ReminderService(new ReminderStore(f.Db), f.Clock, metrics);
        var conversation = new Conversation(); f.Db.Conversations.Add(conversation);
        var node = new AegisNode(Guid.NewGuid(), "fixture", NodePlatform.Windows, "0.7.1", 1, f.Clock.GetUtcNow());
        f.Db.Nodes.Add(node); f.Db.NodeCapabilities.Add(new(node.Id, "notification.show", 1));
        await f.Db.SaveChangesAsync();
        var reminder = await service.CreateAsync(conversation.Id, "X", "2026-09-27T14:00:00Z", default);
        Assert.Equal("X", reminder.Text); Assert.Single(f.Db.Reminders); Assert.Empty(f.Db.PushSubscriptions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartupIgnoresAbsentOrMalformedLegacyVapidWhilePreservingNativePolling(bool staleEnvironment)
    {
        var values = new Dictionary<string, string?> { ["ConnectionStrings:AegisDatabase"] = "Host=unused;Database=unused", ["AEGIS_REMINDER_POLL_SECONDS"] = "7" };
        if (staleEnvironment)
        {
            values["AEGIS_WEB_PUSH_SUBJECT"] = "obsolete";
            values["AEGIS_WEB_PUSH_PUBLIC_KEY"] = "invalid";
            values["WebPush:PrivateKey"] = "invalid";
        }
        using var services = new ServiceCollection().AddInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(values).Build()).BuildServiceProvider();
        services.GetService<IStartupValidator>()?.Validate();
        Assert.Equal(7, services.GetRequiredService<IOptions<ReminderOptions>>().Value.PollSeconds);
        Assert.NotNull(services.GetRequiredService<IDataProtectionProvider>());
    }

    [Fact]
    public async Task LegacyInteractionValidatesReminderActionAndIdempotenceWithoutImplicitAcknowledgement()
    {
        using var f = new Fixture();
        var reminder = new Reminder("X", f.Clock.GetUtcNow().AddHours(1), "America/Sao_Paulo", null, f.Clock.GetUtcNow());
        f.Db.Reminders.Add(reminder); await f.Db.SaveChangesAsync();
        var ack = f.Token(reminder.Id, "acknowledge");
        Assert.IsType<ConflictObjectResult>(await f.Api.Acknowledge(reminder.Id, new(ack), default));
        reminder = (await f.Db.Reminders.FindAsync(reminder.Id))!;
        f.Clock.Advance(TimeSpan.FromHours(1));
        reminder.Claim(Guid.NewGuid(), f.Clock.GetUtcNow()); reminder.BeginTrigger(f.Clock.GetUtcNow()); await f.Db.SaveChangesAsync();
        Assert.IsType<NotFoundResult>(await f.Api.Open(reminder.Id, new(ack), default));
        Assert.IsType<NotFoundResult>(await f.Api.Acknowledge(Guid.NewGuid(), new(ack), default));
        Assert.IsType<NoContentResult>(await f.Api.Open(reminder.Id, new(f.Token(reminder.Id, "open")), default));
        Assert.Null((await f.Db.Reminders.FindAsync(reminder.Id))!.AcknowledgedAt);
        Assert.IsType<NoContentResult>(await f.Api.Acknowledge(reminder.Id, new(ack), default));
        var timestamp = (await f.Db.Reminders.FindAsync(reminder.Id))!.AcknowledgedAt;
        f.Clock.Advance(TimeSpan.FromSeconds(20));
        Assert.IsType<NoContentResult>(await f.Api.Acknowledge(reminder.Id, new(ack), default));
        Assert.Equal(timestamp, (await f.Db.Reminders.FindAsync(reminder.Id))!.AcknowledgedAt);
        f.Clock.Advance(TimeSpan.FromDays(31));
        Assert.IsType<NotFoundResult>(await f.Api.Acknowledge(reminder.Id, new(ack), default));
    }

    [Fact]
    public async Task OriginalLegacyTokenCannotAcknowledgeAnOccurrenceAfterSnooze()
    {
        using var f = new Fixture();
        var reminder = new Reminder("X", f.Clock.GetUtcNow().AddMinutes(1), "America/Sao_Paulo", null, f.Clock.GetUtcNow());
        var ack = f.Token(reminder.Id, "acknowledge");
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        reminder.Claim(Guid.NewGuid(), f.Clock.GetUtcNow()); reminder.BeginTrigger(f.Clock.GetUtcNow());
        reminder.Snooze(f.Clock.GetUtcNow(), 10);
        f.Clock.Advance(TimeSpan.FromMinutes(10));
        reminder.Claim(Guid.NewGuid(), f.Clock.GetUtcNow()); reminder.BeginTrigger(f.Clock.GetUtcNow());
        f.Db.Reminders.Add(reminder); await f.Db.SaveChangesAsync();
        Assert.IsType<NotFoundResult>(await f.Api.Acknowledge(reminder.Id, new(ack), default));
        Assert.Null((await f.Db.Reminders.FindAsync(reminder.Id))!.AcknowledgedAt);
    }

    [Fact]
    public async Task PreviouslyIssuedActionSurvivesKeyRingProviderRestart()
    {
        var directory = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aegis-reminder-keys-" + Guid.NewGuid().ToString("N")));
        directory.Create();
        try
        {
            using var f = new Fixture();
            var before = DataProtectionProvider.Create(directory, builder => builder.SetApplicationName("Aegis"));
            var reminder = new Reminder("restart", f.Clock.GetUtcNow().AddMinutes(1), "America/Sao_Paulo", null, f.Clock.GetUtcNow());
            f.Clock.Advance(TimeSpan.FromMinutes(1)); reminder.Claim(Guid.NewGuid(), f.Clock.GetUtcNow()); reminder.BeginTrigger(f.Clock.GetUtcNow());
            f.Db.Reminders.Add(reminder); await f.Db.SaveChangesAsync();
            var ack = LegacyReminderTokenFixture.Create(before, f.Clock, reminder.Id, "acknowledge");
            var after = DataProtectionProvider.Create(directory, builder => builder.SetApplicationName("Aegis"));
            var restarted = new NotificationsController(new ReminderStore(f.Db), new LegacyReminderInteractionTokens(after, f.Clock), f.Clock);
            Assert.IsType<NoContentResult>(await restarted.Acknowledge(reminder.Id, new(ack), default));
            Assert.NotNull((await f.Db.Reminders.FindAsync(reminder.Id))!.AcknowledgedAt);
        }
        finally { directory.Delete(true); }
    }

    private sealed class Fixture : IDisposable
    {
        public AegisDbContext Db { get; } = new(new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public ReminderTests.TestClock Clock { get; } = new();
        public IDataProtectionProvider Protection { get; } = new EphemeralDataProtectionProvider();
        public NotificationsController Api { get; }
        public Fixture() => Api = new(new ReminderStore(Db), new LegacyReminderInteractionTokens(Protection, Clock), Clock);
        public string Token(Guid reminderId, string action) => LegacyReminderTokenFixture.Create(Protection, Clock, reminderId, action);
        public void Dispose() => Db.Dispose();
    }
}
