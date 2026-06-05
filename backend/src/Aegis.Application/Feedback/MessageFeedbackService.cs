using Aegis.Application.Common;
using Aegis.Domain;
using Aegis.Domain.Entities;

namespace Aegis.Application.Feedback;

public sealed class MessageFeedbackService(IAegisDbContext dbContext) : IMessageFeedbackService
{
    private const int MaxRecentFeedbackLimit = 200;

    public async Task<MessageFeedbackResponse> SubmitFeedbackAsync(
        Guid messageId,
        SubmitMessageFeedbackRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rating = NormalizeRequiredKeyword(request.Rating, nameof(request.Rating));
        if (!FeedbackRatings.IsKnown(rating))
        {
            throw new ArgumentException("Feedback rating must be 'good' or 'bad'.", nameof(request));
        }

        var reason = NormalizeOptionalKeyword(request.Reason);
        if (reason is not null && !FeedbackReasons.IsKnownForRating(rating, reason))
        {
            throw new ArgumentException(
                $"Feedback reason '{request.Reason}' is not valid for rating '{rating}'.",
                nameof(request));
        }

        var message = await dbContext.GetChatMessageAsync(messageId, cancellationToken)
            ?? throw new MessageFeedbackTargetNotFoundException(messageId);

        if (message.Role != ChatRoles.Assistant)
        {
            throw new ArgumentException("Feedback can only be submitted for assistant messages.", nameof(messageId));
        }

        var feedback = new MessageFeedback(
            message.ConversationId,
            message.Id,
            rating,
            reason,
            request.Comment,
            request.CorrectedAnswer);

        dbContext.AddMessageFeedback(feedback);
        await dbContext.SaveChangesAsync(cancellationToken);

        return MapResponse(feedback);
    }

    public async Task<IReadOnlyList<FeedbackSummaryResponse>> GetRecentFeedbackAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var normalizedLimit = Math.Clamp(limit, 1, MaxRecentFeedbackLimit);
        var feedbackItems = await dbContext.GetRecentMessageFeedbackAsync(normalizedLimit, cancellationToken);

        return feedbackItems
            .Select(feedback => new FeedbackSummaryResponse(
                feedback.Id,
                feedback.ConversationId,
                feedback.MessageId,
                feedback.Message?.Content ?? string.Empty,
                feedback.Rating,
                feedback.Reason,
                feedback.Comment,
                feedback.CorrectedAnswer,
                feedback.CreatedAt))
            .ToList();
    }

    public async Task<FeedbackDetailResponse?> GetFeedbackDetailAsync(
        Guid feedbackId,
        CancellationToken cancellationToken = default)
    {
        var feedback = await dbContext.GetMessageFeedbackWithMessageAsync(feedbackId, cancellationToken);
        if (feedback?.Message is null)
        {
            return null;
        }

        var previousUserMessage = await dbContext.GetPreviousUserMessageAsync(
            feedback.ConversationId,
            feedback.Message.CreatedAt,
            cancellationToken);

        return new FeedbackDetailResponse(
            feedback.Id,
            feedback.ConversationId,
            feedback.MessageId,
            feedback.Message.Content,
            previousUserMessage?.Content,
            feedback.Message.Model,
            feedback.Message.PromptSnapshot,
            feedback.Message.RuntimeContextSnapshot,
            feedback.Message.MetadataJson,
            feedback.Rating,
            feedback.Reason,
            feedback.Comment,
            feedback.CorrectedAnswer,
            feedback.MetadataJson,
            feedback.CreatedAt);
    }

    private static MessageFeedbackResponse MapResponse(MessageFeedback feedback)
    {
        return new MessageFeedbackResponse(
            feedback.Id,
            feedback.ConversationId,
            feedback.MessageId,
            feedback.Rating,
            feedback.Reason,
            feedback.Comment,
            feedback.CorrectedAnswer,
            feedback.CreatedAt);
    }

    private static string NormalizeRequiredKeyword(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be empty.", parameterName);
        }

        return value.Trim().ToLowerInvariant();
    }

    private static string? NormalizeOptionalKeyword(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim().ToLowerInvariant();
    }
}
