// Copyright (C) 2026 Pedro Hasson Castello.
// GNU AGPL v3 only; attribution terms: /ADDITIONAL_TERMS.md.
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
