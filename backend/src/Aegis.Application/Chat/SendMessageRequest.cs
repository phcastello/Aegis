namespace Aegis.Application.Chat;

public sealed class SendMessageRequest
{
    public Guid? ConversationId { get; init; }

    public string Content { get; init; } = string.Empty;
}
