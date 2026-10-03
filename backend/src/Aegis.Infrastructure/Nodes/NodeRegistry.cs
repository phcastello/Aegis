using Aegis.Application.Nodes;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace Aegis.Infrastructure.Nodes;

public sealed class NodeRegistry(AegisDbContext db, TimeProvider clock) : INodeRegistry
{
    // The small administrative registry follows the existing PostgreSQL advisory-lock pattern.
    // No transport/presence/background workload shares this lock.
    private async Task<T> Locked<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        if (db.Database.IsNpgsql()) await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7070003)", ct);
        db.ChangeTracker.Clear(); // Each registry command is its own unit of work; no cached identity state.
        var result = await action();
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return result;
    }
    // PostgreSQL timestamps have microsecond precision; keep replayed establishment DTOs stable.
    private DateTimeOffset Now { get { var now = clock.GetUtcNow(); return new(now.Ticks - now.Ticks % 10, now.Offset); } }
    private async Task<AegisNode> Active(Guid id, CancellationToken ct)
    {
        var node = await db.Nodes.SingleOrDefaultAsync(n => n.Id == id, ct);
        if (node is null) throw new NodeException("node_authentication_required", "Identidade de Node não reconhecida.", 401);
        if (node.RevokedAt is not null) throw new NodeException("node_revoked", "A identidade foi revogada. Pareie este dispositivo novamente.", 401);
        if (!node.Enabled) throw new NodeException("node_disabled", "Este Node está desativado. Outro Node ativo ou o administrador local pode reativá-lo.", 403);
        return node;
    }
    private async Task<AegisNode> Target(Guid id, CancellationToken ct)
    {
        var target = await db.Nodes.SingleOrDefaultAsync(n => n.Id == id, ct)
            ?? throw new NodeException("node_not_found", "Node não encontrado.", 404);
        if (target.RevokedAt is not null) throw new NodeException("node_revoked", "A identidade revogada não pode ser alterada ou reativada.", 409);
        return target;
    }
    private async Task Prune(CancellationToken ct)
    {
        // Runs on administrative/pairing activity or explicit local cleanup, never as a heartbeat.
        var now = Now;
        db.NodePairingAttempts.RemoveRange(await db.NodePairingAttempts.Where(a => a.ExpiresAt <= now).ToListAsync(ct));
        db.NodePairingCodes.RemoveRange(await db.NodePairingCodes.Where(c => c.ExpiresAt <= now).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
    }
    public Task CleanupAsync(CancellationToken ct = default) => Locked(async () => { await Prune(ct); return true; }, ct);
    public Task<PairingCodeView> CreateCodeAsync(Guid? issuer, CancellationToken ct = default) => Locked(async () =>
    {
        if (issuer is { } id) await Active(id, ct);
        await Prune(ct);
        if (await db.NodePairingCodes.CountAsync(c => c.ConsumedAt == null && c.IssuerNodeId == issuer, ct) >= 10)
            throw new NodeException("pairing_limit", "Use ou aguarde expirar os códigos já emitidos.", 429);
        var code = NodeSecrets.NewCode(); var entity = new NodePairingCode(code[..8], NodeSecrets.Hash(code), issuer, Now);
        db.NodePairingCodes.Add(entity); return new PairingCodeView(NodeSecrets.DisplayCode(code), entity.ExpiresAt);
    }, ct);
    public async Task<PairNodeReceipt> PairAsync(PairNodeRequest request, CancellationToken ct = default)
    {
        var receipt = await Locked(async () =>
    {
        if (request.AttemptId == Guid.Empty) throw NodeSecrets.PairingError();
        var code = NodeSecrets.NormalizeCode(request.Code); var recovery = NodeSecrets.RecoveryKey(request.RecoveryKey);
        var platform = request.Platform switch { "android" => NodePlatform.Android, "windows" => NodePlatform.Windows, _ => (NodePlatform)(-1) };
        try { AegisNode.Validate(request.Name, platform, request.AppVersion, request.ProtocolVersion); }
        catch (ArgumentException) { throw new NodeException("invalid_node", "Informe nome, plataforma e versão/protocolo de Node válidos."); }
        await Prune(ct);
        var entry = await db.NodePairingCodes.SingleOrDefaultAsync(c => c.Selector == code.Substring(0, 8), ct);
        if (entry is null) throw NodeSecrets.PairingError();
        if (entry.IssuerNodeId is { } issuer)
        {
            var source = await db.Nodes.SingleOrDefaultAsync(n => n.Id == issuer, ct);
            if (source is null || !source.Enabled || source.RevokedAt is not null) throw NodeSecrets.PairingError();
        }
        var retry = await db.NodePairingAttempts.SingleOrDefaultAsync(a => a.Id == request.AttemptId, ct);
        if (retry is not null)
        {
            if (retry.PairingCodeId != entry.Id || retry.ExpiresAt <= Now || !NodeSecrets.Matches(entry.CodeHash, code) ||
                !NodeSecrets.Matches(retry.RecoveryHash, request.RecoveryKey) || retry.Name != request.Name.Trim() ||
                retry.Platform != platform || retry.AppVersion != request.AppVersion) throw NodeSecrets.PairingError();
            return new PairNodeReceipt(retry.NodeId, NodeSecrets.DecryptReceipt(retry.EncryptedReceipt, recovery, retry.Id), retry.ExpiresAt);
        }
        if (!entry.IsUsable(Now)) throw NodeSecrets.PairingError();
        if (!NodeSecrets.Matches(entry.CodeHash, code))
        {
            entry.Fail(Now);
            // Commit failed-attempt budget before returning a generic failure.
            await db.SaveChangesAsync(ct);
            return new PairNodeReceipt(Guid.Empty, "", DateTimeOffset.MinValue);
        }
        var nodeId = Guid.NewGuid(); var credential = NodeSecrets.NewCredential(nodeId);
        var attempt = new NodePairingAttempt(request.AttemptId, nodeId, entry, request.Name, platform, request.AppVersion,
            NodeSecrets.Hash(request.RecoveryKey), NodeSecrets.Hash(credential), NodeSecrets.EncryptReceipt(credential, recovery, request.AttemptId), Now);
        entry.Consume(Now); db.NodePairingAttempts.Add(attempt);
        return new PairNodeReceipt(nodeId, credential, attempt.ExpiresAt);
    }, ct);
        if (receipt.NodeId == Guid.Empty) throw NodeSecrets.PairingError();
        return receipt;
    }
    public Task<NodeView> FinalizeAsync(FinalizeNodeRequest request, CancellationToken ct = default) => Locked(async () =>
    {
        var nodeId = NodeSecrets.CredentialNodeId(request.Credential) ?? throw NodeSecrets.PairingError();
        var existing = await db.NodeCredentials.SingleOrDefaultAsync(c => c.NodeId == nodeId, ct);
        if (existing is not null)
        {
            if (!NodeSecrets.Matches(existing.SecretHash, request.Credential)) throw NodeSecrets.PairingError();
            return NodeView.From(await Active(nodeId, ct)); // Finalize response lost after commit: same identity, no new Node.
        }
        await Prune(ct);
        var attempt = await db.NodePairingAttempts.SingleOrDefaultAsync(a => a.Id == request.AttemptId && a.NodeId == nodeId, ct);
        if (attempt is null || attempt.ExpiresAt <= Now || !NodeSecrets.Matches(attempt.CredentialHash, request.Credential)) throw NodeSecrets.PairingError();
        var code = await db.NodePairingCodes.SingleAsync(c => c.Id == attempt.PairingCodeId, ct);
        if (code.IssuerNodeId is { } issuer) await Active(issuer, ct);
        var node = new AegisNode(nodeId, attempt.Name, attempt.Platform, attempt.AppVersion, 1, Now);
        db.Nodes.Add(node); db.NodeCredentials.Add(new NodeCredential(node.Id, attempt.CredentialHash, Now));
        db.NodePairingAttempts.Remove(attempt); // Erase retry receipt/recovery hash on successful establishment.
        return NodeView.From(node);
    }, ct);
    public async Task<NodeAuthentication> AuthenticateAsync(string credential, CancellationToken ct = default)
    {
        var id = NodeSecrets.CredentialNodeId(credential);
        if (id is null) return new(null, "node_authentication_required");
        var stored = await db.NodeCredentials.AsNoTracking().SingleOrDefaultAsync(c => c.NodeId == id, ct);
        if (stored is null || !NodeSecrets.Matches(stored.SecretHash, credential)) return new(null, "node_authentication_required");
        var node = await db.Nodes.AsNoTracking().SingleOrDefaultAsync(n => n.Id == id, ct);
        if (node is null) return new(null, "node_authentication_required");
        var error = node.RevokedAt is not null || stored.RevokedAt is not null ? "node_revoked" : !node.Enabled ? "node_disabled" : null;
        return new(NodeView.From(node), error);
    }
    public Task<NodeView> MeAsync(Guid actor, CancellationToken ct = default) => Locked(async () => NodeView.From(await Active(actor, ct)), ct);
    public Task<IReadOnlyList<NodeView>> ListAsync(Guid actor, CancellationToken ct = default) => Locked<IReadOnlyList<NodeView>>(async () =>
    {
        await Active(actor, ct); return (await db.Nodes.OrderBy(n => n.CreatedAt).ToListAsync(ct)).Select(NodeView.From).ToArray();
    }, ct);
    public Task<NodeView> RenameAsync(Guid actor, Guid target, string name, CancellationToken ct = default) => Locked(async () =>
    {
        await Active(actor, ct); var node = await Target(target, ct);
        try { node.Rename(name, Now); } catch (ArgumentException) { throw new NodeException("invalid_name", "Nome inválido."); }
        return NodeView.From(node);
    }, ct);
    public Task<NodeView> SetEnabledAsync(Guid? actor, Guid target, bool enabled, CancellationToken ct = default) => Locked(async () =>
    {
        if (actor is { } id) await Active(id, ct);
        var node = await Target(target, ct); node.SetEnabled(enabled, Now); return NodeView.From(node);
    }, ct);
    public Task<NodeView> RevokeAsync(Guid actor, Guid target, CancellationToken ct = default) => Locked(async () =>
    {
        await Active(actor, ct); var node = await Target(target, ct); node.Revoke(Now);
        var credential = await db.NodeCredentials.SingleAsync(c => c.NodeId == target, ct); credential.Revoke(Now); return NodeView.From(node);
    }, ct);
}
