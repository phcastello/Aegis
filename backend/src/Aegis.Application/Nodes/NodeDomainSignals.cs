namespace Aegis.Application.Nodes;

public sealed record NodeDomainCommand(Guid CommandId, string Capability, int CapabilityVersion, DateTimeOffset ExpiresAt, object Input);
public interface INodeDomainSignals
{
    Task<string> SendAsync(Guid nodeId, string capability, object input, CancellationToken ct = default);
}
public interface INodeLiveDomainSignals
{
    Task<string> SendDomainAsync(Guid nodeId, NodeDomainCommand command, CancellationToken ct);
}
public interface INodePushDomainSignals
{
    Task<string> SendDomainAsync(Guid nodeId, NodeDomainCommand command, CancellationToken ct);
}
public sealed class NodeDomainSignals(INodeRegistry nodes, INodeConnections live, INodeLiveDomainSignals socket,
    INodePushDomainSignals push, INodeBackgroundAvailability background, TimeProvider clock) : INodeDomainSignals
{
    public async Task<string> SendAsync(Guid nodeId, string capability, object input, CancellationToken ct = default)
    {
        if (capability is not ("geofence.monitor" or "location.current")) throw new NodeException("invalid_domain_command", "Comando de domínio inválido.");
        var node = (await nodes.ListForDeliveryAsync(ct)).SingleOrDefault(n => n.Id == nodeId);
        if (node is null || !node.Enabled || node.RevokedAt is not null || !node.Capabilities.Any(c => c.Name == capability && c.Version >= 1)) return "unavailable";
        var command = new NodeDomainCommand(Guid.NewGuid(), capability, 1, clock.GetUtcNow().AddMinutes(5), input);
        if (live.LiveCapabilities(nodeId)?.Any(c => c.Name == capability && c.Version >= 1) == true) return await socket.SendDomainAsync(nodeId, command, ct);
        if (await background.HasRouteAsync(nodeId, ct)) return await push.SendDomainAsync(nodeId, command, ct);
        return "unavailable";
    }
}
