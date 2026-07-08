using System.Text.Json;
using System.Text.Json.Serialization;
using Aegis.Application.Common;
using Aegis.Domain.Entities;

namespace Aegis.Application.Email;

public interface IEmailToolContextService
{
    Task RememberSearchAsync(
        Guid conversationId,
        IReadOnlyList<EmailSummaryData> emails,
        string sourceToolName,
        CancellationToken cancellationToken = default);

    Task RememberEmailAsync(
        Guid conversationId,
        EmailContentData email,
        string sourceToolName,
        CancellationToken cancellationToken = default);

    Task RememberThreadAsync(
        Guid conversationId,
        ThreadData thread,
        string sourceToolName,
        CancellationToken cancellationToken = default);

    Task<EmailContextResolution> ResolveAsync(
        Guid conversationId,
        IReadOnlyList<string> emailIds,
        string? selectionKey,
        CancellationToken cancellationToken = default);

    Task<bool> HasRecentEmailContextAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);

    Task RememberModifiedAttemptAsync(
        Guid conversationId,
        IReadOnlyList<string> emailIds,
        string humanSummary,
        string sourceToolName,
        CancellationToken cancellationToken = default);
}

public sealed class EmailToolContextService(IAegisDbContext dbContext) : IEmailToolContextService
{
    public const string Scope = "email";
    public const string ItemEntryType = "email_item";
    public const string SelectionEntryType = "email_selection";
    public const string LastSearchSelection = "last_search";
    public const string LastModifiedAttemptSelection = "last_modified_attempt";

    private static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(30);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task RememberSearchAsync(
        Guid conversationId,
        IReadOnlyList<EmailSummaryData> emails,
        string sourceToolName,
        CancellationToken cancellationToken = default)
    {
        await RememberItemsAsync(
            conversationId,
            emails.Select(EmailContextItem.FromSummary).ToList(),
            sourceToolName,
            cancellationToken);

        await RememberSelectionAsync(
            conversationId,
            LastSearchSelection,
            "Última busca de emails",
            emails.Select(email => email.Id).ToList(),
            sourceToolName,
            cancellationToken);
    }

    public async Task RememberEmailAsync(
        Guid conversationId,
        EmailContentData email,
        string sourceToolName,
        CancellationToken cancellationToken = default)
    {
        await RememberItemsAsync(
            conversationId,
            [EmailContextItem.FromContent(email)],
            sourceToolName,
            cancellationToken);
    }

    public async Task RememberThreadAsync(
        Guid conversationId,
        ThreadData thread,
        string sourceToolName,
        CancellationToken cancellationToken = default)
    {
        await RememberItemsAsync(
            conversationId,
            thread.Messages.Select(EmailContextItem.FromContent).ToList(),
            sourceToolName,
            cancellationToken);
    }

    public async Task<EmailContextResolution> ResolveAsync(
        Guid conversationId,
        IReadOnlyList<string> emailIds,
        string? selectionKey,
        CancellationToken cancellationToken = default)
    {
        var normalizedSelectionKey = string.IsNullOrWhiteSpace(selectionKey) ? null : selectionKey.Trim();
        var requestedIds = emailIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (requestedIds.Count == 0 && normalizedSelectionKey is not null)
        {
            var selectionEntries = await dbContext.GetActiveToolContextEntriesAsync(
                conversationId,
                Scope,
                SelectionEntryType,
                normalizedSelectionKey,
                cancellationToken);
            var selection = selectionEntries
                .Select(entry => Deserialize<EmailContextSelection>(entry.DataJson))
                .FirstOrDefault(selection => selection is not null);
            if (selection is not null)
            {
                requestedIds = selection.EmailIds
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
            }
        }

        if (requestedIds.Count == 0)
        {
            return EmailContextResolution.Invalid(
                "Nenhum email real foi informado para a ação.",
                await GetAvailableSelectionKeysAsync(conversationId, cancellationToken));
        }

        var activeItems = await dbContext.GetActiveToolContextEntriesAsync(
            conversationId,
            Scope,
            ItemEntryType,
            cancellationToken: cancellationToken);
        var knownIds = activeItems
            .Select(entry => Deserialize<EmailContextItem>(entry.DataJson))
            .Where(item => item is not null)
            .Select(item => item!)
            .GroupBy(item => item.EmailId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var invalidIds = requestedIds
            .Where(id => IsPlaceholder(id) || !knownIds.ContainsKey(id))
            .ToList();
        if (invalidIds.Count > 0)
        {
            return EmailContextResolution.Invalid(
                $"A ação contém IDs que não foram observados no contexto recente: {string.Join(", ", invalidIds)}.",
                await GetAvailableSelectionKeysAsync(conversationId, cancellationToken),
                invalidIds);
        }

        return EmailContextResolution.Valid(
            requestedIds,
            requestedIds.Select(id => knownIds[id]).ToList(),
            normalizedSelectionKey);
    }

    public async Task<bool> HasRecentEmailContextAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.HasRecentToolContextEntriesAsync(
            conversationId,
            Scope,
            DateTimeOffset.UtcNow.Subtract(TimeToLive),
            cancellationToken);
    }

