namespace Aegis.Application.Chat;

public sealed record ConversationResponse(
    Guid Id,
    string? Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? TitleSource,
    IReadOnlyList<ChatMessageResponse> Messages);
