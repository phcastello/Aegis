namespace Aegis.Application.Feedback;

public sealed record SubmitMessageFeedbackRequest(
    string Rating,
    string? Reason = null,
    string? Comment = null,
    string? CorrectedAnswer = null);
