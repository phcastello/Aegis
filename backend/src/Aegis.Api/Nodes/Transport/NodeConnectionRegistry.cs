using Aegis.Application.Nodes;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;
namespace Aegis.Api.Nodes.Transport;

public sealed class NodeConnectionRegistry : INodeConnections, IDisposable
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
    public sealed class Lease(Guid nodeId, DateTimeOffset now)
    {
        public Guid NodeId { get; } = nodeId;
        public Guid ConnectionId { get; } = Guid.NewGuid();
        public bool Ready { get; internal set; }
        public DateTimeOffset LastSeenAt { get; internal set; } = now;
        public DateTimeOffset? LastHeartbeatAt { get; internal set; }
        public CancellationTokenSource Ended { get; } = new();
        public string Reason { get; internal set; } = "disconnected";
    }
    private bool IsFresh(Lease lease) => lease.Ready && !IsExpired(lease);
    private bool IsExpired(Lease lease) => lease.Ended.IsCancellationRequested ||
        clock.GetUtcNow() - lease.LastSeenAt >= TimeSpan.FromSeconds(options.TimeoutSeconds);
    public bool IsOnline(Guid id) { lock (gate) return current.TryGetValue(id, out var lease) && IsFresh(lease); }
    public Lease? Lookup(Guid id) { lock (gate) return current.GetValueOrDefault(id); }
    public Lease Register(Guid id, bool ready = true)
    {
        lock (gate)
        {
            if (!current.ContainsKey(id) && current.Count >= options.MaxConnections)
                throw new NodeException("node_transport_capacity", "Transport temporariamente cheio.", 503);
            if (current.Remove(id, out var old)) End(old, "replaced");
            var lease = new Lease(id, clock.GetUtcNow()) { Ready = ready }; current[id] = lease;
            connections.Add(1); logger.LogInformation("Node transport {Event} NodeId={NodeId} ConnectionId={ConnectionId}", "connected", id, lease.ConnectionId);
            return lease;
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
