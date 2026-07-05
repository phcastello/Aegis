namespace Aegis.Application.Chat;

public sealed record ConversationSummaryData(
    Guid Id,
    string? Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? TitleSource,
    int MessageCount,
    string? LastMessageContent);
