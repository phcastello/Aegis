namespace Aegis.Domain.Entities;

public sealed class CalendarActionAudit : AuditableEntity
{
    private CalendarActionAudit()
    {
    }

    public CalendarActionAudit(
        Guid pendingActionId,
        Guid conversationId,
        string actionType,
        string eventId,
        Guid? userConfirmationMessageId,
        bool success,
        string? failureReason = null,
        string calendarId = "primary")
    {
        if (string.IsNullOrWhiteSpace(actionType))
        {
            throw new ArgumentException("Action type is required.", nameof(actionType));
        }

        if (string.IsNullOrWhiteSpace(eventId))
        {
            throw new ArgumentException("Event id is required.", nameof(eventId));
        }

        if (string.IsNullOrWhiteSpace(calendarId) || calendarId.Length > 1024)
            throw new ArgumentException("Calendar id is required and must fit storage.", nameof(calendarId));

        InitializeAudit();
        CalendarId = calendarId;
        PendingActionId = pendingActionId;
        ConversationId = conversationId;
        ActionType = actionType.Trim();
        EventId = eventId;
        UserConfirmationMessageId = userConfirmationMessageId;
        Success = success;
        FailureReason = string.IsNullOrWhiteSpace(failureReason) ? null : failureReason.Trim();
    }

    public Guid PendingActionId { get; private set; }

    public Guid ConversationId { get; private set; }

    public string ActionType { get; private set; } = string.Empty;

    public string CalendarId { get; private set; } = "primary";

    public string EventId { get; private set; } = string.Empty;

    public Guid? UserConfirmationMessageId { get; private set; }

    public bool Success { get; private set; }

    public string? FailureReason { get; private set; }

    public Conversation? Conversation { get; private set; }

    public ChatMessage? UserConfirmationMessage { get; private set; }
}
