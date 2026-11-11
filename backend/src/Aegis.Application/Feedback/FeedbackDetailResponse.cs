// Copyright (C) 2026 Pedro Hasson Castello.
// GNU AGPL v3 only; attribution terms: /ADDITIONAL_TERMS.md.
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
