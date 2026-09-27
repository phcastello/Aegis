using Aegis.Domain;

namespace Aegis.Domain.Entities;

public sealed class PendingCalendarAction : AuditableEntity
{
    private PendingCalendarAction()
    {
    }

    public PendingCalendarAction(
        Guid conversationId,
        string actionType,
        string eventId,
        string payloadJson,
        string humanSummary,
        DateTimeOffset expiresAt,
        string calendarId = "primary")
    {
        if (!CalendarActionTypes.IsKnown(actionType))
        {
            throw new ArgumentException($"Unsupported calendar action '{actionType}'.", nameof(actionType));
        }

        if (string.IsNullOrWhiteSpace(eventId))
        {
            throw new ArgumentException("Event id is required.", nameof(eventId));
        }

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            throw new ArgumentException("Calendar payload is required.", nameof(payloadJson));
        }

        if (string.IsNullOrWhiteSpace(humanSummary))
        {
            throw new ArgumentException("Human summary is required.", nameof(humanSummary));
        }

        if (string.IsNullOrWhiteSpace(calendarId) || calendarId.Length > 1024)
            throw new ArgumentException("Calendar id is required and must fit storage.", nameof(calendarId));

        InitializeAudit();
        CalendarId = calendarId;
        ConversationId = conversationId;
        ActionType = actionType;
        EventId = eventId;
        PayloadJson = payloadJson;
        HumanSummary = humanSummary.Trim();
        ExpiresAt = expiresAt;
    }

    public Guid ConversationId { get; private set; }

    public string ActionType { get; private set; } = string.Empty;

    public string CalendarId { get; private set; } = "primary";

    public string EventId { get; private set; } = string.Empty;

    public string PayloadJson { get; private set; } = "{}";

    public string HumanSummary { get; private set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? ConfirmedAt { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public DateTimeOffset? ExecutedAt { get; private set; }

    public DateTimeOffset? SupersededAt { get; private set; }

    public Guid? SupersededById { get; private set; }

    public bool MayHaveAppliedChanges { get; private set; }

    public Conversation? Conversation { get; private set; }

    public bool IsOpen(DateTimeOffset? now = null)
    {
        var timestamp = now ?? DateTimeOffset.UtcNow;
        return ConfirmedAt is null &&
            CancelledAt is null &&
            ExecutedAt is null &&
            SupersededAt is null &&
            ExpiresAt > timestamp;
    }

    public void Confirm(DateTimeOffset? now = null)
    {
        if (!IsOpen(now))
        {
            throw new InvalidOperationException("Pending calendar action is not open.");
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

    public void Supersede(Guid replacementId)
    {
        if (ConfirmedAt is not null || CancelledAt is not null || ExecutedAt is not null || SupersededAt is not null || MayHaveAppliedChanges)
            throw new InvalidOperationException("Only an unexecuted proposal without possible external effects can be replaced.");
        if (replacementId == Guid.Empty || replacementId == Id) throw new ArgumentException("A different replacement action is required.", nameof(replacementId));
        SupersededAt = DateTimeOffset.UtcNow;
        SupersededById = replacementId;
        Touch(SupersededAt);
    }

    // Used only when a preflight/cancellation proves this attempt sent no mutation.
    public void ClearPossibleExternalEffects()
    {
        MayHaveAppliedChanges = false;
        Touch();
    }

    public void RecordPossibleExternalEffects()
    {
        if (MayHaveAppliedChanges) return;
        MayHaveAppliedChanges = true;
        Touch();
    }
}
