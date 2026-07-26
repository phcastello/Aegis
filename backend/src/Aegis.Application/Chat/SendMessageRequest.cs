namespace Aegis.Application.Chat;

public sealed class SendMessageRequest
{
    public Guid? TurnId { get; init; }

    public Guid? ConversationId { get; init; }

    public string Content { get; init; } = string.Empty;
}
