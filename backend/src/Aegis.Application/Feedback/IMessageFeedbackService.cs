namespace Aegis.Application.Feedback;

public interface IMessageFeedbackService
{
    Task<MessageFeedbackResponse> SubmitFeedbackAsync(
        Guid messageId,
        SubmitMessageFeedbackRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FeedbackSummaryResponse>> GetRecentFeedbackAsync(
        int limit = 50,
        CancellationToken cancellationToken = default);

    Task<FeedbackDetailResponse?> GetFeedbackDetailAsync(
        Guid feedbackId,
        CancellationToken cancellationToken = default);
}
