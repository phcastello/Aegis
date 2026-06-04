using Aegis.Application.Common;
using Aegis.Application.Llm;
using Aegis.Application.Prompts;
using Aegis.Domain;
using Aegis.Domain.Entities;

namespace Aegis.Application.Chat;

public sealed class ChatService(
    IAegisDbContext dbContext,
    IPromptBuilder promptBuilder,
    ILlmClient llmClient) : IChatService
{
    private const int RecentHistoryLimit = 20;
    private const int MaxConversationSummaryLimit = 50;

    public async Task<SendMessageResponse> SendMessageAsync(
        SendMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Content))
        {
            throw new ArgumentException("Message content cannot be empty.", nameof(request));
        }

        var userContent = request.Content.Trim();
        var conversation = await GetOrCreateConversationAsync(request.ConversationId, userContent, cancellationToken);
        var userMessage = conversation.AddMessage(ChatRoles.User, userContent);
        dbContext.AddChatMessage(userMessage);

        await dbContext.SaveChangesAsync(cancellationToken);

        var recentHistory = await dbContext.GetRecentMessagesAsync(
            conversation.Id,
            RecentHistoryLimit + 1,
            cancellationToken);

        var promptResult = await promptBuilder.BuildPromptAsync(
            recentHistory.Where(message => message.Id != userMessage.Id).ToList(),
            userContent,
            cancellationToken);

        var completion = await llmClient.GenerateAsync(promptResult.Prompt, cancellationToken);
        var assistantMessage = conversation.AddMessage(ChatRoles.Assistant, completion.Content);
        dbContext.AddChatMessage(assistantMessage);
        assistantMessage.AttachAuditData(
            completion.Model,
            promptResult.Prompt,
            promptResult.RuntimeContext,
            completion.MetadataJson);

        await dbContext.SaveChangesAsync(cancellationToken);

        return new SendMessageResponse(conversation.Id, MapMessage(assistantMessage));
    }

    public async Task<ConversationResponse?> GetConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        var conversation = await dbContext.GetConversationWithMessagesAsync(conversationId, cancellationToken);
        if (conversation is null)
        {
            return null;
        }

        return new ConversationResponse(
            conversation.Id,
            conversation.Title,
            conversation.CreatedAt,
            conversation.UpdatedAt,
            conversation.Messages
                .OrderBy(message => message.CreatedAt)
                .ThenBy(message => message.Id)
                .Select(MapMessage)
                .ToList());
    }

    public async Task<IReadOnlyList<ConversationSummaryResponse>> GetRecentConversationsAsync(
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var normalizedLimit = Math.Clamp(limit, 1, MaxConversationSummaryLimit);
        var conversations = await dbContext.GetRecentConversationsAsync(normalizedLimit, cancellationToken);

        return conversations
            .Select(conversation =>
            {
                var lastMessage = conversation.Messages
                    .OrderByDescending(message => message.CreatedAt)
                    .ThenByDescending(message => message.Id)
                    .FirstOrDefault();

                return new ConversationSummaryResponse(
                    conversation.Id,
                    conversation.Title,
                    conversation.CreatedAt,
                    conversation.UpdatedAt,
                    conversation.Messages.Count,
                    CreatePreview(lastMessage?.Content));
            })
            .ToList();
    }

    private async Task<Conversation> GetOrCreateConversationAsync(
        Guid? conversationId,
        string firstMessage,
        CancellationToken cancellationToken)
    {
        if (conversationId is null)
        {
            var conversation = new Conversation(CreateConversationTitle(firstMessage));
            dbContext.AddConversation(conversation);
            return conversation;
        }

        return await dbContext.GetConversationWithMessagesAsync(conversationId.Value, cancellationToken)
            ?? throw new ConversationNotFoundException(conversationId.Value);
    }

    private static ChatMessageResponse MapMessage(ChatMessage message)
    {
        return new ChatMessageResponse(
            message.Id,
            message.ConversationId,
            message.Role,
            message.Content,
            message.CreatedAt,
            message.Model);
    }

    private static string CreateConversationTitle(string content)
    {
        var normalized = string.Join(' ', content.Split(default(string[]), StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 80
            ? normalized
            : normalized[..77] + "...";
    }

    private static string? CreatePreview(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var normalized = string.Join(' ', content.Split(default(string[]), StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 140
            ? normalized
            : normalized[..137] + "...";
    }
}
