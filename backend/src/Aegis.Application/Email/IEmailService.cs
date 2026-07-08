namespace Aegis.Application.Email;

public interface IEmailService
{
    Task<EmailSearchResultData> SearchEmailsAsync(
        string? query,
        int? limit = null,
        bool? includeRead = null,
        int? newerThanDays = null,
        CancellationToken cancellationToken = default);

    Task<EmailContentData> ReadEmailAsync(
        string emailId,
        EmailBodyReadPurpose readPurpose = EmailBodyReadPurpose.Full,
        CancellationToken cancellationToken = default);

    Task<ThreadData> ReadThreadAsync(
        string threadId,
        EmailBodyReadPurpose readPurpose = EmailBodyReadPurpose.Full,
        CancellationToken cancellationToken = default);

    Task<EmailModificationResult> MarkReadAsync(
        IReadOnlyList<string> emailIds,
        CancellationToken cancellationToken = default);

    Task<EmailModificationResult> MarkUnreadAsync(
        IReadOnlyList<string> emailIds,
        CancellationToken cancellationToken = default);

    Task<EmailModificationResult> StarAsync(
        IReadOnlyList<string> emailIds,
        CancellationToken cancellationToken = default);

    Task<EmailModificationResult> UnstarAsync(
        IReadOnlyList<string> emailIds,
        CancellationToken cancellationToken = default);

    Task<EmailModificationResult> MarkImportantAsync(
        IReadOnlyList<string> emailIds,
        CancellationToken cancellationToken = default);

    Task<EmailModificationResult> UnmarkImportantAsync(
        IReadOnlyList<string> emailIds,
        CancellationToken cancellationToken = default);
}
