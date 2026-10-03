namespace Aegis.Application.Nodes;

// Presence is process-local. Administrative state remains authoritative in the database.
public interface INodeConnections
{
    bool IsOnline(Guid nodeId);
    void Disconnect(Guid nodeId, string reason);
}
public interface INodeTransportHistory
{
    Task ConnectedAsync(Guid nodeId, string appVersion, DateTimeOffset seenAt, CancellationToken ct);
    Task SeenAsync(Guid nodeId, DateTimeOffset seenAt, DateTimeOffset? heartbeatAt, CancellationToken ct);
}
