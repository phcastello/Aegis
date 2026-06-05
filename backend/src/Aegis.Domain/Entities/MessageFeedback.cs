using Aegis.Domain;

namespace Aegis.Domain.Entities;

public sealed class MessageFeedback : AuditableEntity
{
    private MessageFeedback()
    {
    }

    public MessageFeedback(
        Guid conversationId,
        Guid messageId,
        string rating,
        string? reason = null,
        string? comment = null,
        string? correctedAnswer = null,
        string? metadataJson = null)
    {
        var normalizedRating = NormalizeRequired(rating, nameof(rating));
        if (!FeedbackRatings.IsKnown(normalizedRating))
        {
            throw new ArgumentException($"Unsupported feedback rating '{rating}'.", nameof(rating));
        }

        var normalizedReason = NormalizeOptionalKeyword(reason);
        if (normalizedReason is not null && !FeedbackReasons.IsKnownForRating(normalizedRating, normalizedReason))
        {
            throw new ArgumentException(
                $"Feedback reason '{reason}' is not valid for rating '{normalizedRating}'.",
                nameof(reason));
        }

        InitializeAudit();
        ConversationId = conversationId;
        MessageId = messageId;
        Rating = normalizedRating;
        Reason = normalizedReason;
        Comment = NormalizeOptionalText(comment);
        CorrectedAnswer = NormalizeOptionalText(correctedAnswer);
        MetadataJson = NormalizeOptionalText(metadataJson);
    }

    public Guid ConversationId { get; private set; }

    public Guid MessageId { get; private set; }

    public string Rating { get; private set; } = string.Empty;

    public string? Reason { get; private set; }

    public string? Comment { get; private set; }

    public string? CorrectedAnswer { get; private set; }

    public string? MetadataJson { get; private set; }

    public Conversation? Conversation { get; private set; }

    public ChatMessage? Message { get; private set; }

    private static string NormalizeRequired(string value, string parameterName)
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

    private static string? NormalizeOptionalText(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }
}
