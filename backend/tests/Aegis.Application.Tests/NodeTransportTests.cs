using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Aegis.Api.Controllers;
using Aegis.Api.Nodes;
using Aegis.Api.Nodes.Transport;
using Aegis.Application.Nodes;
using Aegis.Infrastructure.Nodes;
using Aegis.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
namespace Aegis.Application.Tests;

public sealed class NodeTransportTests
{
    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; public void Advance(int seconds) => Now += TimeSpan.FromSeconds(seconds); }
    private static NodeConnectionRegistry Registry(Clock clock, int max = 256) => new(clock,
        Options.Create(new NodeTransportOptions { MaxConnections = max }), NullLogger<NodeConnectionRegistry>.Instance);
    [Fact] public void DuplicateCleanupCannotRemoveNewPresence()
    {
        var clock = new Clock(); using var registry = Registry(clock); var id = Guid.NewGuid();
        var a = registry.Register(id); var b = registry.Register(id);
        Assert.True(a.Ended.IsCancellationRequested); Assert.Equal("replaced", a.Reason);
        Assert.False(registry.Unregister(a)); Assert.True(registry.IsOnline(id)); Assert.Same(b, registry.Lookup(id));
        Assert.True(registry.Unregister(b)); Assert.False(registry.IsOnline(id));
    }
    [Fact] public void HeartbeatTimeoutAndReconnectUseOnlyServerClock()
    {
        var clock = new Clock(); using var registry = Registry(clock); var id = Guid.NewGuid(); var lease = registry.Register(id);
        clock.Advance(25); Assert.True(registry.Heartbeat(lease)); clock.Advance(74); Assert.True(registry.IsOnline(id));
        clock.Advance(1); Assert.False(registry.IsOnline(id)); Assert.False(registry.Heartbeat(lease)); registry.ExpireStale();
        Assert.True(lease.Ended.IsCancellationRequested); Assert.Equal("heartbeat_timeout", lease.Reason);
        registry.Register(id); Assert.True(registry.IsOnline(id));
        using var restarted = Registry(clock); Assert.False(restarted.IsOnline(id));
    }
    [Fact] public void CapacityAndHeartbeatAbuseAreBounded()
    {
        var clock = new Clock(); using var registry = Registry(clock, 1); var id = Guid.NewGuid(); var lease = registry.Register(id);
        Assert.Throws<NodeException>(() => registry.Register(Guid.NewGuid()));
        Assert.True(registry.Heartbeat(lease)); Assert.False(registry.Heartbeat(lease)); Assert.False(registry.IsOnline(id));
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public required WebApplication App; public required NodeRegistry Nodes; public required NodeConnectionRegistry Connections;
        public HttpClient Http => App.GetTestClient();
        public async Task<(NodeView Node, string Secret)> Pair(string platform = "windows")
        {
            var request = NodeIdentityTests.Request((await Nodes.CreateCodeAsync(null)).Code, platform);
            var receipt = await Nodes.PairAsync(request); return (await Nodes.FinalizeAsync(new(request.AttemptId, receipt.Credential)), receipt.Credential);
        }
        public async Task<WebSocket> Connect(string credential, string protocol = "1")
        {
            var client = App.GetTestServer().CreateWebSocketClient();
            client.ConfigureRequest = r => { r.Headers.Authorization = "AegisNode " + credential; r.Headers["X-Aegis-Node-Protocol"] = protocol; };
            return await client.ConnectAsync(new Uri("ws://localhost/api/nodes/connect"), CancellationToken.None);
        }
        public async ValueTask DisposeAsync() { Connections.DisconnectAll(); await App.DisposeAsync(); }
    }
    private static async Task<Fixture> Host(string? connection = null, bool fast = false)
    {
        var name = Guid.NewGuid().ToString(); var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Services.AddControllers().AddApplicationPart(typeof(NodesController).Assembly);
        if (fast) builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["NodeTransport:HeartbeatSeconds"] = "1", ["NodeTransport:TimeoutSeconds"] = "2",
            ["NodeTransport:MinimumHeartbeatSeconds"] = "1", ["NodeTransport:WatchdogSeconds"] = "1", ["NodeTransport:PersistSeconds"] = "2" });
        builder.Services.AddNodeApi(builder.Configuration); builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddDbContext<AegisDbContext>(o => { if (connection is null) o.UseInMemoryDatabase(name); else o.UseNpgsql(connection); });
        builder.Services.AddScoped<INodeRegistry, NodeRegistry>(); builder.Services.AddScoped<INodeTransportHistory, NodeTransportHistory>();
        var app = builder.Build(); app.UseWebSockets(); app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter(); app.MapControllers(); await app.StartAsync();
        var registry = app.Services.GetRequiredService<NodeConnectionRegistry>();
        var dbOptions = new DbContextOptionsBuilder<AegisDbContext>();
        if (connection is null) dbOptions.UseInMemoryDatabase(name); else dbOptions.UseNpgsql(connection);
        var db = new AegisDbContext(dbOptions.Options);
        return new() { App = app, Connections = registry, Nodes = new(db, TimeProvider.System, registry) };
    }
    private static object Message(string type = "hello", int protocol = 1) => new {
        protocolVersion = protocol, type, messageId = Guid.NewGuid(), sentAt = DateTimeOffset.UtcNow.AddYears(5),
        payload = type == "hello" ? new { appVersion = "0.7.0-stage.5" } : null };
    private static Task Send(WebSocket ws, object message) => ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)), WebSocketMessageType.Text, true, CancellationToken.None);
    private static async Task<(WebSocketReceiveResult Result, JsonElement Json)> Read(WebSocket ws)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); var buffer = new byte[8192];
        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token);
        return (result, result.MessageType == WebSocketMessageType.Text ? JsonSerializer.Deserialize<JsonElement>(buffer.AsSpan(0, result.Count)) : default);
    }
    private static async Task Hello(WebSocket ws) { await Send(ws, Message()); var ack = await Read(ws); Assert.Equal("hello_ack", ack.Json.GetProperty("type").GetString()); }
    private static async Task Eventually(Func<Task<bool>> predicate)
    { using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!await predicate()) await Task.Delay(10, limit.Token); }
    private static async Task Advertise(WebSocket socket, params NodeCapability[] capabilities)
    {
        await Send(socket, new { protocolVersion = 1, type = "hello", messageId = Guid.NewGuid(), sentAt = DateTimeOffset.UtcNow,
            payload = new { appVersion = "0.7.0-stage.7", capabilities = capabilities.Select(c => new { name = c.Name, version = c.Version }) } });
        Assert.Equal("hello_ack", (await Read(socket)).Json.GetProperty("type").GetString());
    }
    [Fact] public async Task OldAndNewHelloReplacePersistedAndCurrentSessionCapabilities()
    {
        await using var f = await Host(); var (node, secret) = await f.Pair();
        using (var old = await f.Connect(secret)) { await Hello(old); Assert.Empty(f.Connections.LiveCapabilities(node.Id)!); await old.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test", default); }
        using var newer = await f.Connect(secret); await Advertise(newer, new NodeCapability("audio.input", 1), new NodeCapability("audio.output", 1), new NodeCapability("future.feature", 1));
        Assert.Equal(2, f.Connections.LiveCapabilities(node.Id)!.Count); Assert.Equal(2, (await f.Nodes.MeAsync(node.Id)).Capabilities.Count);
        using var replacement = await f.Connect(secret); await Advertise(replacement, new NodeCapability("audio.output", 2)); await Read(newer);
        Assert.Single(f.Connections.LiveCapabilities(node.Id)!); Assert.Equal(2, (await f.Nodes.MeAsync(node.Id)).Capabilities.Single().Version);
        await replacement.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test", default);
        await Eventually(() => Task.FromResult(!f.Connections.IsOnline(node.Id)));
        Assert.Null(f.Connections.LiveCapabilities(node.Id)); Assert.Single((await f.Nodes.MeAsync(node.Id)).Capabilities);
        using var downgraded = await f.Connect(secret); await Hello(downgraded); Assert.Empty((await f.Nodes.MeAsync(node.Id)).Capabilities);
    }
    [Fact] public async Task RealHttpResolverUsesTwoDifferentAdvertisedSetsAndPriority()
    {
        await using var f = await Host(); var (a, secretA) = await f.Pair(); var (b, secretB) = await f.Pair();
        using var sa = await f.Connect(secretA); using var sb = await f.Connect(secretB);
        await Advertise(sa, new NodeCapability("audio.input", 1), new NodeCapability("audio.output", 1)); await Advertise(sb, new NodeCapability("audio.input", 1));
        using var http = f.Http; http.DefaultRequestHeaders.Add("Authorization", "AegisNode " + secretB);
        var priority = await http.PatchAsJsonAsync($"/api/nodes/{b.Id}/priority", new { targetPriority = 20 }); Assert.True(priority.IsSuccessStatusCode);
        var chosen = await http.PostAsJsonAsync("/api/nodes/resolve", new NodeTargetRequest([new("audio.output", 1)]));
        Assert.Equal(a.Id, (await chosen.Content.ReadFromJsonAsync<NodeTargetResult>())!.Node!.Id);
        var shared = await http.PostAsJsonAsync("/api/nodes/resolve", new NodeTargetRequest([new("audio.input", 1)]));
        Assert.Equal(b.Id, (await shared.Content.ReadFromJsonAsync<NodeTargetResult>())!.Node!.Id);
        var preferred = await http.PostAsJsonAsync("/api/nodes/resolve", new NodeTargetRequest([new("audio.input", 1)], a.Id));
        Assert.Equal(a.Id, (await preferred.Content.ReadFromJsonAsync<NodeTargetResult>())!.Node!.Id);
        var none = await http.PostAsJsonAsync("/api/nodes/resolve", new NodeTargetRequest([new("audio.output", 2)]));
        Assert.Equal("no_eligible_node", (await none.Content.ReadFromJsonAsync<NodeTargetResult>())!.Code);
        using var anonymous = f.Http; Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/nodes/resolve", new NodeTargetRequest([new("audio.output", 1)]))).StatusCode);
    }
    [Theory] [InlineData("zero")] [InlineData("duplicate")] [InlineData("conflicting")] [InlineData("name")] [InlineData("too_many")]
    [InlineData("giant")] [InlineData("null")] [InlineData("extra_field")]
    public async Task InvalidCapabilityHelloClosesBoundedly(string scenario)
    {
        await using var f = await Host(); var (node, secret) = await f.Pair(); using var ws = await f.Connect(secret);
        object? capabilities = scenario switch {
            "null" => null,
            "zero" => new[] { new { name = "audio.output", version = 0 } },
            "name" => new[] { new { name = "Audio.Output", version = 1 } },
            "giant" => new[] { new { name = new string('a', 65), version = 1 } },
            "extra_field" => new[] { new { name = "audio.output", version = 1, arbitrary = true } },
            "too_many" => Enumerable.Range(0, 33).Select(i => new { name = "future.x" + i, version = 1 }).ToArray(),
            _ => new[] { new { name = "audio.output", version = 1 }, new { name = "audio.output", version = scenario == "conflicting" ? 2 : 1 } } };
        await Send(ws, new { protocolVersion = 1, type = "hello", messageId = Guid.NewGuid(), sentAt = DateTimeOffset.UtcNow, payload = new { appVersion = "0.7.0-stage.7", capabilities } });
        Assert.Equal(1008, (int)(await Read(ws)).Result.CloseStatus!); Assert.False(f.Connections.IsOnline(node.Id));
    }
    [Fact] public async Task OldHelloAdmissionCannotOverwriteReplacementSnapshot()
    {
        var clock = new Clock(); using var registry = Registry(clock); var id = Guid.NewGuid();
        var mutable = new List<NodeCapability> { new("audio.input", 1) }; var a = registry.Register(id, capabilities: mutable); mutable.Clear();
        Assert.Single(registry.LiveCapabilities(id)!);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string snapshot = "";
        var old = registry.AnnounceAsync(a, async () => { started.SetResult(); await finish.Task; snapshot = "A"; }, default); await started.Task;
        var b = registry.Register(id, capabilities: [new("audio.output", 1)]);
        var current = registry.AnnounceAsync(b, () => { snapshot = "B"; return Task.CompletedTask; }, default);
        Assert.False(current.IsCompleted); finish.SetResult(); await Task.WhenAll(old, current);
        Assert.Equal("B", snapshot); Assert.False(registry.Unregister(a)); Assert.Equal("audio.output", registry.LiveCapabilities(id)!.Single().Name);
    }
    [Fact] public async Task AuthHelloHeartbeatDisconnectReconnectAndMetadata()
    {
        await using var f = await Host(); var (node, secret) = await f.Pair();
        using var socket = await f.Connect(secret); Assert.False(f.Connections.IsOnline(node.Id)); await Hello(socket);
        Assert.True(f.Connections.IsOnline(node.Id)); Assert.Equal("online", (await f.Nodes.MeAsync(node.Id)).Availability);
        Assert.Equal("0.7.0-stage.5", (await f.Nodes.MeAsync(node.Id)).AppVersion);
        // Diagnostic client clock is deliberately five years ahead; history uses backend time.
        Assert.True((await f.Nodes.MeAsync(node.Id)).LastSeenAt < DateTimeOffset.UtcNow.AddMinutes(1));
        await Send(socket, new { protocolVersion = 1, type = "heartbeat", messageId = Guid.NewGuid(), sentAt = DateTimeOffset.UtcNow.AddYears(5) });
        Assert.Equal("heartbeat_ack", (await Read(socket)).Json.GetProperty("type").GetString());
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test", CancellationToken.None);
        await Eventually(async () => (await f.Nodes.MeAsync(node.Id)).Availability == "offline");
        await Eventually(async () => (await f.Nodes.MeAsync(node.Id)).LastHeartbeatAt is not null);
        using var reconnected = await f.Connect(secret); await Hello(reconnected); Assert.True(f.Connections.IsOnline(node.Id));
    }
    [Fact] public async Task InvalidDisabledRevokedAndWrongHeaderAreRejectedBeforeUpgrade()
    {
        await using var f = await Host(); var (node, secret) = await f.Pair();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Connect("invalid"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Connect(secret, "2"));
        await f.Nodes.SetEnabledAsync(null, node.Id, false); await Assert.ThrowsAsync<InvalidOperationException>(() => f.Connect(secret));
        await f.Nodes.SetEnabledAsync(null, node.Id, true); using (var socket = await f.Connect(secret)) await Hello(socket);
        await f.Nodes.RevokeAsync(node.Id, node.Id); await Assert.ThrowsAsync<InvalidOperationException>(() => f.Connect(secret));
        using var http = f.Http; Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/nodes/connect")).StatusCode);
        http.DefaultRequestHeaders.Add("Authorization", "AegisNode " + secret);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/nodes/connect")).StatusCode);
    }
    [Fact] public async Task TwoLiveNodesHaveFreshHttpAvailabilityAndExactlyOneCurrentId()
    {
        await using var f = await Host(); var (a, secretA) = await f.Pair(); var (b, secretB) = await f.Pair();
        using var socketA = await f.Connect(secretA); using var socketB = await f.Connect(secretB);
        await Hello(socketA); await Hello(socketB);
        foreach (var secret in new[] { secretA, secretB })
        {
            using var http = f.Http; http.DefaultRequestHeaders.Add("Authorization", "AegisNode " + secret);
            var me = await http.GetFromJsonAsync<NodeView>("/api/nodes/me");
            var list = await http.GetFromJsonAsync<NodeView[]>("/api/nodes");
            Assert.Equal(2, list!.Length); Assert.Single(list, n => n.Id == me!.Id);
            Assert.All(list, n => Assert.Equal("online", n.Availability)); Assert.Equal("online", me!.Availability);
        }
        await socketA.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test", CancellationToken.None);
        await Eventually(() => Task.FromResult(!f.Connections.IsOnline(a.Id)));
        using var observer = f.Http; observer.DefaultRequestHeaders.Add("Authorization", "AegisNode " + secretB);
        var after = await observer.GetFromJsonAsync<NodeView[]>("/api/nodes");
        Assert.Equal("offline", after!.Single(n => n.Id == a.Id).Availability);
        Assert.Equal("online", after.Single(n => n.Id == b.Id).Availability);
    }
    [Fact] public async Task AuthenticatedRequestWithoutUpgradeHasExplicitSafeDiagnostic()
    {
        await using var f = await Host(); var (_, secret) = await f.Pair(); using var http = f.Http;
        http.DefaultRequestHeaders.Add("Authorization", "AegisNode " + secret);
        http.DefaultRequestHeaders.Add("X-Aegis-Node-Protocol", "1");
        var response = await http.GetAsync("/api/nodes/connect");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("websocket_required", Assert.Single(response.Headers.GetValues("X-Aegis-Node-Error")));
    }
    [Theory]
    [InlineData("{", 1008)]
    [InlineData("oversized", 1009)]
    [InlineData("wrong_version", 4006)]
    [InlineData("unknown", 1008)]
    [InlineData("heartbeat_first", 1008)]
    public async Task BadProtocolCloses(string scenario, int expected)
    {
        await using var f = await Host(); var (node, secret) = await f.Pair(); using var socket = await f.Connect(secret);
        var data = scenario switch {
            "oversized" => new string('a', 4097), "wrong_version" => JsonSerializer.Serialize(Message(protocol: 2)),
            "unknown" => JsonSerializer.Serialize(Message("command")), "heartbeat_first" => JsonSerializer.Serialize(new { protocolVersion = 1, type = "heartbeat", messageId = Guid.NewGuid(), sentAt = DateTimeOffset.UtcNow }), _ => scenario };
        await socket.SendAsync(Encoding.UTF8.GetBytes(data), WebSocketMessageType.Text, true, CancellationToken.None);
        var close = await Read(socket); Assert.Equal(WebSocketMessageType.Close, close.Result.MessageType); Assert.Equal(expected, (int)close.Result.CloseStatus!);
        Assert.False(f.Connections.IsOnline(node.Id));
    }
    [Fact] public async Task ActiveDisableReenableRevokeAndDuplicateSocketCleanup()
    {
        await using var f = await Host(); var (node, secret) = await f.Pair();
        using var a = await f.Connect(secret); await Hello(a); using var b = await f.Connect(secret); await Hello(b);
        await Read(a); Assert.True(f.Connections.IsOnline(node.Id));
        await f.Nodes.SetEnabledAsync(null, node.Id, false); Assert.False(f.Connections.IsOnline(node.Id));
        Assert.Equal(4003, (int)(await Read(b)).Result.CloseStatus!);
        await f.Nodes.SetEnabledAsync(null, node.Id, true); using var c = await f.Connect(secret); await Hello(c);
        await f.Nodes.RevokeAsync(node.Id, node.Id); Assert.False(f.Connections.IsOnline(node.Id));
        Assert.Equal(4001, (int)(await Read(c)).Result.CloseStatus!);
    }
    [NodePostgresTests.PostgresFact]
    public Task RealPostgresTransportLifecycleAndMonotonicHistory() => NodePostgresTests.WithDatabase(async create => {
        await using var db = create();
        await using var f = await Host(db.Database.GetConnectionString());
        var (node, secret) = await f.Pair();
        using var a = await f.Connect(secret); await Hello(a); Assert.True(f.Connections.IsOnline(node.Id));
        await Send(a, new { protocolVersion = 1, type = "heartbeat", messageId = Guid.NewGuid(), sentAt = DateTimeOffset.UtcNow });
        Assert.Equal("heartbeat_ack", (await Read(a)).Json.GetProperty("type").GetString());
        await a.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test", CancellationToken.None);
        await Eventually(() => Task.FromResult(!f.Connections.IsOnline(node.Id)));
        await Eventually(async () => (await f.Nodes.MeAsync(node.Id)).LastHeartbeatAt is not null);
        using var b = await f.Connect(secret); await Hello(b);
        await f.Nodes.SetEnabledAsync(null, node.Id, false); await Read(b); Assert.False(f.Connections.IsOnline(node.Id));
        // Old cleanup must not overwrite administrative fields or newer server timestamps.
        var future = DateTimeOffset.UtcNow.AddMinutes(1);
        await new NodeTransportHistory(db).SeenAsync(node.Id, future, future, default);
        await new NodeTransportHistory(db).SeenAsync(node.Id, future.AddMinutes(-2), future.AddMinutes(-2), default);
        db.ChangeTracker.Clear(); var historical = await db.Nodes.SingleAsync(n => n.Id == node.Id);
        Assert.False(historical.Enabled); Assert.Equal(future.ToUnixTimeMilliseconds(), historical.LastSeenAt!.Value.ToUnixTimeMilliseconds());
        await f.Nodes.SetEnabledAsync(null, node.Id, true); using var c = await f.Connect(secret); await Hello(c);
        await f.Nodes.RevokeAsync(node.Id, node.Id); Assert.Equal(4001, (int)(await Read(c)).Result.CloseStatus!);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Connect(secret));
    });

    [Fact] public void PendingHelloIsNeverOnlineAndCannotActivateAfterDisableOrReplacement()
    {
        var clock = new Clock(); using var registry = Registry(clock); var id = Guid.NewGuid();
        var pending = registry.Register(id, ready: false); Assert.False(registry.IsOnline(id));
        registry.Disconnect(id, "node_disabled"); Assert.False(registry.Activate(pending));
        var a = registry.Register(id, ready: false); var b = registry.Register(id, ready: false);
        Assert.False(registry.Activate(a)); Assert.True(registry.Activate(b)); Assert.True(registry.IsOnline(id));
    }

    [Fact] public async Task WatchdogClosesSilentSocketWithoutTcpDisconnect()
    {
        await using var f = await Host(fast: true); var (node, secret) = await f.Pair();
        using var socket = await f.Connect(secret); await Hello(socket);
        Assert.True(f.Connections.IsOnline(node.Id));
        var closed = await Read(socket); Assert.Equal(4008, (int)closed.Result.CloseStatus!);
        Assert.False(f.Connections.IsOnline(node.Id));
    }

    [Theory]
    [InlineData("success")][InlineData("expired")][InlineData("unsupported")][InlineData("permission_denied")][InlineData("failed")][InlineData("duplicate")]
    public async Task RealCommandAndHeartbeatShareWriterWithoutBlocking(string status)
    {
        await using var f=await Host();var (node,secret)=await f.Pair();using var socket=await f.Connect(secret);
        await Advertise(socket,new NodeCapability("notification.show",1));
        var command=new NodeNotificationCommand(Guid.NewGuid(),"notification.show",1,DateTimeOffset.UtcNow.AddSeconds(60),new("Aegis","Olá"));
        var sending=f.Connections.SendAsync(node.Id,command,default);
        var received=await Read(socket);Assert.Equal("command",received.Json.GetProperty("type").GetString());
        Assert.Equal("busy", (await f.Connections.SendAsync(node.Id, command, default)).Status); // Pending work has not executed yet.
        var payload=received.Json.GetProperty("payload");Assert.Equal(command.CommandId,payload.GetProperty("commandId").GetGuid());Assert.Equal("Olá",payload.GetProperty("input").GetProperty("body").GetString());
        await Send(socket,new {protocolVersion=1,type="heartbeat",messageId=Guid.NewGuid(),sentAt=DateTimeOffset.UtcNow});Assert.Equal("heartbeat_ack",(await Read(socket)).Json.GetProperty("type").GetString());
        Assert.False(sending.IsCompleted);Assert.True(f.Connections.IsOnline(node.Id));
        await Send(socket,new {protocolVersion=1,type="command_result",messageId=Guid.NewGuid(),sentAt=DateTimeOffset.UtcNow,payload=new {commandId=command.CommandId,status}});
        Assert.Equal(status,(await sending).Status);Assert.True(f.Connections.IsOnline(node.Id));
    }
    [Theory]
    [InlineData("windows")] [InlineData("android")]
    public async Task ReminderToolThroughProcessorAndRealWebSocketPersistsNativeAcceptance(string platform)
    {
        await using var f = await Host(); var (node, secret) = await f.Pair(platform);
        using var socket = await f.Connect(secret); await Advertise(socket, new NodeCapability("notification.show", 1));
        await using var scope = f.App.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<AegisDbContext>();
        using var metrics = new Aegis.Application.Observability.AegisMetrics();
        var store = new Aegis.Infrastructure.Reminders.ReminderStore(db);
        var service = new Aegis.Application.Reminders.ReminderService(store, TimeProvider.System, metrics);
        var tool = new Aegis.Application.Reminders.ReminderCreateTool(service);
        var context = new Aegis.Application.Tools.ToolExecutionContext(Guid.NewGuid(), Guid.NewGuid(), "me lembra daqui um segundo de teste");
        var created = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { text = "teste nativo", dueAt = DateTimeOffset.UtcNow.AddSeconds(1).ToString("O") }), context);
        Assert.True(created.Success); await Task.Delay(1100);
        var processor = new Aegis.Infrastructure.Reminders.ReminderProcessor(db, store,
            scope.ServiceProvider.GetRequiredService<INodeTargetResolver>(), scope.ServiceProvider.GetRequiredService<INodeNotificationDispatcher>(), TimeProvider.System, metrics);
        var sending = processor.ProcessNextAsync();
        var received = (await Read(socket)).Json; Assert.Equal("command", received.GetProperty("type").GetString());
        var command = received.GetProperty("payload"); var commandId = command.GetProperty("commandId").GetGuid();
        Assert.Equal("notification.show", command.GetProperty("capability").GetString()); Assert.Equal(1, command.GetProperty("capabilityVersion").GetInt32());
        Assert.Equal("Aegis", command.GetProperty("input").GetProperty("title").GetString()); Assert.Equal("teste nativo", command.GetProperty("input").GetProperty("body").GetString());
        await Send(socket, new { protocolVersion = 1, type = "command_result", messageId = Guid.NewGuid(), sentAt = DateTimeOffset.UtcNow, payload = new { commandId, status = "success" } });
        Assert.True(await sending); Assert.False(await processor.ProcessNextAsync());
        var attempt = await db.ReminderDeliveryAttempts.SingleAsync(); Assert.Equal(node.Id, attempt.NodeId); Assert.Equal(commandId, attempt.CommandId);
        Assert.Equal("success", attempt.Result); Assert.Equal("live_websocket", attempt.Transport); Assert.NotNull(attempt.AcceptedAt);
        Assert.Equal(Aegis.Domain.Entities.ReminderStatus.Triggered, (await db.Reminders.SingleAsync()).Status); Assert.Empty(await db.PushSubscriptions.ToListAsync());
    }
    [Fact] public async Task LiveDiagnosticTravelsWithSameCommandResult() {
        await using var f = await Host(); var (node, secret) = await f.Pair(); using var socket = await f.Connect(secret);
        await Advertise(socket, new NodeCapability("notification.show", 1));
        var command = new NodeNotificationCommand(Guid.NewGuid(), "notification.show", 1, DateTimeOffset.UtcNow.AddSeconds(60), new("Aegis", "fixture"));
        var pending = f.Connections.SendAsync(node.Id, command, default); await Read(socket);
        await Send(socket, new { protocolVersion = 1, type = "command_result", messageId = Guid.NewGuid(), sentAt = DateTimeOffset.UtcNow,
            payload = new { commandId = command.CommandId, status = "failed", diagnosticCode = "android_notification_build_failed" } });
        var result = await pending; Assert.Equal("failed", result.Status); Assert.Equal("android_notification_build_failed", result.DiagnosticCode);
        Assert.True(f.Connections.IsOnline(node.Id));
    }
    [Theory]
    [InlineData("\"arbitrary-secret-message\"")]
    [InlineData("null")]
    [InlineData("123")]
    [InlineData("{}")]
    [InlineData("\"android_notification_build_failed\",\"diagnosticCode\":\"android_notification_post_failed\"")]
    [InlineData("\"" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" + "\"")]
    public async Task UntrustedDiagnosticCodeIsRejected(string diagnosticJson) {
        await using var f = await Host(); var (_, secret) = await f.Pair(); using var socket = await f.Connect(secret);
        await Advertise(socket, new NodeCapability("notification.show", 1));
        var text = "{\"protocolVersion\":1,\"type\":\"command_result\",\"messageId\":\"" + Guid.NewGuid() + "\",\"sentAt\":\"" + DateTimeOffset.UtcNow.ToString("O") +
            "\",\"payload\":{\"commandId\":\"" + Guid.NewGuid() + "\",\"status\":\"failed\",\"diagnosticCode\":" + diagnosticJson + "}}";
        await socket.SendAsync(System.Text.Encoding.UTF8.GetBytes(text), System.Net.WebSockets.WebSocketMessageType.Text, true, default);
        var closed = await Read(socket); Assert.Equal(System.Net.WebSockets.WebSocketMessageType.Close, closed.Result.MessageType);
        Assert.Equal(1008, (int)closed.Result.CloseStatus!);
    }

    [Fact] public async Task DisconnectCancelsCommandAndExpiredIsNotSent()
    {
        await using var f=await Host();var (node,secret)=await f.Pair();using var socket=await f.Connect(secret);await Advertise(socket,new NodeCapability("notification.show",1));
        var c=new NodeNotificationCommand(Guid.NewGuid(),"notification.show",1,DateTimeOffset.UtcNow.AddSeconds(60),new("Aegis","fixture"));
        Assert.Equal("expired",(await f.Connections.SendAsync(node.Id,c with {ExpiresAt=DateTimeOffset.UtcNow.AddSeconds(-1)},default)).Status);
        var pending=f.Connections.SendAsync(node.Id,c,default);await Read(socket);f.Connections.Disconnect(node.Id,"node_disabled");Assert.Equal("unavailable",(await pending).Status);
    }

    [Fact] public async Task CommandHasBoundedTimeoutWithoutAffectingHealthyLease()
    {
        await using var f=await Host();var (node,secret)=await f.Pair();using var socket=await f.Connect(secret);await Advertise(socket,new NodeCapability("notification.show",1));
        var c=new NodeNotificationCommand(Guid.NewGuid(),"notification.show",1,DateTimeOffset.UtcNow.AddSeconds(60),new("Aegis","fixture"));
        var pending=f.Connections.SendAsync(node.Id,c,default);await Read(socket);
        Assert.Equal("timeout",(await pending.WaitAsync(TimeSpan.FromSeconds(10))).Status);Assert.True(f.Connections.IsOnline(node.Id));
    }
    [Fact] public async Task UnsolicitedResultsAreBoundedAndCannotCreateHeartbeatPresence()
    {
        await using var f=await Host();var (node,secret)=await f.Pair();using var socket=await f.Connect(secret);await Advertise(socket,new NodeCapability("notification.show",1));
        for(var i=0;i<17;i++) await Send(socket,new {protocolVersion=1,type="command_result",messageId=Guid.NewGuid(),sentAt=DateTimeOffset.UtcNow,payload=new {commandId=Guid.NewGuid(),status="success"}});
        var close=await Read(socket);Assert.Equal(WebSocketMessageType.Close,close.Result.MessageType);Assert.False(f.Connections.IsOnline(node.Id));
    }

}
