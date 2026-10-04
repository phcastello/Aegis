using Aegis.Application.Nodes;
using System.Diagnostics.Metrics;
using System.Net.WebSockets;
using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
namespace Aegis.Api.Nodes.Transport;

public sealed class NodeConnectionRegistry : INodeConnections, INodeLiveNotifications, IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, Lease> current = [];
    private readonly TimeProvider clock;
    private readonly NodeTransportOptions options;
    private readonly ILogger<NodeConnectionRegistry> logger;
    private readonly Meter meter = new("Aegis.NodeTransport", "1");
    private readonly Counter<long> connections, disconnects, heartbeats;
    public NodeConnectionRegistry(TimeProvider clock, IOptions<NodeTransportOptions> options, ILogger<NodeConnectionRegistry> logger)
    {
        this.clock = clock; this.options = options.Value; this.logger = logger;
        connections = meter.CreateCounter<long>("node_transport_connections_total");
        disconnects = meter.CreateCounter<long>("node_transport_disconnects_total");
        heartbeats = meter.CreateCounter<long>("node_transport_heartbeats_total");
        meter.CreateObservableGauge("node_transport_active_connections", () => { lock (gate) return current.Values.Count(IsFresh); });
    }
    public sealed class Lease(Guid nodeId, DateTimeOffset now, IReadOnlyList<NodeCapability> capabilities, SemaphoreSlim admission)
    {
        public IReadOnlyList<NodeCapability> Capabilities { get; } = Array.AsReadOnly(capabilities.ToArray());
        internal SemaphoreSlim Admission { get; } = admission;
        public Guid NodeId { get; } = nodeId;
        public Guid ConnectionId { get; } = Guid.NewGuid();
        public WebSocket? Socket { get; internal set; }
        internal SemaphoreSlim Writer { get; } = new(1);
        internal ConcurrentDictionary<Guid, TaskCompletionSource<NodeLiveNotificationResult>> Pending { get; } = new();
        internal DateTimeOffset ConnectedAt { get; } = now;
        internal DateTimeOffset UnknownResultWindow { get; set; } = now;
        internal int UnknownResults { get; set; }
        public bool Ready { get; internal set; }
        public DateTimeOffset LastSeenAt { get; internal set; } = now;
        public DateTimeOffset? LastHeartbeatAt { get; internal set; }
        public CancellationTokenSource Ended { get; } = new();
        public string Reason { get; internal set; } = "disconnected";
    }
    private bool IsFresh(Lease lease) => lease.Ready && !IsExpired(lease);
    private bool IsExpired(Lease lease) => lease.Ended.IsCancellationRequested ||
        clock.GetUtcNow() - (lease.LastHeartbeatAt ?? lease.ConnectedAt) >= TimeSpan.FromSeconds(options.TimeoutSeconds);
    public bool IsOnline(Guid id) { lock (gate) return current.TryGetValue(id, out var lease) && IsFresh(lease); }
    public Lease? Lookup(Guid id) { lock (gate) return current.GetValueOrDefault(id); }
    public IReadOnlyList<NodeCapability>? LiveCapabilities(Guid id) { lock (gate) return current.TryGetValue(id, out var lease) && IsFresh(lease) ? lease.Capabilities : null; }
    public Lease Register(Guid id, bool ready = true, IReadOnlyList<NodeCapability>? capabilities = null)
    {
        lock (gate)
        {
            if (!current.ContainsKey(id) && current.Count >= options.MaxConnections)
                throw new NodeException("node_transport_capacity", "Transport temporariamente cheio.", 503);
            if (current.Remove(id, out var old)) End(old, "replaced");
            var lease = new Lease(id, clock.GetUtcNow(), capabilities ?? Array.Empty<NodeCapability>(), old?.Admission ?? new SemaphoreSlim(1)) { Ready = ready }; current[id] = lease;
            connections.Add(1); logger.LogInformation("Node transport {Event} NodeId={NodeId} ConnectionId={ConnectionId}", "connected", id, lease.ConnectionId);
            return lease;
        }
    }
    // Per replacement chain, only hello metadata persistence is serialized. No gate is
    // held during heartbeat/resolution; an old hello cannot overwrite B's final snapshot.
    public async Task AnnounceAsync(Lease lease, Func<Task> announce, CancellationToken ct)
    {
        await lease.Admission.WaitAsync(ct);
        try { if (Lookup(lease.NodeId) != lease || lease.Ended.IsCancellationRequested) throw new OperationCanceledException(ct); await announce(); }
        finally { lease.Admission.Release(); }
    }
    public async Task WriteAsync(Lease lease, string type, Guid messageId, object? payload, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.Ended.Token); deadline.CancelAfter(TimeSpan.FromSeconds(2));
        await lease.Writer.WaitAsync(deadline.Token);
        try { if (Lookup(lease.NodeId) != lease || lease.Socket is null) throw new OperationCanceledException();
            await NodeProtocol.SendAsync(lease.Socket, type, messageId, clock.GetUtcNow(), payload, deadline.Token); }
        finally { lease.Writer.Release(); }
    }
    public async Task<NodeLiveNotificationResult> SendAsync(Guid id, NodeNotificationCommand command, CancellationToken ct)
    {
        Lease? lease;
        var result = new TaskCompletionSource<NodeLiveNotificationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate) { lease = current.GetValueOrDefault(id); if (lease is null || !IsFresh(lease) || lease.Socket is null ||
            !lease.Capabilities.Any(c => c.Name == NotificationContract.Capability && c.Version >= 1)) return new("unavailable");
            if (lease.Pending.Count >= 16) return new("busy");
            if (!lease.Pending.TryAdd(command.CommandId, result)) return new("duplicate"); }
        if (command.ExpiresAt <= clock.GetUtcNow()) { lease.Pending.TryRemove(command.CommandId, out _); return new("expired"); }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.Ended.Token); deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try { await WriteAsync(lease, "command", Guid.NewGuid(), command, deadline.Token); return await result.Task.WaitAsync(deadline.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(lease.Ended.IsCancellationRequested ? "unavailable" : "timeout"); }
        catch (WebSocketException) { return new("unavailable"); }
        finally { lease.Pending.TryRemove(command.CommandId, out _); }
    }
    public void Result(Lease lease, Guid commandId, string status, string? diagnosticCode = null)
    {
        if (!NotificationContract.Results.Contains(status) || diagnosticCode is not null && !NotificationContract.ValidDiagnostic(diagnosticCode))
            throw new NodeProtocolException("invalid_command_result");
        lock (gate) {
            if (current.GetValueOrDefault(lease.NodeId) != lease || !IsFresh(lease)) return;
            if (lease.Pending.TryGetValue(commandId, out var pending) && pending.TrySetResult(new(status, diagnosticCode))) {
                lease.LastSeenAt = clock.GetUtcNow();
                logger.LogInformation("Node command result NodeId={NodeId} ConnectionId={ConnectionId} CommandId={CommandId} Status={Status}", lease.NodeId, lease.ConnectionId, commandId, status);
                return;
            }
            // Late results are harmless; an unbounded stream of unsolicited/duplicate results is not.
            if (clock.GetUtcNow() - lease.UnknownResultWindow >= TimeSpan.FromMinutes(1)) { lease.UnknownResultWindow = clock.GetUtcNow(); lease.UnknownResults = 0; }
            if (++lease.UnknownResults > 16) { current.Remove(lease.NodeId); End(lease, "command_result_abuse"); }
        }
    }
    public bool Activate(Lease lease)
    {
        lock (gate)
        {
            if (current.GetValueOrDefault(lease.NodeId) != lease || IsExpired(lease)) return false;
            lease.Ready = true; return true;
        }
    }
    public bool Heartbeat(Lease lease)
    {
        lock (gate)
        {
            if (current.GetValueOrDefault(lease.NodeId) != lease || !IsFresh(lease)) return false;
            var now = clock.GetUtcNow();
            if (lease.LastHeartbeatAt is { } last && now - last < TimeSpan.FromSeconds(options.MinimumHeartbeatSeconds))
            { current.Remove(lease.NodeId); End(lease, "heartbeat_abuse"); return false; }
            lease.LastHeartbeatAt = lease.LastSeenAt = now; heartbeats.Add(1); return true;
        }
    }
    public (DateTimeOffset SeenAt, DateTimeOffset? HeartbeatAt) History(Lease lease)
    { lock (gate) return (lease.LastSeenAt, lease.LastHeartbeatAt); }
    public bool Unregister(Lease lease, string reason = "disconnected")
    {
        lock (gate)
        {
            if (current.GetValueOrDefault(lease.NodeId) != lease) return false;
            current.Remove(lease.NodeId); End(lease, reason); return true;
        }
    }
    public void Disconnect(Guid id, string reason) { lock (gate) if (current.Remove(id, out var lease)) End(lease, reason); }
    public void ExpireStale()
    {
        lock (gate) foreach (var lease in current.Values.Where(IsExpired).ToArray())
        { current.Remove(lease.NodeId); End(lease, "heartbeat_timeout"); }
    }
    public void DisconnectAll() { lock (gate) { foreach (var lease in current.Values) End(lease, "server_shutdown"); current.Clear(); } }
    private void End(Lease lease, string reason)
    {
        lease.Reason = reason; lease.Ended.Cancel(); disconnects.Add(1);
        logger.LogInformation("Node transport disconnected NodeId={NodeId} ConnectionId={ConnectionId} Reason={Reason}", lease.NodeId, lease.ConnectionId, reason);
    }
    public void Dispose() { DisconnectAll(); meter.Dispose(); }
}
public sealed class NodeConnectionWatchdog(NodeConnectionRegistry registry, TimeProvider clock, IOptions<NodeTransportOptions> options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.WatchdogSeconds), clock);
            while (await timer.WaitForNextTickAsync(stoppingToken)) registry.ExpireStale(); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { registry.DisconnectAll(); }
    }
}
