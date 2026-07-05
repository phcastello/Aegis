using Aegis.Application.Common;
using Aegis.Application.Llm;
using Aegis.Domain;
using Aegis.Domain.Entities;
using System.Globalization;
using System.Text;

namespace Aegis.Application.Chat;

public sealed class ConversationTitleService(
    IAegisDbContext dbContext,
    ILlmClient llmClient) : IConversationTitleService
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
            !conversation.CanGenerateAutomaticTitle ||
            conversation.Messages.Count(message => message.Role == ChatRoles.User) != 1)
        {
            return;
        }

        var completion = await llmClient.GenerateAsync(
            BuildTitlePrompt(job.UserContent, job.AssistantContent),
            cancellationToken);
        var title = SanitizeGeneratedTitle(completion.Content, job.UserContent);
        conversation.SetGeneratedTitle(title);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string BuildTitlePrompt(string userContent, string assistantContent)
    {
        return $"""
            Gere um título curto em português brasileiro para esta conversa.

            Regras:
            - Máximo de 50 caracteres.
            - Não use aspas.
            - Não use ponto final.
            - Não use emoji.
            - Não explique.
            - Nomeie o assunto da conversa, não o formato da resposta.
            - Não mencione instruções de estilo, tamanho ou formato da resposta.
            - Não use expressões como "breve resposta", "resposta curta", "explicação", "ajuda", "pergunta", "conversa" ou semelhantes.
            - Retorne apenas o título.

            Exemplos:
            Ruim: Olá Aegis breve resposta
            Bom: Saudação inicial

            Ruim: Resposta curta sobre histórico
            Bom: Histórico de conversas

            Mensagem inicial do usuário:
            {LimitForTitlePrompt(userContent, TitlePromptUserLimit)}

            Primeira resposta da Aegis:
            {LimitForTitlePrompt(assistantContent, TitlePromptAssistantLimit)}
            """;
    }

    private static string LimitForTitlePrompt(string content, int maxLength)
    {
        var normalized = string.Join(' ', content.Split(default(string[]), StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength].TrimEnd();
    }

    private static string SanitizeGeneratedTitle(string generatedTitle, string fallbackContent)
    {
        var firstLine = generatedTitle
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? string.Empty;
        var title = RemoveEmojiLikeSymbols(firstLine)
            .Replace("\"", string.Empty, StringComparison.Ordinal)
            .Replace("'", string.Empty, StringComparison.Ordinal)
            .Trim()
            .TrimEnd('.');

        if (string.IsNullOrWhiteSpace(title))
        {
            title = RemoveEmojiLikeSymbols(fallbackContent).Trim();
        }

        title = string.Join(' ', title.Split(default(string[]), StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(title))
        {
            return Conversation.DefaultTitle;
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
