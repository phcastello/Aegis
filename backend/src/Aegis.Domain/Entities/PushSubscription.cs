namespace Aegis.Domain.Entities;

public sealed class PushSubscription : AuditableEntity
{
    private PushSubscription() { }
    public PushSubscription(string endpoint, string p256dh, string auth, Guid deviceId, string? userAgent, DateTimeOffset now)
    {
        InitializeAudit(now);
        Endpoint = endpoint;
        DeviceId = deviceId;
        Refresh(p256dh, auth, userAgent, now);
    }
    public string Endpoint { get; private set; } = "";
    public string P256dh { get; private set; } = "";
    public string Auth { get; private set; } = "";
    public Guid DeviceId { get; private set; }
    public string? UserAgent { get; private set; }
    public DateTimeOffset? LastSuccessfulPushAt { get; private set; }
    public DateTimeOffset? DisabledAt { get; private set; }
    public void Refresh(string p256dh, string auth, string? userAgent, DateTimeOffset now)
    {
        P256dh = p256dh; Auth = auth; UserAgent = userAgent; DisabledAt = null; Touch(now);
    }
    public void Disable(DateTimeOffset now) { DisabledAt ??= now; Touch(now); }
    public void Accepted(DateTimeOffset now) { LastSuccessfulPushAt = now; Touch(now); }
}
