namespace Aegis.Domain.Entities;
// Last announced metadata; presence and eligibility belong to the active connection.
public sealed class NodeCapabilitySnapshot
{
    private NodeCapabilitySnapshot() { }
    public NodeCapabilitySnapshot(Guid nodeId, string name, int version) { NodeId = nodeId; Name = name; Version = version; }
    public Guid NodeId { get; private set; }
    public string Name { get; private set; } = "";
    public int Version { get; private set; }
}
