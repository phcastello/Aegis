using Aegis.Application.Common;
using Aegis.Application.Llm;
using Aegis.Application.Prompts;
using Aegis.Domain;
using Aegis.Domain.Entities;
using System.Runtime.CompilerServices;
using System.Text;

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

        try
        {
            var completion = await llmClient.GenerateAsync(promptResult.Prompt, cancellationToken);
            var assistantMessage = conversation.AddMessage(ChatRoles.Assistant, completion.Content);
            dbContext.AddChatMessage(assistantMessage);
            assistantMessage.AttachAuditData(
                completion.Model,
                promptResult.Prompt,
                promptResult.RuntimeContext,
                completion.MetadataJson);
            dbContext.AddLlmRequestAudit(CreateLlmRequestAudit(
                conversation.Id,
                userMessage.Id,
                assistantMessage.Id,
                completion.AuditData));

            await dbContext.SaveChangesAsync(cancellationToken);

            return new SendMessageResponse(conversation.Id, MapMessage(assistantMessage));
        }
        catch (LlmRequestException exception)
        {
            dbContext.AddLlmRequestAudit(CreateLlmRequestAudit(
                conversation.Id,
                userMessage.Id,
                null,
                exception.AuditData));

            await dbContext.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    public async IAsyncEnumerable<ChatStreamEvent> StreamMessageAsync(
        SendMessageRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
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

        yield return ChatStreamEvent.Conversation(conversation.Id);

        var recentHistory = await dbContext.GetRecentMessagesAsync(
            conversation.Id,
            RecentHistoryLimit + 1,
            cancellationToken);

        var promptResult = await promptBuilder.BuildPromptAsync(
            recentHistory.Where(message => message.Id != userMessage.Id).ToList(),
            userContent,
            cancellationToken);

        var content = new StringBuilder();
        await using var stream = llmClient
            .StreamCompletionAsync(promptResult.Prompt, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            LlmStreamChunk chunk;
            try
            {
                if (!await stream.MoveNextAsync())
                {
                    throw new InvalidOperationException(
                        "Ollama streaming ended before a final event was received.");
                }

                chunk = stream.Current;
            }
            catch (LlmRequestException exception)
            {
                dbContext.AddLlmRequestAudit(CreateLlmRequestAudit(
                    conversation.Id,
                    userMessage.Id,
                    null,
                    exception.AuditData));

                await dbContext.SaveChangesAsync(CancellationToken.None);
                throw;
            }

            if (!string.IsNullOrEmpty(chunk.Content))
            {
                content.Append(chunk.Content);
                yield return ChatStreamEvent.Token(chunk.Content);
            }

            if (!chunk.IsDone)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(content.ToString()) ||
                string.IsNullOrWhiteSpace(chunk.Model) ||
                chunk.AuditData is null)
            {
                throw new InvalidOperationException("Ollama streaming ended without a complete response.");
            }

            var assistantMessage = conversation.AddMessage(ChatRoles.Assistant, content.ToString());
            dbContext.AddChatMessage(assistantMessage);
            assistantMessage.AttachAuditData(
                chunk.Model,
                promptResult.Prompt,
                promptResult.RuntimeContext,
                chunk.MetadataJson);
            dbContext.AddLlmRequestAudit(CreateLlmRequestAudit(
                conversation.Id,
                userMessage.Id,
                assistantMessage.Id,
                chunk.AuditData));

            await dbContext.SaveChangesAsync(cancellationToken);

            yield return ChatStreamEvent.Done(conversation.Id, assistantMessage.Id);
            yield break;
        }
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

    private static LlmRequestAudit CreateLlmRequestAudit(
        Guid conversationId,
        Guid userMessageId,
        Guid? assistantMessageId,
        LlmRequestAuditData auditData)
    {
        return new LlmRequestAudit(
            conversationId,
            userMessageId,
            assistantMessageId,
            auditData.Provider,
            auditData.Model,
            auditData.Success,
            auditData.DurationMilliseconds,
            auditData.RequestPayloadJson,
            auditData.HttpStatusCode,
            auditData.ResponseBody,
            auditData.FailureReason,
            auditData.ErrorType);
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
