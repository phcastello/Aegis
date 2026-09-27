using System.Security.Cryptography;
using System.Text.Json;
using Aegis.Api.Controllers;
using Aegis.Domain.Entities;
using Aegis.Application.Reminders;
using Aegis.Application.Observability;
using Aegis.Infrastructure.Persistence;
using Aegis.Infrastructure.Reminders;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class NotificationApiTests
{
    [Fact]
    public async Task SubscriptionRegistrationIsIdempotentSupportsDevicesAndRetainsDisabledRecords()
    {
        using var f = new Fixture(); var request = f.Request();
        var first = Assert.IsType<OkObjectResult>(await f.Api.Register(request, default));
        var value = JsonSerializer.SerializeToElement(first.Value);
        var id = value.GetProperty("subscriptionId").GetGuid(); var token = value.GetProperty("token").GetString()!;
        Assert.IsType<OkObjectResult>(await f.Api.Register(request, default)); Assert.Single(f.Db.PushSubscriptions);
        Assert.IsType<OkObjectResult>(await f.Api.Register(f.Request(), default)); Assert.Equal(2, f.Db.PushSubscriptions.Count());
        Assert.IsType<NotFoundResult>(await f.Api.Disable(id, new("wrong"), default));
        Assert.IsType<NoContentResult>(await f.Api.Disable(id, new(token), default));
        Assert.IsType<NoContentResult>(await f.Api.Disable(id, new(token), default));
        Assert.NotNull((await f.Db.PushSubscriptions.FindAsync(id))!.DisabledAt);
        Assert.IsType<OkObjectResult>(await f.Api.Register(request, default));
        Assert.Null((await f.Db.PushSubscriptions.FindAsync(id))!.DisabledAt);
        Assert.Equal(2, f.Db.PushSubscriptions.Count());
    }
    [Fact]
    public async Task RealSubscriptionRegistrationAndStatusUnlockReminderCreation()
    {
        using var f = new Fixture(); using var metrics = new AegisMetrics();
        using var http = new HttpClient();
        var push = new WebPushService(http, Microsoft.Extensions.Options.Options.Create(f.Options), f.Clock);
        var service = new ReminderService(new ReminderStore(f.Db), push, f.Clock, metrics);
        var conversation = new Conversation(); f.Db.Conversations.Add(conversation); await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<ReminderException>(() => service.CreateAsync(conversation.Id, "X", "2026-09-27T14:00:00Z", default));
        var request = f.Request();
        var result = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await f.Api.Register(request, default)).Value);
        var id = result.GetProperty("subscriptionId").GetGuid(); var token = result.GetProperty("token").GetString()!;
        var status = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await f.Api.Status(id, new(token, request.Endpoint), default)).Value);
        Assert.True(status.GetProperty("active").GetBoolean());
        var mismatch = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await f.Api.Status(id, new(token, request.Endpoint + "other"), default)).Value);
        Assert.False(mismatch.GetProperty("active").GetBoolean());
        Assert.IsType<NotFoundResult>(await f.Api.Status(id, new("tampered", request.Endpoint), default));
        Assert.NotNull(await service.CreateAsync(conversation.Id, "X", "2026-09-27T14:00:00Z", default));
        await f.Api.Disable(id, new(token), default);
        status = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await f.Api.Status(id, new(token, request.Endpoint), default)).Value);
        Assert.False(status.GetProperty("active").GetBoolean());
        await Assert.ThrowsAsync<ReminderException>(() => service.CreateAsync(conversation.Id, "X", "2026-09-27T14:00:00Z", default));
        Assert.Single(f.Db.Reminders);
        await f.Api.Register(request, default); f.Options.PrivateKey = "";
        await Assert.ThrowsAsync<ReminderException>(() => service.CreateAsync(conversation.Id, "X", "2026-09-27T14:00:00Z", default));
    }

    [Fact]
    public async Task RotatingEndpointDisablesOnlySameDeviceAndManagementTokenCannotSelectAnotherRecord()
    {
        using var f = new Fixture(); var request = f.Request();
        var first = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await f.Api.Register(request, default)).Value);
        var oldId = first.GetProperty("subscriptionId").GetGuid(); var token = first.GetProperty("token").GetString()!;
        var next = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await f.Api.Register(request with { Endpoint = request.Endpoint + "new" }, default)).Value);
        Assert.NotNull((await f.Db.PushSubscriptions.FindAsync(oldId))!.DisabledAt);
        Assert.Equal(1, f.Db.PushSubscriptions.Count(s => s.DisabledAt == null));
        Assert.IsType<NotFoundResult>(await f.Api.Disable(next.GetProperty("subscriptionId").GetGuid(), new(token), default));
        Assert.IsType<ConflictObjectResult>(await f.Api.Register(request with { DeviceId = Guid.NewGuid() }, default));
    }
    [Theory]
    [InlineData("http://fcm.googleapis.com/push")]
    [InlineData("https://127.0.0.1/push")]
    [InlineData("https://fcm.googleapis.com.attacker.test/push")]
    [InlineData("https://fcm.googleapis.com:444/push")]
    public async Task RegistrationRejectsNonChromiumOrUnsafeEndpoints(string endpoint)
    {
        using var f = new Fixture();
        Assert.IsType<BadRequestObjectResult>(await f.Api.Register(f.Request() with { Endpoint = endpoint }, default));
        Assert.Empty(f.Db.PushSubscriptions);
    }
    [Fact]
    public async Task MalformedKeysAreRejectedAndConfigurationNeverExposesPrivateMaterial()
    {
        using var f = new Fixture();
        Assert.IsType<BadRequestObjectResult>(await f.Api.Register(f.Request() with { Auth = "bad" }, default));
        var json = JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(f.Api.Configuration()).Value);
        Assert.Contains(f.Options.PublicKey, json); Assert.DoesNotContain(f.Options.PrivateKey, json);
        f.Options.PrivateKey = "";
        Assert.IsType<ConflictObjectResult>(await f.Api.Register(f.Request(), default));
    }
    [Fact]
    public async Task InteractionValidatesReminderActionAndIdempotenceWithoutImplicitAcknowledgement()
    {
        using var f = new Fixture();
        var r = new Reminder("X", f.Clock.GetUtcNow().AddHours(1), "America/Sao_Paulo", null, f.Clock.GetUtcNow());
        f.Db.Reminders.Add(r); await f.Db.SaveChangesAsync();
        var ack = f.Tokens.Create(r.Id, Guid.NewGuid(), "acknowledge");
        Assert.IsType<ConflictObjectResult>(await f.Api.Acknowledge(r.Id, new(ack), default));
        r = (await f.Db.Reminders.FindAsync(r.Id))!;
        f.Clock.Advance(TimeSpan.FromHours(1));
        r.Claim(Guid.NewGuid(), f.Clock.GetUtcNow()); r.BeginTrigger(f.Clock.GetUtcNow()); await f.Db.SaveChangesAsync();
        Assert.IsType<NotFoundResult>(await f.Api.Open(r.Id, new(ack), default));
        Assert.IsType<NotFoundResult>(await f.Api.Acknowledge(Guid.NewGuid(), new(ack), default));
        var open = f.Tokens.Create(r.Id, Guid.NewGuid(), "open");
        Assert.IsType<NoContentResult>(await f.Api.Open(r.Id, new(open), default));
        Assert.Null((await f.Db.Reminders.FindAsync(r.Id))!.AcknowledgedAt);
        Assert.IsType<NoContentResult>(await f.Api.Acknowledge(r.Id, new(ack), default));
        var timestamp = (await f.Db.Reminders.FindAsync(r.Id))!.AcknowledgedAt;
        f.Clock.Advance(TimeSpan.FromSeconds(20));
        Assert.IsType<NoContentResult>(await f.Api.Acknowledge(r.Id, new(ack), default));
        Assert.Equal(timestamp, (await f.Db.Reminders.FindAsync(r.Id))!.AcknowledgedAt);
    }
    [Fact]
    public async Task NotificationAndManagementTokensSurviveKeyRingProviderRestart()
    {
        var directory = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aegis-reminder-keys-" + Guid.NewGuid().ToString("N")));
        directory.Create();
        try
        {
            using var f = new Fixture();
            var before = DataProtectionProvider.Create(directory, builder => builder.SetApplicationName("Aegis"));
            var tokens = new ReminderInteractionTokens(before, f.Clock);
            var api = new NotificationsController(f.Db, new ReminderStore(f.Db), tokens, before, f.Clock, Microsoft.Extensions.Options.Options.Create(f.Options));
            var request = f.Request();
            var registered = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await api.Register(request, default)).Value);
            var id = registered.GetProperty("subscriptionId").GetGuid(); var management = registered.GetProperty("token").GetString()!;
            var reminder = new Reminder("restart", f.Clock.GetUtcNow().AddMinutes(1), "America/Sao_Paulo", null, f.Clock.GetUtcNow());
            f.Clock.Advance(TimeSpan.FromMinutes(1)); reminder.Claim(Guid.NewGuid(), f.Clock.GetUtcNow()); reminder.BeginTrigger(f.Clock.GetUtcNow());
            f.Db.Reminders.Add(reminder); await f.Db.SaveChangesAsync();
            var ack = tokens.Create(reminder.Id, id, "acknowledge");
            // New provider loads the same persisted key ring, as a recreated container does.
            var after = DataProtectionProvider.Create(directory, builder => builder.SetApplicationName("Aegis"));
            var restartedTokens = new ReminderInteractionTokens(after, f.Clock);
            var restarted = new NotificationsController(f.Db, new ReminderStore(f.Db), restartedTokens, after, f.Clock, Microsoft.Extensions.Options.Options.Create(f.Options));
            Assert.IsType<OkObjectResult>(await restarted.Status(id, new(management, request.Endpoint), default));
            Assert.IsType<NoContentResult>(await restarted.Acknowledge(reminder.Id, new(ack), default));
            Assert.NotNull((await f.Db.Reminders.FindAsync(reminder.Id))!.AcknowledgedAt);
        }
        finally { directory.Delete(true); }
    }

    private sealed class Fixture : IDisposable
    {
        public AegisDbContext Db { get; } = new(new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public ReminderTests.TestClock Clock { get; } = new();
        public WebPushOptions Options { get; }
        public ReminderInteractionTokens Tokens { get; }
        public NotificationsController Api { get; }
        public Fixture()
        {
            using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256); var key = ec.ExportParameters(true);
            Options = new WebPushOptions { Subject = "mailto:eval@example.test", PrivateKey = WebEncoders.Base64UrlEncode(key.D!), PublicKey = WebEncoders.Base64UrlEncode(new byte[] { 4 }.Concat(key.Q.X!).Concat(key.Q.Y!).ToArray()) };
            var protection = new EphemeralDataProtectionProvider(); Tokens = new ReminderInteractionTokens(protection, Clock);
            Api = new NotificationsController(Db, new ReminderStore(Db), Tokens, protection, Clock, Microsoft.Extensions.Options.Options.Create(Options));
        }
        public NotificationsController.SubscriptionRequest Request()
        {
            using var ec = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256); var q = ec.ExportParameters(false).Q;
            return new(Guid.NewGuid(), "https://fcm.googleapis.com/push/" + Guid.NewGuid(), WebEncoders.Base64UrlEncode(new byte[] { 4 }.Concat(q.X!).Concat(q.Y!).ToArray()), WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(16)), "test");
        }
        public void Dispose() => Db.Dispose();
    }
}
