namespace Aegis.Application.Chat;

public sealed record SendMessageResponse(
    Guid ConversationId,
    ChatMessageResponse Message);
