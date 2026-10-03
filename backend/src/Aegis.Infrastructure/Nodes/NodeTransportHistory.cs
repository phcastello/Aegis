using Aegis.Application.Nodes;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace Aegis.Infrastructure.Nodes;

public sealed class NodeTransportHistory(AegisDbContext db) : INodeTransportHistory
{
    public async Task ConnectedAsync(Guid id, string version, DateTimeOffset seenAt, CancellationToken ct)
    {
        var node = await db.Nodes.AsNoTracking().SingleAsync(n => n.Id == id, ct);
        if (node.RevokedAt is not null) throw new NodeException("node_revoked", "Identidade revogada.", 401);
        if (!node.Enabled) throw new NodeException("node_disabled", "Node desativado.", 403);
        AegisNode.Validate(node.Name, node.Platform, version, node.ProtocolVersion);
        if (db.Database.IsRelational())
            await db.Nodes.Where(n => n.Id == id && n.Enabled && n.RevokedAt == null).ExecuteUpdateAsync(s => s
                .SetProperty(n => n.AppVersion, version), ct);
        else { var tracked = await db.Nodes.SingleAsync(n => n.Id == id, ct); tracked.TransportSeen(seenAt, appVersion: version); await db.SaveChangesAsync(ct); }
        await SeenAsync(id, seenAt, null, ct);
    }
    public async Task SeenAsync(Guid id, DateTimeOffset seenAt, DateTimeOffset? heartbeatAt, CancellationToken ct)
    {
        // Update only historical columns, with monotonic predicates. Old lease cleanup cannot
        // overwrite a replacement's newer timestamps or resurrect Enabled/Revoked metadata.
        if (db.Database.IsRelational())
        {
            await db.Nodes.Where(n => n.Id == id && (n.LastSeenAt == null || n.LastSeenAt < seenAt))
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.LastSeenAt, seenAt), ct);
            if (heartbeatAt is { } heartbeat)
                await db.Nodes.Where(n => n.Id == id && (n.LastHeartbeatAt == null || n.LastHeartbeatAt < heartbeat))
                    .ExecuteUpdateAsync(s => s.SetProperty(n => n.LastHeartbeatAt, heartbeat), ct);
        }
        else
        {
            var node = await db.Nodes.SingleAsync(n => n.Id == id, ct);
            node.TransportSeen(seenAt, heartbeatAt); await db.SaveChangesAsync(ct);
        }
    }
}
