using Aegis.Application.Common;
using Aegis.Domain.Entities;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Aegis.Application.Chat;

public sealed class ConversationTitleService(
    IAegisDbContext dbContext,
    ILocalTitleGenerator titleGenerator) : IConversationTitleService
{
    private const int TitlePromptUserLimit = 600;
    private const int TitlePromptAssistantLimit = 900;

    public async Task GenerateTitleAsync(
        ConversationTitleJob job,
        CancellationToken cancellationToken = default)
    {
        var conversation = await dbContext.GetConversationWithMessagesAsync(
            job.ConversationId,
            cancellationToken);
        if (conversation is null ||
            !conversation.CanGenerateAutomaticTitle)
        {
            return;
        }

        var generatedTitle = await titleGenerator.GenerateAsync(
            LimitForTitlePrompt(job.UserContent, TitlePromptUserLimit),
            LimitForTitlePrompt(job.AssistantContent, TitlePromptAssistantLimit),
            cancellationToken);
        var title = SanitizeGeneratedTitle(generatedTitle);
        if (title is null)
        {
            if (generatedTitle is not null)
            {
                conversation.RecordTitleGenerationRawResponse(generatedTitle);
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            return;
        }

        conversation.SetGeneratedTitle(title, generatedTitle);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string LimitForTitlePrompt(string content, int maxLength)
    {
        var normalized = string.Join(' ', content.Split((string[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength].TrimEnd();
    }

    private static string? SanitizeGeneratedTitle(string? generatedTitle)
    {
        var firstLine = (generatedTitle ?? string.Empty)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? string.Empty;
        var title = RemoveEmojiLikeSymbols(firstLine)
            .Replace("\"", string.Empty, StringComparison.Ordinal)
            .Replace("'", string.Empty, StringComparison.Ordinal)
            .Trim()
            .TrimEnd('.');
        title = Regex.Replace(
            title,
            @"^\s*t[íi]tulo\s*[:\-]\s*",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        title = string.Join(' ', title.Split((string[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        return title.Length <= Conversation.MaxGeneratedTitleLength
            ? title
            : title[..Conversation.MaxGeneratedTitleLength].TrimEnd();
    }

    private static string RemoveEmojiLikeSymbols(string content)
    {
        var builder = new StringBuilder(content.Length);
        foreach (var rune in content.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.OtherSymbol or UnicodeCategory.Surrogate)
            {
                continue;
            }

            builder.Append(rune);
        }

        return builder.ToString();
    }
}
