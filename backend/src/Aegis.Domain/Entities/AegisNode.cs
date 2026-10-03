namespace Aegis.Domain.Entities;

public enum NodePlatform { Android, Windows }

public sealed class AegisNode : AuditableEntity
{
    private AegisNode() { }
    public AegisNode(Guid id, string name, NodePlatform platform, string appVersion, int protocolVersion, DateTimeOffset now)
    {
        if (id == Guid.Empty) throw new ArgumentException("Node ID cannot be empty.");
        Validate(name, platform, appVersion, protocolVersion);
        InitializeAudit(now); Id = id; Name = name.Trim(); Platform = platform;
        AppVersion = appVersion; ProtocolVersion = protocolVersion; PairedAt = now; Enabled = true;
    }
    public string Name { get; private set; } = "";
    public NodePlatform Platform { get; private set; }
    public bool Enabled { get; private set; }
    public int TargetPriority { get; private set; }
    public void SetTargetPriority(int priority, DateTimeOffset now) { RequireTrusted(); if (priority is < -1000 or > 1000) throw new ArgumentException("Invalid target priority."); TargetPriority = priority; Touch(now); }
    public string AppVersion { get; private set; } = "";
    public int ProtocolVersion { get; private set; }
    public DateTimeOffset PairedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public DateTimeOffset? LastSeenAt { get; private set; }
    public DateTimeOffset? LastHeartbeatAt { get; private set; }
    public void TransportSeen(DateTimeOffset seenAt, DateTimeOffset? heartbeatAt = null, string? appVersion = null)
    {
        if (LastSeenAt is null || seenAt > LastSeenAt) LastSeenAt = seenAt;
        if (heartbeatAt is { } heartbeat && (LastHeartbeatAt is null || heartbeat > LastHeartbeatAt)) LastHeartbeatAt = heartbeat;
        if (appVersion is not null) { Validate(Name, Platform, appVersion, ProtocolVersion); AppVersion = appVersion; }
    }
    public void Rename(string name, DateTimeOffset now) { RequireTrusted(); ValidateName(name); Name = name.Trim(); Touch(now); }
    public void SetEnabled(bool enabled, DateTimeOffset now) { RequireTrusted(); Enabled = enabled; Touch(now); }
    public void Revoke(DateTimeOffset now) { if (RevokedAt is null) { RevokedAt = now; Enabled = false; Touch(now); } }
    private void RequireTrusted() { if (RevokedAt is not null) throw new InvalidOperationException("Node identity is revoked."); }
    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100 || name.Any(char.IsControl))
            throw new ArgumentException("O nome precisa ter entre 1 e 100 caracteres, sem controles.");
    }
    public static void Validate(string name, NodePlatform platform, string version, int protocol)
    {
        ValidateName(name);
        if (!Enum.IsDefined(platform) || protocol != 1 || string.IsNullOrWhiteSpace(version) || version.Length > 80 ||
            !System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$"))
            throw new ArgumentException("Plataforma, versão ou protocolo de Node inválido.");
    }
}

public sealed class NodeCredential
{
    private NodeCredential() { }
    public NodeCredential(Guid nodeId, byte[] hash, DateTimeOffset now) { NodeId = nodeId; SecretHash = hash; CreatedAt = now; }
    public Guid NodeId { get; private set; }
    public byte[] SecretHash { get; private set; } = [];
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;
}

public sealed class NodePairingCode : AuditableEntity
{
    private NodePairingCode() { }
    public NodePairingCode(string selector, byte[] hash, Guid? issuer, DateTimeOffset now)
    { InitializeAudit(now); Selector = selector; CodeHash = hash; IssuerNodeId = issuer; ExpiresAt = now.AddMinutes(10); }
    public string Selector { get; private set; } = "";
    public byte[] CodeHash { get; private set; } = [];
    public Guid? IssuerNodeId { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? ConsumedAt { get; private set; }
    public int FailedAttempts { get; private set; }
    public bool IsUsable(DateTimeOffset now) => ConsumedAt is null && ExpiresAt > now && FailedAttempts < 5;
    public void Fail(DateTimeOffset now) { FailedAttempts++; Touch(now); }
    public void Consume(DateTimeOffset now) { if (!IsUsable(now)) throw new InvalidOperationException("Pairing code unavailable."); ConsumedAt = now; Touch(now); }
}

// No Node exists until its issued credential has been securely stored and confirmed.
public sealed class NodePairingAttempt : AuditableEntity
{
    private NodePairingAttempt() { }
    public NodePairingAttempt(Guid id, Guid nodeId, NodePairingCode code, string name, NodePlatform platform,
        string version, byte[] recoveryHash, byte[] credentialHash, byte[] receipt, DateTimeOffset now)
    {
        InitializeAudit(now); Id = id; NodeId = nodeId; PairingCodeId = code.Id; Name = name.Trim(); Platform = platform;
        AppVersion = version; RecoveryHash = recoveryHash; CredentialHash = credentialHash; EncryptedReceipt = receipt; ExpiresAt = code.ExpiresAt;
    }
    public Guid NodeId { get; private set; }
    public Guid PairingCodeId { get; private set; }
    public string Name { get; private set; } = "";
    public NodePlatform Platform { get; private set; }
    public string AppVersion { get; private set; } = "";
    public byte[] RecoveryHash { get; private set; } = [];
    public byte[] CredentialHash { get; private set; } = [];
    public byte[] EncryptedReceipt { get; private set; } = [];
    public DateTimeOffset ExpiresAt { get; private set; }
}