    public async Task RememberModifiedAttemptAsync(
        Guid conversationId,
        IReadOnlyList<string> emailIds,
        string humanSummary,
        string sourceToolName,
        CancellationToken cancellationToken = default)
    {
        await RememberSelectionAsync(
            conversationId,
            LastModifiedAttemptSelection,
            humanSummary,
            emailIds,
            sourceToolName,
            cancellationToken);
    }

    private async Task RememberItemsAsync(
        Guid conversationId,
        IReadOnlyList<EmailContextItem> items,
        string sourceToolName,
        CancellationToken cancellationToken)
    {
        foreach (var item in items.Where(item => !string.IsNullOrWhiteSpace(item.EmailId)))
        {
            await dbContext.ReplaceActiveToolContextEntriesAsync(
                conversationId,
                Scope,
                ItemEntryType,
                item.EmailId,
                cancellationToken);
            dbContext.AddToolContextEntry(CreateEntry(
                conversationId,
                ItemEntryType,
                item.EmailId,
                item,
                sourceToolName));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task RememberSelectionAsync(
        Guid conversationId,
        string selectionKey,
        string humanSummary,
        IReadOnlyList<string> emailIds,
        string sourceToolName,
        CancellationToken cancellationToken)
    {
        var normalizedIds = emailIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (normalizedIds.Count == 0)
        {
            return;
        }

        await dbContext.ReplaceActiveToolContextEntriesAsync(
            conversationId,
            Scope,
            SelectionEntryType,
            selectionKey,
            cancellationToken);
        dbContext.AddToolContextEntry(CreateEntry(
            conversationId,
            SelectionEntryType,
            selectionKey,
            new EmailContextSelection(selectionKey, humanSummary, normalizedIds),
            sourceToolName));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<string>> GetAvailableSelectionKeysAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var entries = await dbContext.GetActiveToolContextEntriesAsync(
            conversationId,
            Scope,
            SelectionEntryType,
            cancellationToken: cancellationToken);
        return entries
            .Select(entry => entry.Key)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static ToolContextEntry CreateEntry(
        Guid conversationId,
        string entryType,
        string key,
        object data,
        string sourceToolName)
    {
        return new ToolContextEntry(
            conversationId,
            Scope,
            entryType,
            key,
            JsonSerializer.Serialize(data, JsonOptions),
            sourceToolName,
            DateTimeOffset.UtcNow.Add(TimeToLive));
    }

    private static T? Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static bool IsPlaceholder(string value)
    {
        return value.Contains("placeholder", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("example", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("TODO", StringComparison.OrdinalIgnoreCase) ||
            value.Contains(' ', StringComparison.Ordinal);
    }
}

public sealed record EmailContextItem(
    string EmailId,
    string ThreadId,
    string? From,
    string? To,
    string? Subject,
    DateTimeOffset? ReceivedAt,
    string? Snippet,
    IReadOnlyList<string> Labels,
    IReadOnlyList<EmailAttachmentData> Attachments,
    bool IsUnread,
    bool IsStarred,
    bool IsImportant)
{
    public static EmailContextItem FromSummary(EmailSummaryData email)
    {
        return new EmailContextItem(
            email.Id,
            email.ThreadId,
            email.From,
            email.To,
            email.Subject,
            email.ReceivedAt,
            email.Snippet,
            email.Labels,
            email.Attachments,
            email.IsUnread,
            email.IsStarred,
            email.IsImportant);
    }

    public static EmailContextItem FromContent(EmailContentData email)
    {
        return new EmailContextItem(
            email.Id,
            email.ThreadId,
            email.From,
            email.To,
            email.Subject,
            email.ReceivedAt,
            email.Snippet,
            email.Labels,
            email.Attachments,
            email.IsUnread,
            email.IsStarred,
            email.IsImportant);
    }
}

public sealed record EmailContextSelection(
    string SelectionKey,
    string HumanSummary,
    IReadOnlyList<string> EmailIds);

public sealed record EmailContextResolution(
    bool IsValid,
    IReadOnlyList<string> EmailIds,
    IReadOnlyList<EmailContextItem> Items,
    string? SelectionKey,
    string? ErrorMessage,
    IReadOnlyList<string> AvailableSelectionKeys,
    IReadOnlyList<string> InvalidEmailIds)
{
    public static EmailContextResolution Valid(
        IReadOnlyList<string> emailIds,
        IReadOnlyList<EmailContextItem> items,
        string? selectionKey)
    {
        return new EmailContextResolution(
            true,
            emailIds,
            items,
            selectionKey,
            null,
            [],
            []);
    }

    public static EmailContextResolution Invalid(
        string errorMessage,
        IReadOnlyList<string> availableSelectionKeys,
        IReadOnlyList<string>? invalidEmailIds = null)
    {
        return new EmailContextResolution(
            false,
            [],
            [],
            null,
            errorMessage,
            availableSelectionKeys,
            invalidEmailIds ?? []);
    }
}
