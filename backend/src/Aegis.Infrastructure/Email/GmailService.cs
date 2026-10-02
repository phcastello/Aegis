using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Aegis.Application.Email;
using Aegis.Application.Google;
using Aegis.Domain;
using Microsoft.Extensions.Options;

namespace Aegis.Infrastructure.Email;

public sealed partial class GmailService(
    HttpClient httpClient,
    IOptions<GmailOptions> options,
    IGoogleAccessTokenProvider tokenProvider) : IEmailService
{
    private const int DefaultSearchLimit = 10;
    private const int MaxSearchLimit = 50;
    private const int MaxModificationCount = 100;
    private const int ModificationChunkSize = 20;
    private const int MaxThreadMessages = 15;
    private const int MaxMetadataConcurrency = 4;

    public async Task<EmailSearchResultData> SearchEmailsAsync(
        string? query,
        int? limit = null,
        bool? includeRead = null,
        int? newerThanDays = null,
        CancellationToken cancellationToken = default)
    {
        var accessToken = await GetAccessTokenAsync(cancellationToken);
        var gmailOptions = options.Value;
        var defaultLimit = string.IsNullOrWhiteSpace(query)
            ? gmailOptions.MaxEmailsPerManualBriefing
            : DefaultSearchLimit;
        var effectiveNewerThanDays = newerThanDays ??
            (string.IsNullOrWhiteSpace(query) ? gmailOptions.EmailBriefingLookbackDays : null);
        var normalizedLimit = Math.Clamp(limit ?? defaultLimit, 1, MaxSearchLimit);
        var gmailQuery = BuildQuery(query, includeRead, effectiveNewerThanDays);
        var path = new StringBuilder("https://gmail.googleapis.com/gmail/v1/users/me/messages?maxResults=");
        path.Append(normalizedLimit);
        if (!string.IsNullOrWhiteSpace(gmailQuery))
        {
            path.Append("&q=");
            path.Append(Uri.EscapeDataString(gmailQuery));
        }

        var list = await SendGmailAsync<GmailListMessagesResponse>(
            HttpMethod.Get,
            path.ToString(),
            accessToken,
            body: null,
            cancellationToken)
            ?? new GmailListMessagesResponse();

        if (list.Messages.Count == 0)
        {
            return new EmailSearchResultData([], list.ResultSizeEstimate);
        }

        var results = new List<EmailSummaryData>();
        foreach (var message in list.Messages.Take(normalizedLimit))
        {
            var full = await GetMessageAsync(message.Id, "metadata", accessToken, cancellationToken);
            results.Add(MapSummary(full));
        }

        return new EmailSearchResultData(results, list.ResultSizeEstimate);
    }

    public async Task<EmailContentData> ReadEmailAsync(
        string emailId,
        EmailBodyReadPurpose readPurpose = EmailBodyReadPurpose.Full,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(emailId))
        {
            throw new ArgumentException("Email id is required.", nameof(emailId));
        }

        var accessToken = await GetAccessTokenAsync(cancellationToken);
        var message = await GetMessageAsync(emailId.Trim(), "full", accessToken, cancellationToken);
        return MapContent(message, GetMaxBodyCharacters(readPurpose));
    }

    public async Task<IReadOnlyList<EmailSummaryData>> ReadEmailMetadataBatchAsync(
        IReadOnlyList<string> emailIds,
        CancellationToken cancellationToken = default)
    {
        if (emailIds.Count == 0 || emailIds.Count > MaxModificationCount ||
            emailIds.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("A valid bounded email selection is required.", nameof(emailIds));
        }

        var accessToken = await GetAccessTokenAsync(cancellationToken);
        using var gate = new SemaphoreSlim(MaxMetadataConcurrency);
        var requests = emailIds.Select(async emailId =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var message = await GetMessageAsync(emailId.Trim(), "metadata", accessToken, cancellationToken);
                return MapSummary(message);
            }
            finally
            {
                gate.Release();
            }
        });
        return await Task.WhenAll(requests);
    }

    public async Task<ThreadData> ReadThreadAsync(
        string threadId,
        EmailBodyReadPurpose readPurpose = EmailBodyReadPurpose.Full,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new ArgumentException("Thread id is required.", nameof(threadId));
        }

        var accessToken = await GetAccessTokenAsync(cancellationToken);
        var thread = await SendGmailAsync<GmailThreadResponse>(
            HttpMethod.Get,
            $"https://gmail.googleapis.com/gmail/v1/users/me/threads/{Uri.EscapeDataString(threadId.Trim())}?format=full",
            accessToken,
            body: null,
            cancellationToken)
            ?? throw new InvalidOperationException("Gmail returned an empty thread response.");

        var perMessageLimit = GetMaxBodyCharacters(readPurpose);
        var messages = thread.Messages
            .OrderBy(message => message.InternalDate)
            .TakeLast(MaxThreadMessages)
            .Select(message => MapContent(message, perMessageLimit))
            .ToList();
        var subject = messages.LastOrDefault(message => !string.IsNullOrWhiteSpace(message.Subject))?.Subject;

        return new ThreadData(thread.Id ?? threadId.Trim(), subject, messages);
    }

    private int GetMaxBodyCharacters(EmailBodyReadPurpose readPurpose)
    {
        var gmailOptions = options.Value;
        var configuredLimit = readPurpose == EmailBodyReadPurpose.Briefing
            ? gmailOptions.MaxEmailBriefingBodyChars
            : gmailOptions.MaxEmailFullBodyChars;

        return Math.Max(1, configuredLimit);
    }

    public Task<EmailModificationResult> MarkReadAsync(
        IReadOnlyList<string> emailIds,
        CancellationToken cancellationToken = default)
    {
        return ModifyLabelsAsync(EmailActionTypes.MarkRead, emailIds, [], ["UNREAD"], cancellationToken);
    }

    public Task<EmailModificationResult> MarkUnreadAsync(
        IReadOnlyList<string> emailIds,
        CancellationToken cancellationToken = default)
    {
        return ModifyLabelsAsync(EmailActionTypes.MarkUnread, emailIds, ["UNREAD"], [], cancellationToken);
    }

    public Task<EmailModificationResult> StarAsync(
        IReadOnlyList<string> emailIds,
        CancellationToken cancellationToken = default)
    {
        return ModifyLabelsAsync(EmailActionTypes.Star, emailIds, ["STARRED"], [], cancellationToken);
    }

    public Task<EmailModificationResult> UnstarAsync(
        IReadOnlyList<string> emailIds,
        CancellationToken cancellationToken = default)
    {
        return ModifyLabelsAsync(EmailActionTypes.Unstar, emailIds, [], ["STARRED"], cancellationToken);
    }

    public Task<EmailModificationResult> MarkImportantAsync(
        IReadOnlyList<string> emailIds,
        CancellationToken cancellationToken = default)
    {
        return ModifyLabelsAsync(EmailActionTypes.MarkImportant, emailIds, ["IMPORTANT"], [], cancellationToken);
    }

    public Task<EmailModificationResult> UnmarkImportantAsync(
        IReadOnlyList<string> emailIds,
        CancellationToken cancellationToken = default)
    {
        return ModifyLabelsAsync(EmailActionTypes.UnmarkImportant, emailIds, [], ["IMPORTANT"], cancellationToken);
    }

    private async Task<EmailModificationResult> ModifyLabelsAsync(
        string actionType,
        IReadOnlyList<string> emailIds,
        IReadOnlyList<string> addLabels,
        IReadOnlyList<string> removeLabels,
        CancellationToken cancellationToken)
    {
        var normalizedIds = emailIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (normalizedIds.Count == 0)
        {
            throw new EmailModificationAttemptException(false, 0,
                new ArgumentException("At least one email id is required.", nameof(emailIds)));
        }

        if (normalizedIds.Count > MaxModificationCount)
        {
            throw new EmailModificationAttemptException(false, 0,
                new InvalidOperationException($"Cannot modify more than {MaxModificationCount} emails at once."));
        }

        string accessToken;
        try
        {
            accessToken = await GetAccessTokenAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new EmailModificationAttemptException(false, 0, exception);
        }
        var modifiedCount = 0;
        var requestWasSent = false;

        foreach (var chunk in normalizedIds.Chunk(ModificationChunkSize))
        {
            foreach (var emailId in chunk)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Once SendGmailAsync begins, a canceled HTTP request may still have reached Gmail.
                    requestWasSent = true;
                    await SendGmailAsync<GmailMessageResponse>(
                        HttpMethod.Post,
                        $"https://gmail.googleapis.com/gmail/v1/users/me/messages/{Uri.EscapeDataString(emailId)}/modify",
                        accessToken,
                        new GmailModifyRequest(addLabels, removeLabels),
                        cancellationToken);
                    modifiedCount++;
                }
                catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
                {
                    if (!requestWasSent) throw;
                    throw new EmailModificationCancelledException(
                        requestWasSent, modifiedCount, exception, cancellationToken);
                }
                catch (Exception exception)
                {
                    throw new EmailModificationAttemptException(true, modifiedCount, exception);
                }
            }
        }

        return new EmailModificationResult(actionType, normalizedIds.Count, modifiedCount, normalizedIds);
    }

    private async Task<GmailMessageResponse> GetMessageAsync(
        string emailId,
        string format,
        string accessToken,
        CancellationToken cancellationToken)
    {
        return await SendGmailAsync<GmailMessageResponse>(
            HttpMethod.Get,
            $"https://gmail.googleapis.com/gmail/v1/users/me/messages/{Uri.EscapeDataString(emailId)}?format={format}&metadataHeaders=From&metadataHeaders=To&metadataHeaders=Cc&metadataHeaders=Subject&metadataHeaders=Date",
            accessToken,
            body: null,
            cancellationToken)
            ?? throw new InvalidOperationException("Gmail returned an empty message response.");
    }

    private Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) =>
        tokenProvider.GetAccessTokenAsync(GoogleScopes.Gmail, cancellationToken);

    private async Task<T?> SendGmailAsync<T>(
        HttpMethod method,
        string url,
        string accessToken,
        object? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new("Bearer", accessToken);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new EmailProviderException("email_not_found", "Esse email não está mais disponível. Consulte novamente.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            // Inspect provider reason codes only; never expose the raw provider response.
            var apiDisabled = errorBody.Contains("SERVICE_DISABLED", StringComparison.Ordinal) ||
                errorBody.Contains("accessNotConfigured", StringComparison.Ordinal);
            var scopeMissing = errorBody.Contains("insufficientPermissions", StringComparison.Ordinal) ||
                errorBody.Contains("ACCESS_TOKEN_SCOPE_INSUFFICIENT", StringComparison.Ordinal);
            var transient = (int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests ||
                errorBody.Contains("rateLimitExceeded", StringComparison.Ordinal) ||
                errorBody.Contains("userRateLimitExceeded", StringComparison.Ordinal) ||
                errorBody.Contains("quotaExceeded", StringComparison.Ordinal);
            throw response.StatusCode switch
            {
                _ when apiDisabled => new EmailProviderException("email_api_disabled", "A Gmail API está desativada no projeto Google Cloud. Ative a API no projeto da conexão e tente novamente."),
                _ when transient => new EmailProviderException("email_temporarily_unavailable", "Gmail está temporariamente indisponível ou limitou as requisições. Tente mais tarde."),
                _ when scopeMissing => new EmailProviderException("email_scope_missing", "Falta autorização Gmail. Use email_create_connect_link para reautorizar."),
                HttpStatusCode.Unauthorized => new EmailProviderException("email_authentication_failed", "A autorização Google foi recusada. Use email_create_connect_link."),
                HttpStatusCode.Forbidden => new EmailProviderException("email_access_denied", "Google recusou acesso ao recurso. Verifique as permissões."),
                HttpStatusCode.BadRequest => new EmailProviderException("invalid_tool_arguments", "Google recusou os parâmetros. Corrija a consulta."),
                _ => new EmailProviderException("email_temporarily_unavailable", "Gmail está temporariamente indisponível. Tente mais tarde.")
            };
        }
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);
    }

    private static string BuildQuery(string? query, bool? includeRead, int? newerThanDays)
    {
        var terms = new List<string>();
        if (!string.IsNullOrWhiteSpace(query))
        {
            terms.Add(query.Trim());
        }

        if (includeRead == false &&
            !terms.Any(term => term.Contains("is:unread", StringComparison.OrdinalIgnoreCase)) &&
            !terms.Any(term => term.Contains("is:read", StringComparison.OrdinalIgnoreCase)))
        {
            terms.Add("is:unread");
        }

        if (newerThanDays is > 0 &&
            !terms.Any(term => term.Contains("newer_than:", StringComparison.OrdinalIgnoreCase)))
        {
            terms.Add($"newer_than:{Math.Min(newerThanDays.Value, 365)}d");
        }

        return string.Join(' ', terms);
    }

    private static EmailSummaryData MapSummary(GmailMessageResponse message)
    {
        var labels = message.LabelIds ?? [];
        return new EmailSummaryData(
            message.Id ?? string.Empty,
            message.ThreadId ?? string.Empty,
            GetHeader(message, "From"),
            GetHeader(message, "To"),
            GetHeader(message, "Subject"),
            message.Snippet,
            ParseInternalDate(message.InternalDate) ?? ParseHeaderDate(GetHeader(message, "Date")),
            labels,
            ExtractAttachments(message.Payload),
            labels.Contains("UNREAD", StringComparer.OrdinalIgnoreCase),
            labels.Contains("STARRED", StringComparer.OrdinalIgnoreCase),
            labels.Contains("IMPORTANT", StringComparer.OrdinalIgnoreCase));
    }

    private static EmailContentData MapContent(GmailMessageResponse message, int maxBodyCharacters)
    {
        var labels = message.LabelIds ?? [];
        return new EmailContentData(
            message.Id ?? string.Empty,
            message.ThreadId ?? string.Empty,
            GetHeader(message, "From"),
            GetHeader(message, "To"),
            GetHeader(message, "Cc"),
            GetHeader(message, "Subject"),
            ParseInternalDate(message.InternalDate) ?? ParseHeaderDate(GetHeader(message, "Date")),
            Truncate(ExtractBodyText(message.Payload), maxBodyCharacters),
            message.Snippet,
            labels,
            ExtractAttachments(message.Payload),
            labels.Contains("UNREAD", StringComparer.OrdinalIgnoreCase),
            labels.Contains("STARRED", StringComparer.OrdinalIgnoreCase),
            labels.Contains("IMPORTANT", StringComparer.OrdinalIgnoreCase));
    }

    private static string? GetHeader(GmailMessageResponse message, string name)
    {
        return message.Payload?.Headers?
            .FirstOrDefault(header => string.Equals(header.Name, name, StringComparison.OrdinalIgnoreCase))
            ?.Value;
    }

    private static DateTimeOffset? ParseInternalDate(string? internalDate)
    {
        if (!long.TryParse(internalDate, out var milliseconds))
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
    }

    private static DateTimeOffset? ParseHeaderDate(string? date)
    {
        return DateTimeOffset.TryParse(date, out var parsed) ? parsed : null;
    }

    private static IReadOnlyList<EmailAttachmentData> ExtractAttachments(GmailMessagePart? part)
    {
        var attachments = new List<EmailAttachmentData>();
        WalkParts(part, current =>
        {
            var hasFileName = !string.IsNullOrWhiteSpace(current.FileName);
            var hasAttachmentId = !string.IsNullOrWhiteSpace(current.Body?.AttachmentId);
            if (!hasFileName && !hasAttachmentId)
            {
                return;
            }

            attachments.Add(new EmailAttachmentData(
                current.Body?.AttachmentId,
                current.FileName,
                current.MimeType,
                current.Body?.Size,
                IsInlineAttachment(current)));
        });

        return attachments;
    }

    private static bool IsInlineAttachment(GmailMessagePart part)
    {
        var disposition = part.Headers?
            .FirstOrDefault(header => string.Equals(header.Name, "Content-Disposition", StringComparison.OrdinalIgnoreCase))
            ?.Value;

        return !string.IsNullOrWhiteSpace(disposition) &&
            disposition.Contains("inline", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractBodyText(GmailMessagePart? payload)
    {
        var plainParts = new List<string>();
        var htmlParts = new List<string>();

        WalkParts(payload, part =>
        {
            var data = part.Body?.Data;
            if (string.IsNullOrWhiteSpace(data))
            {
                return;
            }

            var decoded = DecodeBase64Url(data);
            if (string.IsNullOrWhiteSpace(decoded))
            {
                return;
            }

            if (string.Equals(part.MimeType, "text/plain", StringComparison.OrdinalIgnoreCase))
            {
                plainParts.Add(decoded);
            }
            else if (string.Equals(part.MimeType, "text/html", StringComparison.OrdinalIgnoreCase))
            {
                htmlParts.Add(StripHtml(decoded));
            }
        });

        var selected = plainParts.Count > 0 ? plainParts : htmlParts;
        return NormalizeWhitespace(string.Join("\n\n", selected));
    }

    private static void WalkParts(GmailMessagePart? part, Action<GmailMessagePart> visit)
    {
        if (part is null)
        {
            return;
        }

        visit(part);

        if (part.Parts is null)
        {
            return;
        }

        foreach (var child in part.Parts)
        {
            WalkParts(child, visit);
        }
    }

    private static string DecodeBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }

    private static string StripHtml(string html)
    {
        var withoutBlocks = HtmlBlockRegex().Replace(html, "\n");
        var withoutTags = HtmlTagRegex().Replace(withoutBlocks, " ");
        return WebUtility.HtmlDecode(withoutTags);
    }

    private static string NormalizeWhitespace(string value)
    {
        return WhitespaceRegex().Replace(value, " ").Trim();
    }

    private static string Truncate(string value, int maxCharacters)
    {
        if (value.Length <= maxCharacters)
        {
            return value;
        }

        return value[..maxCharacters].TrimEnd() + "...";
    }

    [GeneratedRegex(@"<(br|/p|/div|/li|/tr|/h[1-6])\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlBlockRegex();

    [GeneratedRegex("<[^>]+>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    private sealed class GmailListMessagesResponse
    {
        [JsonPropertyName("messages")]
        public IReadOnlyList<GmailMessageReference> Messages { get; init; } = [];

        [JsonPropertyName("resultSizeEstimate")]
        public int ResultSizeEstimate { get; init; }
    }

    private sealed record GmailMessageReference(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("threadId")] string? ThreadId);

    private sealed class GmailThreadResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("messages")]
        public IReadOnlyList<GmailMessageResponse> Messages { get; init; } = [];
    }

    private sealed class GmailMessageResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("threadId")]
        public string? ThreadId { get; init; }

        [JsonPropertyName("labelIds")]
        public IReadOnlyList<string>? LabelIds { get; init; }

        [JsonPropertyName("snippet")]
        public string? Snippet { get; init; }

        [JsonPropertyName("internalDate")]
        public string? InternalDate { get; init; }

        [JsonPropertyName("payload")]
        public GmailMessagePart? Payload { get; init; }
    }

    private sealed class GmailMessagePart
    {
        [JsonPropertyName("mimeType")]
        public string? MimeType { get; init; }

        [JsonPropertyName("filename")]
        public string? FileName { get; init; }

        [JsonPropertyName("headers")]
        public IReadOnlyList<GmailHeader>? Headers { get; init; }

        [JsonPropertyName("body")]
        public GmailMessagePartBody? Body { get; init; }

        [JsonPropertyName("parts")]
        public IReadOnlyList<GmailMessagePart>? Parts { get; init; }
    }

    private sealed record GmailHeader(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("value")] string? Value);

    private sealed class GmailMessagePartBody
    {
        [JsonPropertyName("data")]
        public string? Data { get; init; }

        [JsonPropertyName("attachmentId")]
        public string? AttachmentId { get; init; }

        [JsonPropertyName("size")]
        public long? Size { get; init; }
    }

    private sealed record GmailModifyRequest(
        [property: JsonPropertyName("addLabelIds")] IReadOnlyList<string> AddLabelIds,
        [property: JsonPropertyName("removeLabelIds")] IReadOnlyList<string> RemoveLabelIds);

}
