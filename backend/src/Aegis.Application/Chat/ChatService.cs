using Aegis.Application.Common;
using Aegis.Application.Llm;
using Aegis.Application.Models;
using Aegis.Application.Prompts;
using Aegis.Domain;
using Aegis.Domain.Entities;
using System.Runtime.CompilerServices;
using System.Text;

namespace Aegis.Application.Chat;

public sealed class ChatService(
    IAegisDbContext dbContext,
    IPromptBuilder promptBuilder,
    IAegisModelClient modelClient,
    AegisModelRouter modelRouter,
    IConversationTitleJobQueue titleJobQueue) : IChatService
{
    private const int RecentHistoryLimit = 20;
    private const int DefaultConversationSummaryLimit = 30;
    private const int MaxConversationSummaryLimit = 100;

    public async Task<SendMessageResponse> SendMessageAsync(
        SendMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Content))
        {
            throw new ArgumentException("Message content cannot be empty.", nameof(request));
        }

        var userContent = request.Content.Trim();
        var conversation = await GetOrCreateConversationAsync(request.ConversationId, cancellationToken);
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
            var purpose = modelRouter.ChoosePurpose(new ChatRequestContext(userContent));
            var completion = await modelClient.GenerateAsync(
                CreateModelRequest(promptResult, userContent, purpose),
                cancellationToken);
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
            await QueueConversationTitleGenerationAsync(conversation, CancellationToken.None);

            return new SendMessageResponse(
                conversation.Id,
                conversation.Title,
                conversation.TitleSource,
                MapMessage(assistantMessage));
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
        var conversation = await GetOrCreateConversationAsync(request.ConversationId, cancellationToken);
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
        var purpose = modelRouter.ChoosePurpose(new ChatRequestContext(userContent));
        await using var stream = modelClient
            .StreamAsync(CreateModelRequest(promptResult, userContent, purpose), cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            ModelStreamChunk chunk;
            try
            {
                if (!await stream.MoveNextAsync())
                {
                    throw new InvalidOperationException(
                        "Model streaming ended before a final event was received.");
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
                throw new InvalidOperationException("Model streaming ended without a complete response.");
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
            await QueueConversationTitleGenerationAsync(conversation, CancellationToken.None);

            yield return ChatStreamEvent.Done(
                conversation.Id,
                assistantMessage.Id,
                conversation.Title,
                conversation.TitleSource);
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
            conversation.TitleSource,
            conversation.Messages
                .OrderBy(message => message.CreatedAt)
                .ThenBy(message => message.Id)
                .Select(MapMessage)
                .ToList());
    }

    public async Task<ConversationPageResponse> GetRecentConversationsAsync(
        int limit = DefaultConversationSummaryLimit,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        if (!ConversationCursor.TryDecode(cursor, out var decodedCursor))
        {
            throw new ArgumentException("Invalid cursor.", nameof(cursor));
        }

        var normalizedLimit = Math.Clamp(limit, 1, MaxConversationSummaryLimit);
        var conversations = await dbContext.GetRecentConversationSummariesAsync(
            normalizedLimit + 1,
            decodedCursor,
            cancellationToken);
        var hasMore = conversations.Count > normalizedLimit;
        var pageItems = conversations.Take(normalizedLimit).ToList();
        var nextCursor = hasMore && pageItems.Count > 0
            ? new ConversationCursor(pageItems[^1].UpdatedAt, pageItems[^1].Id).Encode()
            : null;

        var items = pageItems
            .Select(conversation =>
            {
                return MapConversationSummary(conversation);
            })
            .ToList();

        return new ConversationPageResponse(items, nextCursor, hasMore);
    }

    public async Task<ConversationSummaryResponse?> RenameConversationAsync(
        Guid conversationId,
        string? title,
        CancellationToken cancellationToken = default)
    {
        var conversation = await dbContext.GetConversationWithMessagesAsync(conversationId, cancellationToken);
        if (conversation is null)
        {
            return null;
        }

        conversation.Rename(title ?? string.Empty);
        await dbContext.SaveChangesAsync(cancellationToken);

        return MapConversationSummary(conversation);
    }

    public async Task<bool> DeleteConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        var conversation = await dbContext.GetConversationWithMessagesAsync(conversationId, cancellationToken);
        if (conversation is null)
        {
            return false;
        }

        conversation.Delete();
        await dbContext.SaveChangesAsync(cancellationToken);

        return true;
    }

    private async Task<Conversation> GetOrCreateConversationAsync(
        Guid? conversationId,
        CancellationToken cancellationToken)
    {
        if (conversationId is null)
        {
            var conversation = new Conversation(Conversation.DefaultTitle);
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

    private static ConversationSummaryResponse MapConversationSummary(Conversation conversation)
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
            conversation.TitleSource,
            conversation.Messages.Count,
            CreatePreview(lastMessage?.Content));
    }

    private static ConversationSummaryResponse MapConversationSummary(ConversationSummaryData conversation)
    {
        return new ConversationSummaryResponse(
            conversation.Id,
            conversation.Title,
            conversation.CreatedAt,
            conversation.UpdatedAt,
            conversation.TitleSource,
            conversation.MessageCount,
            CreatePreview(conversation.LastMessageContent));
    }

    private async Task QueueConversationTitleGenerationAsync(
        Conversation conversation,
        CancellationToken cancellationToken)
    {
        if (!conversation.CanGenerateAutomaticTitle)
        {
            return;
        }

        var firstUserMessage = conversation.Messages
            .Where(message => message.Role == ChatRoles.User)
            .OrderBy(message => message.CreatedAt)
            .ThenBy(message => message.Id)
            .FirstOrDefault();
        if (firstUserMessage is null)
        {
            return;
        }

        var firstAssistantMessage = conversation.Messages
            .Where(message => message.Role == ChatRoles.Assistant)
            .OrderBy(message => message.CreatedAt)
            .ThenBy(message => message.Id)
            .FirstOrDefault();
        if (firstAssistantMessage is null)
        {
            return;
        }

        await titleJobQueue.EnqueueAsync(
            new ConversationTitleJob(
                conversation.Id,
                firstUserMessage.Content,
                firstAssistantMessage.Content),
            cancellationToken);
    }

    private static ModelRequest CreateModelRequest(
        PromptBuildResult promptResult,
        string userContent,
        ModelPurpose purpose)
    {
        return new ModelRequest(
            promptResult.Prompt,
            userContent,
            purpose,
            new Dictionary<string, string>
            {
                ["aegis_version"] = "0.2.0",
                ["purpose"] = purpose.ToString()
            });
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
