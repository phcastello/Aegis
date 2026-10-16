namespace Aegis.Application.Nodes;

// Presence is process-local. Administrative state remains authoritative in the database.
public interface INodeConnections
{
    IReadOnlyList<NodeCapability>? LiveCapabilities(Guid id);
    bool IsOnline(Guid nodeId);
    void Disconnect(Guid nodeId, string reason);
}
public interface INodeTransportHistory
{
    Task ConnectedAsync(Guid nodeId, string appVersion, DateTimeOffset seenAt, CancellationToken ct);
    Task AnnouncedAsync(Guid id, string version, DateTimeOffset seenAt, IReadOnlyList<NodeCapability> capabilities, CancellationToken ct);
    Task SeenAsync(Guid nodeId, DateTimeOffset seenAt, DateTimeOffset? heartbeatAt, CancellationToken ct);
}
