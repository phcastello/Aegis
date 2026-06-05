namespace Aegis.Application.Feedback;

public sealed record FeedbackDetailResponse(
    Guid Id,
    Guid ConversationId,
    Guid MessageId,
    string AssistantAnswer,
    string? UserMessageBefore,
    string? Model,
    string? PromptSnapshot,
    string? RuntimeContextSnapshot,
    string? MessageMetadataJson,
    string Rating,
    string? Reason,
    string? Comment,
    string? CorrectedAnswer,
    string? FeedbackMetadataJson,
    DateTimeOffset CreatedAt);
