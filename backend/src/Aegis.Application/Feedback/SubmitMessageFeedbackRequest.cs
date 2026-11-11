// Copyright (C) 2026 Pedro Hasson Castello.
// GNU AGPL v3 only; attribution terms: /ADDITIONAL_TERMS.md.
namespace Aegis.Application.Feedback;

public sealed record SubmitMessageFeedbackRequest(
    string Rating,
    string? Reason = null,
    string? Comment = null,
    string? CorrectedAnswer = null);
