namespace Aegis.Application.Chat;

public sealed record ConversationSummaryResponse(
    Guid Id,
    string? Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? TitleSource,
    int MessageCount,
    string? LastMessagePreview,
    bool HasUnreadProactive = false);
