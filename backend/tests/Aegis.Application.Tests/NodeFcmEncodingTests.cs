using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Aegis.Application.Nodes;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Nodes;
using Aegis.Infrastructure.Persistence;
using Aegis.Infrastructure.Reminders;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aegis.Application.Tests;

// A local handler captures the real serialized HTTP body. No request reaches FCM.
public sealed class NodeFcmEncodingTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private sealed class Credentials : INodeFcmCredentials
    {
        public bool Configured => true;
        public Task<string> AccessTokenAsync(CancellationToken ct) => Task.FromResult("synthetic-oauth-not-a-provider-credential");
    }
    private sealed class Handler : HttpMessageHandler
    {
        public readonly List<byte[]> Bodies = [];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("https://fcm.googleapis.com/v1/projects/synthetic-project/messages:send", request.RequestUri!.AbsoluteUri);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            Bodies.Add(await request.Content.ReadAsByteArrayAsync(ct));
            return new(Status) { Content = new StringContent("{}") };
        }
    }
    private sealed class Clients(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) { Assert.Equal("node-fcm", name); return client; } }
    private sealed class Fixture : IAsyncDisposable
    {
        public readonly AegisDbContext Db = new(new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public readonly Guid NodeId = Guid.NewGuid();
        public readonly Handler Handler = new();
        private readonly HttpClient client;
        private readonly EphemeralDataProtectionProvider protection = new();
        public readonly NodeFcmTransport Transport;
        public Fixture()
        {
            client = new(Handler); var credentials = new Credentials();
            var registrations = new NodePushRegistrations(Db, protection, credentials, TimeProvider.System);
            Transport = new(Db, registrations, credentials, new Clients(client), Options.Create(new NodeFcmOptions { ProjectId = "synthetic-project" }), TimeProvider.System, NullLogger<NodeFcmTransport>.Instance);
        }
        public async Task Initialize()
        {
            Db.Nodes.Add(new(NodeId, "Synthetic encoding phone", NodePlatform.Android, "0.7.0-unstable.12", 1, DateTimeOffset.UtcNow)); await Db.SaveChangesAsync();
            await new NodePushRegistrations(Db, protection, new Credentials(), TimeProvider.System).RegisterAsync(NodeId, "synthetic-token-not-a-provider-registration", default);
        }
        public string ActionToken()
        {
            var now = DateTimeOffset.UtcNow;
            return new ReminderActionTokens(protection, TimeProvider.System).Create(new Reminder("Synthetic token scope", now.AddMinutes(1), "UTC", null, now), now.AddDays(1), NodeId);
        }
        public async ValueTask DisposeAsync() { client.Dispose(); await Db.DisposeAsync(); }
    }
    private static NodeNotificationCommand Command(string body, bool reminder = true, string? token = null) => new(Guid.NewGuid(), reminder ? "reminder.notify" : "notification.show", 1,
        DateTimeOffset.UtcNow.AddMinutes(1), reminder ? new("Aegis", body, Guid.NewGuid(), 1, 1, token ?? new string('t', 50)) : new("Aegis", body));
    private static Dictionary<string, string> Data(Guid node, NodeNotificationCommand command) => new() {
        ["nodeId"] = node.ToString(), ["type"] = command.Capability, ["version"] = "1", ["commandId"] = command.CommandId.ToString(), ["expiresAt"] = command.ExpiresAt.ToString("O"),
        ["input"] = JsonSerializer.Serialize(command.Input, Json)
    };

    [Theory]
    [InlineData("notification.show", "HIGH")]
    [InlineData("reminder.notify", "HIGH")]
    [InlineData("reminder.withdraw", "NORMAL")]
    public async Task OnlyVisibleNotificationsRequestHighPriority(string capability, string expected)
    {
        await using var f = new Fixture(); await f.Initialize();
        var command = capability == "reminder.withdraw"
            ? new NodeNotificationCommand(Guid.NewGuid(), capability, 1, DateTimeOffset.UtcNow.AddMinutes(5), new(null, null, Guid.NewGuid(), Generation: 1))
            : Command("Synthetic priority", capability == "reminder.notify");
        Assert.Equal("accepted", await f.Transport.SendAsync(f.NodeId, command, default));
        using var json = JsonDocument.Parse(Assert.Single(f.Handler.Bodies));
        Assert.Equal(expected, json.RootElement.GetProperty("message").GetProperty("android").GetProperty("priority").GetString());
    }

    [Fact]
    public async Task DomainSynchronizationUsesNormalPriority()
    {
        await using var f = new Fixture(); await f.Initialize();
        var command = new NodeDomainCommand(Guid.NewGuid(), "geofence.monitor", 1, DateTimeOffset.UtcNow.AddMinutes(5), new { revision = 1 });
        Assert.Equal("accepted", await f.Transport.SendDomainAsync(f.NodeId, command, default));
        using var json = JsonDocument.Parse(Assert.Single(f.Handler.Bodies));
        var message = json.RootElement.GetProperty("message");
        Assert.Equal("domain.sync", message.GetProperty("data").GetProperty("type").GetString());
        Assert.Equal("NORMAL", message.GetProperty("android").GetProperty("priority").GetString());
    }

    [Fact]
    public async Task ProactiveFcmRetryPreservesTheSameCommandAndMessageIdsAsTheLiveCommand()
    {
        await using var f = new Fixture(); await f.Initialize();
        var command = new NodeNotificationCommand(Guid.NewGuid(), NotificationContract.ConversationCapability, 1, DateTimeOffset.UtcNow.AddMinutes(1),
            new("Aegis", "Nova mensagem da Aegis", ConversationId: Guid.NewGuid(), MessageId: Guid.NewGuid()));
        f.Handler.Status = HttpStatusCode.ServiceUnavailable;
        Assert.Equal("failed", await f.Transport.SendAsync(f.NodeId, command, default));
        f.Handler.Status = HttpStatusCode.OK;
        Assert.Equal("accepted", await f.Transport.SendAsync(f.NodeId, command, default));
        var live = JsonSerializer.SerializeToElement(command, Json);
        Assert.Equal(2, f.Handler.Bodies.Count);
        foreach (var body in f.Handler.Bodies) {
            using var json = JsonDocument.Parse(body);
            var message = json.RootElement.GetProperty("message");
            var data = message.GetProperty("data");
            Assert.Equal("HIGH", message.GetProperty("android").GetProperty("priority").GetString());
            Assert.Equal(live.GetProperty("commandId").GetString(), data.GetProperty("commandId").GetString());
            Assert.Equal(NotificationContract.ConversationCapability, data.GetProperty("type").GetString());
            using var input = JsonDocument.Parse(data.GetProperty("input").GetString()!);
            Assert.Equal(live.GetProperty("input").GetProperty("messageId").GetString(), input.RootElement.GetProperty("messageId").GetString());
            Assert.Equal(live.GetProperty("input").GetProperty("conversationId").GetString(), input.RootElement.GetProperty("conversationId").GetString());
            Assert.Equal(4, input.RootElement.EnumerateObject().Count());
        }
    }

    [Fact]
    public async Task WithdrawalProvider503IsNotReportedAsAccepted()
    {
        await using var f = new Fixture(); await f.Initialize(); f.Handler.Status = HttpStatusCode.ServiceUnavailable;
        var command = new NodeNotificationCommand(Guid.NewGuid(), "reminder.withdraw", 1, DateTimeOffset.UtcNow.AddMinutes(5), new(null, null, Guid.NewGuid(), Generation: 1));
        Assert.Equal("failed", await f.Transport.SendAsync(f.NodeId, command, default));
        using var json = JsonDocument.Parse(Assert.Single(f.Handler.Bodies));
        Assert.Equal("NORMAL", json.RootElement.GetProperty("message").GetProperty("android").GetProperty("priority").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SixHundredCjkCharactersUseUtf8WithoutArtificialUnicodeExpansion(bool reminder)
    {
        await using var f = new Fixture(); await f.Initialize(); var body = new string('界', 600);
        var command = Command(body, reminder, f.ActionToken()); NotificationContract.Validate("Aegis", body, 60);
        Assert.Equal("accepted", await f.Transport.SendAsync(f.NodeId, command, default));
        var bytes = Assert.Single(f.Handler.Bodies); var wire = Encoding.UTF8.GetString(bytes);
        Assert.Contains(body, wire); Assert.DoesNotContain("\\u754C", wire, StringComparison.OrdinalIgnoreCase);
        using var json = JsonDocument.Parse(bytes); var data = json.RootElement.GetProperty("message").GetProperty("data");
        Assert.True(Encoding.UTF8.GetByteCount(data.GetRawText()) <= 3900);
        if (reminder) {
            Assert.Equal(6, data.EnumerateObject().Count()); using var input = JsonDocument.Parse(data.GetProperty("input").GetString()!);
            Assert.Equal(body, input.RootElement.GetProperty("body").GetString()); Assert.Equal(command.Input.InteractionToken, input.RootElement.GetProperty("interactionToken").GetString());
        } else { Assert.Equal(7, data.EnumerateObject().Count()); Assert.Equal(body, data.GetProperty("body").GetString()); Assert.False(data.TryGetProperty("input", out _)); }
    }

    [Theory]
    [InlineData(3900, "accepted", 1)]
    [InlineData(3901, "failed", 0)]
    public async Task RealSerializedDataBudgetAcceptsBoundaryAndRejectsOneExtraByteBeforeHttp(int targetBytes, string expected, int calls)
    {
        await using var f = new Fixture(); await f.Initialize(); var command = Command(new string('界', 600), token: "");
        var tokenLength = targetBytes - JsonSerializer.SerializeToUtf8Bytes(Data(f.NodeId, command), Json).Length;
        Assert.InRange(tokenLength, 20, 2048); command = command with { Input = command.Input with { InteractionToken = new string('t', tokenLength) } };
        Assert.Equal(targetBytes, JsonSerializer.SerializeToUtf8Bytes(Data(f.NodeId, command), Json).Length);
        Assert.Equal(expected, await f.Transport.SendAsync(f.NodeId, command, default)); Assert.Equal(calls, f.Handler.Bodies.Count);
        if (calls == 1) {
            using var json = JsonDocument.Parse(f.Handler.Bodies.Single());
            Assert.Equal(targetBytes, Encoding.UTF8.GetByteCount(json.RootElement.GetProperty("message").GetProperty("data").GetRawText()));
        }
    }

    [Fact]
    public async Task QuotesBackslashesAndControlsRemainEscapedThroughBothJsonLayers()
    {
        await using var f = new Fixture(); await f.Initialize(); const string body = "Synthetic \"quoted\" \\ line\nnext\ttab <>& 界";
        var command = Command(body, token: f.ActionToken()); Assert.Equal("accepted", await f.Transport.SendAsync(f.NodeId, command, default));
        var bytes = Assert.Single(f.Handler.Bodies); var wire = Encoding.UTF8.GetString(bytes); Assert.DoesNotContain("\n", wire); Assert.DoesNotContain("\t", wire);
        using var json = JsonDocument.Parse(bytes); using var input = JsonDocument.Parse(json.RootElement.GetProperty("message").GetProperty("data").GetProperty("input").GetString()!);
        Assert.Equal(body, input.RootElement.GetProperty("body").GetString()); Assert.Contains("<>& 界", wire);
    }
}
