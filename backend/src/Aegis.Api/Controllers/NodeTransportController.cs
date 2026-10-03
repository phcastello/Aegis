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
        if (!HttpContext.WebSockets.IsWebSocketRequest) { Response.StatusCode = 400; await Response.WriteAsJsonAsync(new { code = "websocket_required" }, ct); return; }
        if (Request.QueryString.HasValue) { Response.StatusCode = 400; await Response.WriteAsJsonAsync(new { code = "query_not_allowed" }, ct); return; }
        if (Request.Headers["X-Aegis-Node-Protocol"] != "1") { Response.StatusCode = 409; Response.Headers["X-Aegis-Node-Error"] = "protocol_mismatch"; await Response.WriteAsJsonAsync(new { code = "protocol_mismatch" }, ct); return; }
        var capacity = HttpContext.RequestServices.GetRequiredService<NodeHandshakeCapacity>();
        if (!await capacity.Slots.WaitAsync(0, ct)) { Response.StatusCode = 503; return; }
        WebSocket accepted;
        try { accepted = await HttpContext.WebSockets.AcceptWebSocketAsync(); }
        catch { capacity.Slots.Release(); throw; }
        using var socket = accepted;
        NodeConnectionRegistry.Lease? lease = null; var reason = "disconnected";
        var nodeId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var options = configured.Value;
        try
        {
            using var helloDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(options.HelloTimeoutSeconds), clock);
            using var helloToken = CancellationTokenSource.CreateLinkedTokenSource(ct, helloDeadline.Token);
            var hello = await NodeProtocol.ReceiveAsync(socket, helloToken.Token);
            if (hello?.Type != "hello") throw new NodeProtocolException("hello_required");
            var version = hello.Payload!.Value.GetProperty("appVersion").GetString()!;
            // Register before a fresh administrative check: disable/revoke either cancels this
            // lease after commit, or this recheck observes the committed change. No admission gap.
            lease = connections.Register(nodeId, ready: false);
            using var session = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.Ended.Token);
            await WithHistory(h => h.ConnectedAsync(nodeId, version, lease.LastSeenAt, session.Token));
            using (var scope = scopes.CreateScope()) await scope.ServiceProvider.GetRequiredService<INodeRegistry>().MeAsync(nodeId, session.Token);
            if (!connections.Activate(lease)) throw new OperationCanceledException();
            await NodeProtocol.SendAsync(socket, "hello_ack", hello.MessageId, clock.GetUtcNow(),
                new { heartbeatSeconds = options.HeartbeatSeconds, timeoutSeconds = options.TimeoutSeconds }, session.Token);
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
            }
        }
        catch (NodeException e) { reason = e.Code; }
        catch (ArgumentException) { reason = "invalid_hello"; }
        catch (NodeProtocolException e) { reason = e.Message; }
        catch (OperationCanceledException) { reason = lease?.Ended.IsCancellationRequested == true ? lease.Reason : "heartbeat_timeout"; }
        catch (WebSocketException) { reason = "network_disconnected"; }
        catch (Exception e) { reason = "transport_failed"; logger.LogWarning("Node transport failed NodeId={NodeId} ErrorType={ErrorType}", nodeId, e.GetType().Name); }
        finally
        {
            if (lease is not null)
            {
                if (lease.Ended.IsCancellationRequested) reason = lease.Reason;
                connections.Unregister(lease, reason);
            }
            logger.LogInformation("Node transport ended NodeId={NodeId} Reason={Reason}", nodeId, reason);
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
