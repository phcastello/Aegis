namespace Aegis.Application.Chat;

public sealed record ChatMessageResponse(
    Guid Id,
    Guid ConversationId,
    string Role,
    string Content,
    DateTimeOffset CreatedAt,
    string? Model);
