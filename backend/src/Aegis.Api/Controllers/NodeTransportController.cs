using System.Net.WebSockets;
using System.Security.Claims;
using Aegis.Api.Nodes;
using Aegis.Api.Nodes.Transport;
using Aegis.Application.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
namespace Aegis.Api.Controllers;

[ApiController, Route("api/nodes/connect"), Authorize(Policy = "NodeManagement"), ServiceFilter(typeof(NodeApiFilter)), EnableRateLimiting("node-connect")]
public sealed class NodeTransportController(NodeConnectionRegistry connections, IServiceScopeFactory scopes,
    TimeProvider clock, IOptions<NodeTransportOptions> configured, ILogger<NodeTransportController> logger) : ControllerBase
{
    // Bounds upgraded sockets still waiting for hello, independently of registered presence.
    [HttpGet]
    public async Task Connect(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var nodeId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        logger.LogInformation("Node transport handshake NodeId={NodeId} Phase=authenticated Upgrade={Upgrade}", nodeId, HttpContext.WebSockets.IsWebSocketRequest);
        if (!HttpContext.WebSockets.IsWebSocketRequest) { Response.StatusCode = 400; Response.Headers["X-Aegis-Node-Error"] = "websocket_required"; logger.LogWarning("Node transport rejected NodeId={NodeId} Phase=upgrade HttpStatus=400 Reason=websocket_required", nodeId); await Response.WriteAsJsonAsync(new { code = "websocket_required" }, ct); return; }
        if (Request.QueryString.HasValue) { Response.StatusCode = 400; await Response.WriteAsJsonAsync(new { code = "query_not_allowed" }, ct); return; }
        if (Request.Headers["X-Aegis-Node-Protocol"] != "1") { Response.StatusCode = 409; Response.Headers["X-Aegis-Node-Error"] = "protocol_mismatch"; await Response.WriteAsJsonAsync(new { code = "protocol_mismatch" }, ct); return; }
        var capacity = HttpContext.RequestServices.GetRequiredService<NodeHandshakeCapacity>();
        if (!await capacity.Slots.WaitAsync(0, ct)) { Response.StatusCode = 503; return; }
        WebSocket accepted;
        try { accepted = await HttpContext.WebSockets.AcceptWebSocketAsync(); }
        catch { capacity.Slots.Release(); throw; }
        using var socket = accepted;
        NodeConnectionRegistry.Lease? lease = null; var reason = "disconnected";
        logger.LogInformation("Node transport upgraded NodeId={NodeId} Phase=upgrade HttpStatus=101", nodeId);
        var options = configured.Value;
        try
        {
            using var helloDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(options.HelloTimeoutSeconds), clock);
            using var helloToken = CancellationTokenSource.CreateLinkedTokenSource(ct, helloDeadline.Token);
            var hello = await NodeProtocol.ReceiveAsync(socket, helloToken.Token);
            if (hello?.Type != "hello") throw new NodeProtocolException("hello_required");
            logger.LogInformation("Node transport hello NodeId={NodeId} Phase=hello", nodeId);
            var version = hello.Payload!.Value.GetProperty("appVersion").GetString()!;
            var capabilities = NodeProtocol.Capabilities(hello.Payload.Value, out var unknown);
            if (unknown > 0) logger.LogInformation("Node capabilities ignored NodeId={NodeId} UnknownCount={UnknownCount}", nodeId, unknown);
            // Register before a fresh administrative check: disable/revoke either cancels this
            // lease after commit, or this recheck observes the committed change. No admission gap.
            lease = connections.Register(nodeId, ready: false, capabilities: capabilities);
            using var session = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.Ended.Token);
            await connections.AnnounceAsync(lease, () => WithHistory(h => h.AnnouncedAsync(nodeId, version, lease.LastSeenAt, capabilities, session.Token)), session.Token);
            using (var scope = scopes.CreateScope()) await scope.ServiceProvider.GetRequiredService<INodeRegistry>().MeAsync(nodeId, session.Token);
            if (!connections.Activate(lease)) throw new OperationCanceledException();
            await NodeProtocol.SendAsync(socket, "hello_ack", hello.MessageId, clock.GetUtcNow(),
                new { heartbeatSeconds = options.HeartbeatSeconds, timeoutSeconds = options.TimeoutSeconds }, session.Token);
            logger.LogInformation("Node transport acknowledged NodeId={NodeId} ConnectionId={ConnectionId} Phase=hello_ack", nodeId, lease.ConnectionId);
            var lastPersisted = clock.GetUtcNow();
            while (!session.IsCancellationRequested)
            {
                var message = await NodeProtocol.ReceiveAsync(socket, ct).WaitAsync(session.Token);
                if (message is null) break;
                if (message.Type != "heartbeat") throw new NodeProtocolException("unexpected_hello");
                if (!connections.Heartbeat(lease)) break;
                if (clock.GetUtcNow() - lastPersisted >= TimeSpan.FromSeconds(options.PersistSeconds))
                {
                    var history = connections.History(lease);
                    await WithHistory(h => h.SeenAsync(nodeId, history.SeenAt, history.HeartbeatAt, session.Token));
                    lastPersisted = clock.GetUtcNow();
                }
                await NodeProtocol.SendAsync(socket, "heartbeat_ack", message.MessageId, clock.GetUtcNow(), null, session.Token);
                logger.LogDebug("Node transport heartbeat NodeId={NodeId} ConnectionId={ConnectionId} Phase=heartbeat_ack", nodeId, lease.ConnectionId);
            }
        }
        catch (NodeException e) { reason = e.Code; }
        catch (ArgumentException) { reason = "invalid_hello"; }
        catch (NodeProtocolException e) { reason = e.Message; }
        catch (OperationCanceledException) { reason = lease?.Ended.IsCancellationRequested == true ? lease.Reason : lease is null ? "hello_timeout" : "heartbeat_timeout"; }
        catch (WebSocketException) { reason = "network_disconnected"; }
        catch (Exception e) { reason = "transport_failed"; logger.LogWarning("Node transport failed NodeId={NodeId} ErrorType={ErrorType}", nodeId, e.GetType().Name); }
        finally
        {
            if (lease is not null)
            {
                if (lease.Ended.IsCancellationRequested) reason = lease.Reason;
                connections.Unregister(lease, reason);
            }
            logger.LogInformation("Node transport ended NodeId={NodeId} ConnectionId={ConnectionId} Reason={Reason} CloseCode={CloseCode}", nodeId, lease?.ConnectionId, reason, NodeProtocol.CloseCode(reason));
            try { using var closeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2), clock);
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await socket.CloseOutputAsync((WebSocketCloseStatus)NodeProtocol.CloseCode(reason), reason, closeDeadline.Token);
            } catch (Exception e) when (e is OperationCanceledException or WebSocketException) { socket.Abort(); }
            if (lease is not null)
            {
                try {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), clock);
                    var history = connections.History(lease);
                    await WithHistory(h => h.SeenAsync(nodeId, history.SeenAt, history.HeartbeatAt, deadline.Token));
                } catch (Exception e) { logger.LogWarning("Node history flush failed NodeId={NodeId} ErrorType={ErrorType}", nodeId, e.GetType().Name); }
                lease.Ended.Dispose();
            }
            capacity.Slots.Release();
        }
    }
    private async Task WithHistory(Func<INodeTransportHistory, Task> operation)
    { using var scope = scopes.CreateScope(); await operation(scope.ServiceProvider.GetRequiredService<INodeTransportHistory>()); }
}
public sealed class NodeHandshakeCapacity(IOptions<NodeTransportOptions> options) : IDisposable
{
    public SemaphoreSlim Slots { get; } = new(options.Value.MaxConnections * 2);
    public System.Threading.RateLimiting.FixedWindowRateLimiter Attempts { get; } = new(new() {
        PermitLimit = 200, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true });
    public void Dispose() { Slots.Dispose(); Attempts.Dispose(); }
}
