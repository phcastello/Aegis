using Aegis.Application.Common;
using Aegis.Application.Llm;
using Aegis.Application.Models;
using Aegis.Application.Prompts;
using Aegis.Application.Tools;
using Aegis.Application.Email;
using Aegis.Application.Turns;
using Aegis.Domain;
using Aegis.Domain.Entities;
using System.Runtime.CompilerServices;
using System.Text;

namespace Aegis.Application.Chat;

public sealed class ChatService(
    IAegisDbContext dbContext,
    IPromptBuilder promptBuilder,
    IAegisModelClient modelClient,
    IAegisToolLoop toolLoop,
    IEmailToolContextService emailContextService,
    AegisModelRouter modelRouter,
    IConversationTitleJobQueue titleJobQueue,
    IActiveTurnRegistry turnRegistry) : IChatService
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
        var turn = turnRegistry.Register(request.TurnId ?? Guid.NewGuid(), conversation.Id);
        turnRegistry.TryTransition(turn.TurnId, TurnStatus.Created, TurnStatus.GeneratingText);
        using var turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, turn.Cancellation.Token);
        var turnToken = turnCancellation.Token;
        var userMessage = conversation.AddMessage(ChatRoles.User, userContent);
        turn.UserMessageId = userMessage.Id;
        dbContext.AddChatMessage(userMessage);

        await dbContext.SaveChangesAsync(turnToken);

        var recentHistory = await dbContext.GetRecentMessagesAsync(
            conversation.Id,
            RecentHistoryLimit + 1,
            turnToken);

        var promptResult = await promptBuilder.BuildPromptAsync(
            recentHistory.Where(message => message.Id != userMessage.Id).ToList(),
            userContent,
            turnToken);

        try
        {
            var chatContext = await CreateChatRequestContextAsync(
                conversation.Id,
                userContent,
                recentHistory,
                turnToken);
            var purpose = modelRouter.ChoosePurpose(chatContext);
            var useTools = modelRouter.RequiresTools(chatContext);
            var modelRequest = CreateModelRequest(promptResult, userContent, purpose);
            var completion = useTools
                ? await RunToolCompletionAsync(modelRequest, conversation.Id, userMessage.Id, userContent, turnToken)
                : await modelClient.GenerateAsync(modelRequest, turnToken);
            EnsureCurrent(turn);
            var assistantMessage = conversation.AddMessage(ChatRoles.Assistant, completion.Content);
            if (!turnRegistry.TrySetTextCompleted(turn.TurnId, assistantMessage.Id))
            {
                throw new OperationCanceledException(turnToken);
            }
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

            await dbContext.SaveChangesAsync(CancellationToken.None);
            if (turnRegistry.IsCurrent(conversation.Id, turn.TurnId))
            {
                await QueueConversationTitleGenerationAsync(conversation, CancellationToken.None);
            }

            return new SendMessageResponse(
                conversation.Id,
                conversation.Title,
                conversation.TitleSource,
                MapMessage(assistantMessage));
        }
        catch (OperationCanceledException) when (turnToken.IsCancellationRequested)
        {
            throw;
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

        if (request.TurnId is not { } turnId || turnId == Guid.Empty)
        {
            throw new ArgumentException("A valid turnId is required for streaming chat.", nameof(request));
        }

        var userContent = request.Content.Trim();
        var conversation = await GetOrCreateConversationAsync(request.ConversationId, cancellationToken);
        var turn = turnRegistry.Register(turnId, conversation.Id);
        if (!turnRegistry.TryTransition(turn.TurnId, TurnStatus.Created, TurnStatus.GeneratingText))
        {
            throw new OperationCanceledException(turn.Cancellation.Token);
        }

        using var turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, turn.Cancellation.Token);
        var turnToken = turnCancellation.Token;
        var userMessage = conversation.AddMessage(ChatRoles.User, userContent);
        turn.UserMessageId = userMessage.Id;
        dbContext.AddChatMessage(userMessage);
        await dbContext.SaveChangesAsync(turnToken);
        yield return ChatStreamEvent.Conversation(turn.TurnId, conversation.Id);

        var recentHistory = await dbContext.GetRecentMessagesAsync(conversation.Id, RecentHistoryLimit + 1, turnToken);
        var promptResult = await promptBuilder.BuildPromptAsync(
            recentHistory.Where(message => message.Id != userMessage.Id).ToList(), userContent, turnToken);
        var chatContext = await CreateChatRequestContextAsync(conversation.Id, userContent, recentHistory, turnToken);
        var modelRequest = CreateModelRequest(promptResult, userContent, modelRouter.ChoosePurpose(chatContext));
        IAsyncEnumerable<ModelStreamChunk> chunks = modelRouter.RequiresTools(chatContext)
            ? toolLoop.StreamAsync(modelRequest with { Purpose = ModelPurpose.Main }, new ToolExecutionContext(conversation.Id, userMessage.Id, userContent), turnToken)
            : modelClient.StreamAsync(modelRequest, turnToken);

        var content = new StringBuilder();
        await foreach (var chunk in chunks.WithCancellation(turnToken))
        {
            EnsureCurrent(turn);
            if (!string.IsNullOrEmpty(chunk.Content))
            {
                content.Append(chunk.Content);
                yield return ChatStreamEvent.Token(turn.TurnId, chunk.Content);
            }

            if (!chunk.IsDone)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(content.ToString()) || string.IsNullOrWhiteSpace(chunk.Model) || chunk.AuditData is null)
            {
                throw new InvalidOperationException("Model streaming ended without a complete response.");
            }

            EnsureCurrent(turn);
            var assistantMessage = conversation.AddMessage(ChatRoles.Assistant, content.ToString());
            if (!turnRegistry.TrySetTextCompleted(turn.TurnId, assistantMessage.Id))
            {
                throw new OperationCanceledException(turnToken);
            }

            dbContext.AddChatMessage(assistantMessage);
            assistantMessage.AttachAuditData(chunk.Model, promptResult.Prompt, promptResult.RuntimeContext, chunk.MetadataJson);
            dbContext.AddLlmRequestAudit(CreateLlmRequestAudit(conversation.Id, userMessage.Id, assistantMessage.Id, chunk.AuditData));
            await dbContext.SaveChangesAsync(CancellationToken.None);
            if (turnRegistry.IsCurrent(conversation.Id, turn.TurnId))
            {
                await QueueConversationTitleGenerationAsync(conversation, CancellationToken.None);
                yield return ChatStreamEvent.Done(turn.TurnId, conversation.Id, assistantMessage.Id, conversation.Title, conversation.TitleSource);
            }
            yield break;
        }

        throw new InvalidOperationException("Model streaming ended before a final event was received.");
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

    private async Task<ChatRequestContext> CreateChatRequestContextAsync(
        Guid conversationId,
        string userContent,
        IReadOnlyList<ChatMessage> recentHistory,
        CancellationToken cancellationToken)
    {
        var hasPendingAction = await dbContext.GetLatestOpenPendingEmailActionAsync(
            conversationId,
            cancellationToken) is not null;
        var hasRecentEmailContext = await emailContextService.HasRecentEmailContextAsync(
            conversationId,
            cancellationToken);
        var hasRecentEmailHistory = recentHistory
            .OrderByDescending(message => message.CreatedAt)
            .Take(8)
            .Any(message => ContainsAny(message.Content,
            [
                "email",
                "gmail",
                "briefing",
                "não lido",
                "nao lido",
                "lido",
                "marcar",
                "conexão",
                "conexao"
            ]));

        return new ChatRequestContext(
            userContent,
            HasPendingAction: hasPendingAction,
            HasRecentToolContext: hasRecentEmailContext || hasRecentEmailHistory);
    }

    private async Task<ModelCompletionResponse> RunToolCompletionAsync(
        ModelRequest request,
        Guid conversationId,
        Guid userMessageId,
        string userContent,
        CancellationToken cancellationToken)
    {
        var response = await toolLoop.RunAsync(
            request with { Purpose = ModelPurpose.Main },
            new ToolExecutionContext(conversationId, userMessageId, userContent),
            cancellationToken);

        return new ModelCompletionResponse(
            response.Content,
            response.Provider,
            response.Model,
            response.Purpose,
            response.MetadataJson,
            response.AuditData);
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
                ["aegis_version"] = "0.3.0",
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

    private static bool ContainsAny(string value, IReadOnlyList<string> terms)
    {
        return terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private void EnsureCurrent(ActiveTurn turn)
    {
        if (!turnRegistry.IsCurrent(turn.ConversationId, turn.TurnId))
        {
            throw new OperationCanceledException(turn.Cancellation.Token);
        }
    }
}
