namespace Aegis.Application.Chat;

public sealed record ConversationPageResponse(
    IReadOnlyList<ConversationSummaryResponse> Items,
    string? NextCursor,
    bool HasMore);
