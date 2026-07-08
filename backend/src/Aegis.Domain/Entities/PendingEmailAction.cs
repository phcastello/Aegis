using Aegis.Domain;

namespace Aegis.Domain.Entities;

public sealed class PendingEmailAction : AuditableEntity
{
    private PendingEmailAction()
    {
    }

    public PendingEmailAction(
        Guid conversationId,
        string actionType,
        string emailIdsJson,
        string humanSummary,
        DateTimeOffset expiresAt)
    {
        if (!EmailActionTypes.IsKnown(actionType))
        {
            throw new ArgumentException($"Unsupported email action '{actionType}'.", nameof(actionType));
        }

        if (string.IsNullOrWhiteSpace(emailIdsJson))
        {
            throw new ArgumentException("Email ids are required.", nameof(emailIdsJson));
        }

        if (string.IsNullOrWhiteSpace(humanSummary))
        {
            throw new ArgumentException("Human summary is required.", nameof(humanSummary));
        }

        InitializeAudit();
        ConversationId = conversationId;
        ActionType = actionType;
        EmailIdsJson = emailIdsJson;
        HumanSummary = humanSummary.Trim();
        ExpiresAt = expiresAt;
    }

    public Guid ConversationId { get; private set; }

    public string ActionType { get; private set; } = string.Empty;

    public string EmailIdsJson { get; private set; } = "[]";

    public string HumanSummary { get; private set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? ConfirmedAt { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public DateTimeOffset? ExecutedAt { get; private set; }

    public Conversation? Conversation { get; private set; }

    public bool IsOpen(DateTimeOffset? now = null)
    {
        var timestamp = now ?? DateTimeOffset.UtcNow;
        return ConfirmedAt is null &&
            CancelledAt is null &&
            ExecutedAt is null &&
            ExpiresAt > timestamp;
    }

    public void Confirm(DateTimeOffset? now = null)
    {
        if (!IsOpen(now))
        {
            throw new InvalidOperationException("Pending email action is not open.");
        }

        ConfirmedAt = now ?? DateTimeOffset.UtcNow;
        Touch(ConfirmedAt);
    }

    public void Cancel(DateTimeOffset? now = null)
    {
        if (CancelledAt is not null || ExecutedAt is not null)
        {
            return;
        }

        CancelledAt = now ?? DateTimeOffset.UtcNow;
        Touch(CancelledAt);
    }

    public void MarkExecuted(DateTimeOffset? now = null)
    {
        ExecutedAt = now ?? DateTimeOffset.UtcNow;
        Touch(ExecutedAt);
    }
}
