using Aegis.Domain.Entities;
namespace Aegis.Application.Nodes;

public sealed record NodeView(Guid Id, string Name, string Platform, bool Enabled, string AppVersion, int ProtocolVersion,
    DateTimeOffset PairedAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? RevokedAt, DateTimeOffset? LastSeenAt = null, DateTimeOffset? LastHeartbeatAt = null, string Availability = "offline")
{
    public int TargetPriority { get; init; }
    public IReadOnlyList<NodeCapability> Capabilities { get; init; } = Array.Empty<NodeCapability>();
    public static NodeView From(AegisNode node) => new(node.Id, node.Name, node.Platform.ToString().ToLowerInvariant(), node.Enabled,
        node.AppVersion, node.ProtocolVersion, node.PairedAt, node.CreatedAt, node.UpdatedAt, node.RevokedAt, node.LastSeenAt, node.LastHeartbeatAt) { TargetPriority = node.TargetPriority };
}
public sealed record PairNodeRequest(Guid AttemptId, string Code, string RecoveryKey, string Name, string Platform, string AppVersion, int ProtocolVersion);
public sealed record PairNodeReceipt(Guid NodeId, string Credential, DateTimeOffset ExpiresAt);
public sealed record FinalizeNodeRequest(Guid AttemptId, string Credential);
public sealed record PairingCodeView(string Code, DateTimeOffset ExpiresAt);
public sealed record NodeAuthentication(NodeView? Node, string? Error)
{
    public bool Authenticated => Node is not null && Error is null;
}
public sealed class NodeException(string code, string message, int status = 400) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}
public interface INodeRegistry
{
    Task<PairingCodeView> CreateCodeAsync(Guid? issuer, CancellationToken ct = default);
    Task<PairNodeReceipt> PairAsync(PairNodeRequest request, CancellationToken ct = default);
    Task<NodeView> FinalizeAsync(FinalizeNodeRequest request, CancellationToken ct = default);
    Task<NodeAuthentication> AuthenticateAsync(string credential, CancellationToken ct = default);
    Task<IReadOnlyList<NodeView>> ListAsync(Guid actor, CancellationToken ct = default);
    Task<NodeView> MeAsync(Guid actor, CancellationToken ct = default);
    Task<NodeView> RenameAsync(Guid actor, Guid target, string name, CancellationToken ct = default);
    Task<NodeView> SetEnabledAsync(Guid? actor, Guid target, bool enabled, CancellationToken ct = default);
    Task<NodeView> RevokeAsync(Guid actor, Guid target, CancellationToken ct = default);
    Task<NodeView> SetTargetPriorityAsync(Guid actor, Guid target, int priority, CancellationToken ct = default);
    Task CleanupAsync(CancellationToken ct = default);
}
