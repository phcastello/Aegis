using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Aegis.Application.Email;
using Aegis.Domain;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aegis.Infrastructure.Email;

public sealed partial class GmailService(
    AegisDbContext dbContext,
    HttpClient httpClient,
    IOptions<GmailOptions> options,
    EmailTokenProtector tokenProtector) : IEmailService
{
    private const int DefaultSearchLimit = 10;
    private const int MaxSearchLimit = 50;
    private const int MaxModificationCount = 100;
    private const int ModificationChunkSize = 20;
    private const int MaxThreadMessages = 15;

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
            throw new ArgumentException("At least one email id is required.", nameof(emailIds));
        }

        if (normalizedIds.Count > MaxModificationCount)
        {
            throw new InvalidOperationException($"Cannot modify more than {MaxModificationCount} emails at once.");
        }

        var accessToken = await GetAccessTokenAsync(cancellationToken);
        var modifiedCount = 0;

        foreach (var chunk in normalizedIds.Chunk(ModificationChunkSize))
        {
            foreach (var emailId in chunk)
            {
                await SendGmailAsync<GmailMessageResponse>(
                    HttpMethod.Post,
                    $"https://gmail.googleapis.com/gmail/v1/users/me/messages/{Uri.EscapeDataString(emailId)}/modify",
                    accessToken,
                    new GmailModifyRequest(addLabels, removeLabels),
                    cancellationToken);
                modifiedCount++;
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

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var connection = await dbContext.EmailAccountConnections
            .Where(item =>
                item.Provider == EmailAccountConnection.GmailProvider &&
                item.DisconnectedAt == null)
            .OrderByDescending(item => item.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (connection is null)
        {
            throw new EmailNotConnectedException();
        }

        if (connection.AccessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            try
            {
                return tokenProtector.Unprotect(connection.AccessTokenEncrypted);
            }
            catch (CryptographicException)
            {
                connection.Disconnect();
                await dbContext.SaveChangesAsync(cancellationToken);
                throw new EmailNotConnectedException();
            }
        }

        return await RefreshAccessTokenAsync(connection, cancellationToken);
    }

    private async Task<string> RefreshAccessTokenAsync(
        EmailAccountConnection connection,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connection.RefreshTokenEncrypted))
        {
            connection.Disconnect();
            await dbContext.SaveChangesAsync(cancellationToken);
            throw new EmailNotConnectedException();
        }

        var gmailOptions = options.Value;
        string refreshToken;
        try
        {
            refreshToken = tokenProtector.Unprotect(connection.RefreshTokenEncrypted);
        }
        catch (CryptographicException)
        {
            connection.Disconnect();
            await dbContext.SaveChangesAsync(cancellationToken);
            throw new EmailNotConnectedException();
        }

        using var response = await httpClient.PostAsync(
            "https://oauth2.googleapis.com/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = gmailOptions.ClientId ?? string.Empty,
                ["client_secret"] = gmailOptions.ClientSecret ?? string.Empty,
                ["refresh_token"] = refreshToken,
                ["grant_type"] = "refresh_token"
            }),
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            connection.Disconnect();
            await dbContext.SaveChangesAsync(cancellationToken);
            throw new EmailNotConnectedException();
        }

        var tokens = await response.Content.ReadFromJsonAsync<GoogleTokenResponse>(
            cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Google returned an empty refresh response.");

        if (string.IsNullOrWhiteSpace(tokens.AccessToken))
        {
            connection.Disconnect();
            await dbContext.SaveChangesAsync(cancellationToken);
            throw new EmailNotConnectedException();
        }

        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, tokens.ExpiresIn));
        connection.UpdateAccessToken(tokenProtector.Protect(tokens.AccessToken), expiresAt);
        await dbContext.SaveChangesAsync(cancellationToken);

        return tokens.AccessToken;
    }

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
            throw new InvalidOperationException("Gmail item was not found.");
        }

        response.EnsureSuccessStatusCode();
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

    private sealed class GoogleTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; init; }
    }
}
