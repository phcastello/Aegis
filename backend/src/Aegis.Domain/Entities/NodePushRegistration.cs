namespace Aegis.Domain.Entities;
public sealed class NodePushRegistration
{
    private NodePushRegistration() { }
    public NodePushRegistration(Guid id, string encryptedToken, byte[] tokenHash, DateTimeOffset now)
    { NodeId = id; Provider = "fcm"; CreatedAt = now; Refresh(encryptedToken, tokenHash, now); }
    public Guid NodeId { get; private set; }
    public string Provider { get; private set; } = "fcm";
    public string EncryptedToken { get; private set; } = "";
    public byte[] TokenHash { get; private set; } = [];
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public void Refresh(string encryptedToken, byte[] hash, DateTimeOffset now) { EncryptedToken = encryptedToken; TokenHash = hash; UpdatedAt = now; ExpiresAt = now.AddDays(30); }
}
