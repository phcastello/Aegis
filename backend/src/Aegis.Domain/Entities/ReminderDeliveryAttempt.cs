namespace Aegis.Domain.Entities;

public sealed class ReminderDeliveryAttempt : AuditableEntity
{
    private ReminderDeliveryAttempt() { }
    public ReminderDeliveryAttempt(Guid reminderId, Guid subscriptionId, int attempt, DateTimeOffset now)
    {
        InitializeAudit(now);
        ReminderId = reminderId; PushSubscriptionId = subscriptionId; Attempt = attempt; AttemptedAt = now;
    }
    public Guid ReminderId { get; private set; }
    public Guid PushSubscriptionId { get; private set; }
    public int Attempt { get; private set; }
    public DateTimeOffset AttemptedAt { get; private set; }
    public DateTimeOffset? AcceptedAt { get; private set; }
    public string? FailureReason { get; private set; }
    public int? HttpStatus { get; private set; }
    public DateTimeOffset? RetryAt { get; private set; }
    public void Complete(int? status, string? failure, DateTimeOffset? retryAt, DateTimeOffset now)
    {
        HttpStatus = status; FailureReason = failure; RetryAt = retryAt;
        if (failure is null) AcceptedAt = now;
        Touch(now);
    }
}
