namespace Aegis.Domain.Entities;

public sealed class ReminderDeliveryAttempt : AuditableEntity
{
    private ReminderDeliveryAttempt() { }
    public ReminderDeliveryAttempt(Guid reminderId, int attempt, Guid? nodeId, Guid? commandId,
        DateTimeOffset? commandExpiresAt, DateTimeOffset now)
    {
        InitializeAudit(now);
        ReminderId = reminderId; Attempt = attempt; NodeId = nodeId; CommandId = commandId;
        CommandExpiresAt = commandExpiresAt; AttemptedAt = now;
    }
    public Guid ReminderId { get; private set; }
    public Guid? NodeId { get; private set; }
    public Guid? CommandId { get; private set; }
    public DateTimeOffset? CommandExpiresAt { get; private set; }
    public int Attempt { get; private set; }
    public DateTimeOffset AttemptedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public DateTimeOffset? AcceptedAt { get; private set; }
    public string? Result { get; private set; }
    public string? Transport { get; private set; }
    public bool OutcomeAmbiguous { get; private set; }
    public DateTimeOffset? RetryAt { get; private set; }
    public void Complete(string result, string? transport, bool ambiguous, DateTimeOffset? retryAt, DateTimeOffset now)
    {
        Result = result; Transport = transport; OutcomeAmbiguous = ambiguous; RetryAt = retryAt;
        CompletedAt = now;
        if (result is "success" or "accepted" or "duplicate") { AcceptedAt = now; OutcomeAmbiguous = false; RetryAt = null; }
        Touch(now);
    }
}
