namespace Aegis.Application.Feedback;

public sealed record MessageFeedbackResponse(
    Guid Id,
    Guid ConversationId,
    Guid MessageId,
    string Rating,
    string? Reason,
    string? Comment,
    string? CorrectedAnswer,
    DateTimeOffset CreatedAt);
