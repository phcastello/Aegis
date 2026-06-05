namespace Aegis.Application.Feedback;

public sealed record FeedbackSummaryResponse(
    Guid Id,
    Guid ConversationId,
    Guid MessageId,
    string MessageContent,
    string Rating,
    string? Reason,
    string? Comment,
    string? CorrectedAnswer,
    DateTimeOffset CreatedAt);
