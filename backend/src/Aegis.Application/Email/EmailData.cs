namespace Aegis.Application.Email;

public enum EmailBodyReadPurpose
{
    Briefing,
    Full
}

public sealed record EmailSummaryData(
    string Id,
    string ThreadId,
    string? From,
    string? To,
    string? Subject,
    string? Snippet,
    DateTimeOffset? ReceivedAt,
    IReadOnlyList<string> Labels,
    IReadOnlyList<EmailAttachmentData> Attachments,
    bool IsUnread,
    bool IsStarred,
    bool IsImportant)
{
    public bool HasAttachments => Attachments.Count > 0;
}

public sealed record EmailSearchResultData(
    IReadOnlyList<EmailSummaryData> Emails,
    int TotalMatchingCount);

public sealed record EmailContentData(
    string Id,
    string ThreadId,
    string? From,
    string? To,
    string? Cc,
    string? Subject,
    DateTimeOffset? ReceivedAt,
    string BodyText,
    string? Snippet,
    IReadOnlyList<string> Labels,
    IReadOnlyList<EmailAttachmentData> Attachments,
    bool IsUnread,
    bool IsStarred,
    bool IsImportant);

public sealed record EmailAttachmentData(
    string? AttachmentId,
    string? FileName,
    string? MimeType,
    long? Size,
    bool IsInline);

public sealed record ThreadData(
    string ThreadId,
    string? Subject,
    IReadOnlyList<EmailContentData> Messages);

public sealed record EmailModificationResult(
    string ActionType,
    int RequestedCount,
    int ModifiedCount,
    IReadOnlyList<string> EmailIds);

public sealed record EmailSignal(
    string EmailId,
    string ThreadId,
    string SignalType,
    string Summary,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    DateTimeOffset? DueAt,
    string? Location,
    string? Source,
    string? SuggestedAction,
    string? Confidence);
