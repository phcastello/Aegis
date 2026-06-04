namespace Aegis.Application.Chat;

public sealed class ConversationNotFoundException(Guid conversationId)
    : Exception($"Conversation '{conversationId}' was not found.")
{
    public Guid ConversationId { get; } = conversationId;
}
