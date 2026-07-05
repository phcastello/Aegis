namespace Aegis.Application.Chat;

public sealed record SendMessageResponse(
    Guid ConversationId,
    string? ConversationTitle,
    string? TitleSource,
    ChatMessageResponse Message);
