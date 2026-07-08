namespace Aegis.Domain.Entities;

public sealed class EmailActionAudit : AuditableEntity
{
    private EmailActionAudit()
    {
    }

    public EmailActionAudit(
        Guid conversationId,
        string actionType,
        string emailIdsJson,
        Guid? userConfirmationMessageId,
        bool success,
        string? failureReason = null)
    {
        if (string.IsNullOrWhiteSpace(actionType))
        {
            throw new ArgumentException("Action type is required.", nameof(actionType));
        }

        if (string.IsNullOrWhiteSpace(emailIdsJson))
        {
            throw new ArgumentException("Email ids are required.", nameof(emailIdsJson));
        }

        InitializeAudit();
        ConversationId = conversationId;
        ActionType = actionType.Trim();
        EmailIdsJson = emailIdsJson;
        UserConfirmationMessageId = userConfirmationMessageId;
        Success = success;
        FailureReason = string.IsNullOrWhiteSpace(failureReason) ? null : failureReason.Trim();
    }

    public Guid ConversationId { get; private set; }

    public string ActionType { get; private set; } = string.Empty;

    public string EmailIdsJson { get; private set; } = "[]";

    public Guid? UserConfirmationMessageId { get; private set; }

    public bool Success { get; private set; }

    public string? FailureReason { get; private set; }

    public Conversation? Conversation { get; private set; }

    public ChatMessage? UserConfirmationMessage { get; private set; }
}
