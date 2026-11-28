using Aegis.Application.Reminders;
using Aegis.Application.Common;
using Aegis.Application.Llm;
using Aegis.Application.Memory;
using Aegis.Application.Observability;
using Aegis.Application.Models;
using Aegis.Application.Prompts;
using Aegis.Application.Tools;
using Aegis.Application.Turns;
using Aegis.Application.Voice;
using Aegis.Domain;
using Aegis.Domain.Entities;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Aegis.Application.Chat;

public sealed class ChatService(
    IAegisDbContext dbContext,
    IPromptBuilder promptBuilder,
    IAegisToolLoop toolLoop,
    IConversationTitleJobQueue titleJobQueue,
    IActiveTurnRegistry turnRegistry,
    IVoiceService voiceService,
    ReminderService? reminders = null,
    MemoryService? memory = null,
    MemoryAutomaticOptions? automaticMemory = null,
    TimeProvider? clock = null,
    AegisMetrics? metrics = null,
    MemoryHybridRetriever? hybrid = null,
    IMemoryActivityStore? activity = null,
    ILogger<ChatService>? logger = null,
    IResearchDelegationQueue? researchQueue = null,
    VoiceInterruptionReceipts? interruptionReceipts = null,
    IScriptWorkspaceCleanup? scriptWorkspaces = null) : IChatService
{
    private const int DefaultConversationSummaryLimit = 30;
    private const int MaxConversationSummaryLimit = 100;
    private const string PriorSpeechLabel = "Pedido anterior interrompido: ";
    private const string CurrentSpeechLabel = "\nNova fala (complemento ou correção): ";

    public async Task<SendMessageResponse?> SendMessageAsync(
        SendMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!request.HasMessage)
        {
            throw new ArgumentException("Message content cannot be empty.", nameof(request));
        }

        var effectiveTurnId = request.TurnId ?? Guid.NewGuid();
        using var profile = TurnLatencyProfile.Begin(effectiveTurnId, logger);
        var userContent = HandsFreeReply.UserContent(request);
        var acceptedInterruption = TakeInterruptionReceipt(request, userContent);
        var defer = request.IsHandsFree && request.AllowSilentIgnore && !acceptedInterruption;
        if (defer && userContent.Length == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        using var pending = await PrepareTurnAsync(request, userContent, cancellationToken);
        if (defer || acceptedInterruption) pending.Track(turnRegistry, effectiveTurnId, request);
        var conversation = pending.Conversation;
        var userMessage = pending.UserMessage;
        userContent = pending.ModelContent;
        var turnToken = pending.Token;
        if (!defer) await AcceptTurnAsync(pending, request, effectiveTurnId, turnToken);

        var promptResult = await BuildPromptForTurnAsync(
            conversation.Messages.Where(message => message.Id != userMessage.Id)
                .OrderBy(message => message.CreatedAt).ThenBy(message => message.Id).ToList(),
            userContent, conversation.Id, request.IsHandsFree, userMessage, pending.Attachments, turnToken);
        promptResult = HandsFreeReply.AddOperationalContext(promptResult, request);

        try
        {
            await using var artifacts = new ScriptArtifacts();
            var modelRequest = CreateModelRequest(promptResult, userContent, request);
            var completion = await TurnLatencyProfile.MeasureAsync("chat.model_and_tools", () =>
                RunToolCompletionAsync(modelRequest, conversation.Id, userMessage.Id, userContent, request, promptResult.AttachmentReader, artifacts, turnToken,
                    defer ? ct => AcceptTurnAsync(pending, request, effectiveTurnId, ct) : null));
            if (completion.Ignored)
            {
                if (!defer || pending.Turn is not null) throw new InvalidOperationException("Unexpected ignored turn.");
                pending.MarkIgnored();
                turnToken.ThrowIfCancellationRequested();
                return null;
            }
            var turn = pending.Turn ?? throw new InvalidOperationException("Voice turn was not accepted.");
            EnsureCurrent(turn);
            TurnLatencyProfile.Mark("chat.text_ready", completion.Content.Length);
            var finalContent = completion.Content;
            var assistantMessage = conversation.AddMessage(ChatRoles.Assistant, finalContent);
            if (!turnRegistry.TrySetTextCompleted(turn.TurnId, assistantMessage.Id))
            {
                throw new OperationCanceledException(turnToken);
            }
            dbContext.AddChatMessage(assistantMessage);
            var generated = await artifacts.AttachAsync(dbContext, assistantMessage.Id, turnToken);
            assistantMessage.AttachAuditData(
                completion.Model,
                promptResult.Prompt,
                promptResult.AuditRuntimeContext ?? promptResult.RuntimeContext,
                completion.MetadataJson);
            dbContext.AddLlmRequestAudit(CreateLlmRequestAudit(
                conversation.Id,
                userMessage.Id,
                assistantMessage.Id,
                promptResult.ContainsAttachments ? ChatAttachmentContent.PrivateAudit(completion.AuditData) : completion.AuditData));
            StageResearch(userMessage, completion.DelegatedResearchBrief, isPython: completion.DelegatePython, isVideo: completion.DelegateVideo);

            var memoryActivity = await CompleteMemoryActivityAsync(promptResult, conversation.Id,
                userMessage.Id, assistantMessage.Id, CancellationToken.None);
            if (turnRegistry.IsCurrent(conversation.Id, turn.TurnId))
            {
                await TurnLatencyProfile.MeasureAsync("chat.title_queue", () => QueueConversationTitleGenerationAsync(conversation, CancellationToken.None));
            }

            return new SendMessageResponse(
                conversation.Id,
                conversation.Title,
                conversation.TitleSource,
                MapMessage(assistantMessage, memoryActivity, generated), completion.VoiceContinuation);
        }
        catch (OperationCanceledException) when (turnToken.IsCancellationRequested)
        {
            throw;
        }
        catch (LlmRequestException exception) when (pending.Turn is not null)
        {
            dbContext.AddLlmRequestAudit(CreateLlmRequestAudit(
                conversation.Id,
                userMessage.Id,
                null,
                promptResult.ContainsAttachments ? ChatAttachmentContent.PrivateAudit(exception.AuditData) : exception.AuditData));

            await dbContext.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    public async IAsyncEnumerable<ChatStreamEvent> StreamMessageAsync(
        SendMessageRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!request.HasMessage)
        {
            throw new ArgumentException("Message content cannot be empty.", nameof(request));
        }

        if (request.TurnId is not { } turnId || turnId == Guid.Empty)
        {
            throw new ArgumentException("A valid turnId is required for streaming chat.", nameof(request));
        }

        var effectiveTurnId = request.TurnId ?? Guid.NewGuid();
        using var profile = TurnLatencyProfile.Begin(effectiveTurnId, logger);
        var userContent = HandsFreeReply.UserContent(request);
        var acceptedInterruption = TakeInterruptionReceipt(request, userContent);
        var defer = request.IsHandsFree && request.AllowSilentIgnore && !acceptedInterruption;
        if (defer && userContent.Length == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return ChatStreamEvent.Ignored(turnId);
            yield break;
        }

        using var pending = await PrepareTurnAsync(request, userContent, cancellationToken);
        if (defer || acceptedInterruption) pending.Track(turnRegistry, effectiveTurnId, request);
        var conversation = pending.Conversation;
        var userMessage = pending.UserMessage;
        userContent = pending.ModelContent;
        var turnToken = pending.Token;
        var announced = false;
        if (!defer)
        {
            await AcceptTurnAsync(pending, request, turnId, turnToken);
            announced = true;
            yield return ChatStreamEvent.Conversation(turnId, conversation.Id);
        }

        var promptResult = await BuildPromptForTurnAsync(
            conversation.Messages.Where(message => message.Id != userMessage.Id)
                .OrderBy(message => message.CreatedAt).ThenBy(message => message.Id).ToList(),
            userContent, conversation.Id, request.IsHandsFree, userMessage, pending.Attachments, turnToken);
        promptResult = HandsFreeReply.AddOperationalContext(promptResult, request);
        var modelRequest = CreateModelRequest(promptResult, userContent, request);
        await using var artifacts = new ScriptArtifacts();
        IAsyncEnumerable<ModelStreamChunk> chunks = toolLoop.StreamAsync(
            modelRequest, new ToolExecutionContext(conversation.Id, userMessage.Id, userContent,
                request.IsHandsFree ? HandsFreeReply.Origin : null, request.VoiceSessionId, request.VoiceSegments,
                defer ? ct => AcceptTurnAsync(pending, request, turnId, ct) : null,
                AllowResearchDelegation: researchQueue is not null,
                AllowPythonDelegation: researchQueue is not null, AttachmentReader: promptResult.AttachmentReader)
                { Artifacts = artifacts }, turnToken);

        var content = new StringBuilder();
        ChatMessage? segmentedMessage = null;
        var segments = new List<FinalSpeechSegment>();
        SpeechTextFilter? speechText = null;
        var streamCompleted = false;
        try
        {
        await foreach (var chunk in chunks.WithCancellation(turnToken))
        {
            if (chunk.Ignored)
            {
                if (!defer || pending.Turn is not null) throw new InvalidOperationException("Unexpected ignored turn.");
                pending.MarkIgnored();
                turnToken.ThrowIfCancellationRequested();
                streamCompleted = true;
                yield return ChatStreamEvent.Ignored(turnId);
                yield break;
            }
            var turn = pending.Turn ?? throw new InvalidOperationException("Voice turn was not accepted.");
            EnsureCurrent(turn);
            if (!announced)
            {
                announced = true;
                yield return ChatStreamEvent.Conversation(turn.TurnId, conversation.Id);
            }
            if (chunk.ToolStatus is { } status)
            {
                yield return ChatStreamEvent.ToolStatus(turn.TurnId, status.Category, status.State, status.Message);
            }
            if (request.IncludeWebResearchSources && chunk.WebResearchSources is { Count: > 0 } sources)
            {
                yield return ChatStreamEvent.WebResearchSources(turn.TurnId, conversation.Id, sources);
            }
            if (chunk.FinalSpeechSegment is { } segmentContent)
            {
                if (!request.IsHandsFree || !request.VoiceSegments) throw new InvalidOperationException("Unexpected speech segment.");
                FinalSpeechSegments.Validate(segmentContent);
                if (segments.Count > 0) content.Append(' ');
                content.Append(segmentContent);
                if (segmentedMessage is null)
                {
                    segmentedMessage = conversation.AddMessage(ChatRoles.Assistant, content.ToString());
                    dbContext.AddChatMessage(segmentedMessage);
                }
                else segmentedMessage.UpdateContent(content.ToString());
                var spokenContent = (speechText ??= new SpeechTextFilter()).Append(segmentContent);
                var segment = new FinalSpeechSegment(Guid.NewGuid(), segments.Count, segmentedMessage.Id, segmentContent)
                {
                    SpokenContent = spokenContent == segmentContent ? null : spokenContent
                };
                segments.Add(segment);
                segmentedMessage.AttachAuditData(null, null, null, SpeechSegmentMetadata(turn.TurnId, "generating", segments));
                await dbContext.SaveChangesAsync(turnToken);
                EnsureCurrent(turn);
                if (!turnRegistry.TryAuthorizeSpeechSegment(turn.TurnId, segment)) throw new OperationCanceledException(turnToken);
                TurnLatencyProfile.Mark("chat.final_segment", segment.Sequence);
                yield return ChatStreamEvent.FinalSegment(turn.TurnId, conversation.Id, segmentedMessage.Id, segment.Id, segment.Sequence, segmentContent);
            }
            if (!string.IsNullOrEmpty(chunk.Content))
            {
                if (segments.Count > 0) throw new InvalidOperationException("A committed voice response cannot be replaced.");
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
            TurnLatencyProfile.Mark("chat.text_ready", content.Length);
            var finalContent = content.ToString();
            var assistantMessage = segmentedMessage ?? conversation.AddMessage(ChatRoles.Assistant, finalContent);
            if (!turnRegistry.TrySetTextCompleted(turn.TurnId, assistantMessage.Id))
            {
                throw new OperationCanceledException(turnToken);
            }

            if (segmentedMessage is null) dbContext.AddChatMessage(assistantMessage);
            var generated = await artifacts.AttachAsync(dbContext, assistantMessage.Id, turnToken);
            assistantMessage.AttachAuditData(chunk.Model, promptResult.Prompt,
                promptResult.AuditRuntimeContext ?? promptResult.RuntimeContext,
                segments.Count > 0 ? SpeechSegmentMetadata(turn.TurnId, "complete", segments, chunk.MetadataJson) : chunk.MetadataJson);
            dbContext.AddLlmRequestAudit(CreateLlmRequestAudit(conversation.Id, userMessage.Id, assistantMessage.Id,
                promptResult.ContainsAttachments ? ChatAttachmentContent.PrivateAudit(chunk.AuditData) : chunk.AuditData));
            var researchVoiceSession = request.IsHandsFree && request.VoiceSegments ? request.VoiceSessionId : null;
            StageResearch(userMessage, chunk.DelegatedResearchBrief, researchVoiceSession, chunk.DelegatePython, chunk.DelegateVideo);
            var memoryActivity = await CompleteMemoryActivityAsync(promptResult, conversation.Id,
                userMessage.Id, assistantMessage.Id, CancellationToken.None);
            if (turnRegistry.IsCurrent(conversation.Id, turn.TurnId))
            {
                await TurnLatencyProfile.MeasureAsync("chat.title_queue", () => QueueConversationTitleGenerationAsync(conversation, CancellationToken.None));
                streamCompleted = true;
                yield return ChatStreamEvent.Done(turn.TurnId, conversation.Id, assistantMessage.Id,
                    conversation.Title, conversation.TitleSource, memoryActivity, chunk.VoiceContinuation,
                    WebResearchMetadata.Read(assistantMessage.MetadataJson, assistantMessage.Content),
                    researchVoiceSession.HasValue && chunk.DelegatedResearchBrief is not null ? userMessage.Id : null, generated);
            }
            streamCompleted = true;
            yield break;
        }

        throw new InvalidOperationException("Model streaming ended before a final event was received.");
        }
        finally
        {
            if (!streamCompleted && pending.Turn is { } turn)
            {
                if (segmentedMessage is not null)
                {
                    segmentedMessage.AttachAuditData(segmentedMessage.Model, segmentedMessage.PromptSnapshot,
                        segmentedMessage.RuntimeContextSnapshot, SpeechSegmentMetadata(turn.TurnId, "interrupted", segments));
                    await dbContext.SaveChangesAsync(CancellationToken.None);
                }
                // Persistence (including interrupted segment metadata) is now
                // settled. An unrelated native TTS DELETE must not hold up a
                // successor's read of this completed database boundary.
                pending.MarkSettled();
                await voiceService.CancelTurnAsync(turn.TurnId, "text_stream_interrupted", CancellationToken.None);
            }
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

        var assistantIds = conversation.Messages.Where(x => x.Role == ChatRoles.Assistant).Select(x => x.Id).ToArray();
        var attachments = await dbContext.GetAttachmentIndexAsync(conversationId, cancellationToken);
        var activities = activity is null
            ? new Dictionary<Guid, MemoryActivitySnapshot>()
            : await activity.LoadForAssistantMessagesAsync(assistantIds, cancellationToken);
        return new ConversationResponse(
            conversation.Id,
            conversation.Title,
            conversation.CreatedAt,
            conversation.UpdatedAt,
            conversation.TitleSource,
            conversation.Messages
                .OrderBy(message => message.CreatedAt)
                .ThenBy(message => message.Id)
                .Select(message => MapMessage(message, activities.GetValueOrDefault(message.Id),
                    attachments.Contains(message.Id) ? attachments[message.Id].ToArray() : null))
                .ToList());
    }

    public async Task<MemoryActivitySnapshot?> GetMemoryActivityAsync(Guid assistantMessageId,
        CancellationToken cancellationToken = default)
    {
        var message = await dbContext.GetChatMessageAsync(assistantMessageId, cancellationToken);
        if (message is null || message.Role != ChatRoles.Assistant) return null;
        if (activity is null) return new MemoryActivitySnapshot(false, []);
        var snapshots = await activity.LoadForAssistantMessagesAsync([assistantMessageId], cancellationToken);
        return snapshots.GetValueOrDefault(assistantMessageId) ?? new MemoryActivitySnapshot(false, []);
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
        if (scriptWorkspaces is not null) await scriptWorkspaces.DeleteAsync(conversationId, CancellationToken.None);

        return true;
    }

    private bool TakeInterruptionReceipt(SendMessageRequest request, string content)
    {
        if (request.InterruptionReceipt is null && request.ResearchJobId is null) return false;
        if (request.InterruptionReceipt is not { } receipt || !request.IsHandsFree || !request.VoiceSegments ||
            !request.AllowSilentIgnore || request.VoiceSessionId is not { } session || string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("A autorização da interrupção é inválida.");
        var candidate = new VoiceInterruptionRequest(session, request.ConversationId, content,
            request.InterruptedGeneration, request.InterruptedPlayback, request.ResearchJobId).Normalize();
        if (interruptionReceipts?.Consume(receipt, candidate) != true)
            throw new ArgumentException("A autorização da interrupção expirou ou pertence a outro pedido.");
        return true;
    }

    private async Task<PendingChatTurn> PrepareTurnAsync(SendMessageRequest request, string userContent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var destination = request.ConversationId;
        Conversation? conversation = null;
        var playback = request.InterruptedPlayback;
        string? interruptedContent = null;
        if (request.InterruptedGeneration is { } interruption)
        {
            // Cancellation is idempotent and tombstones a request that has not
            // registered yet. The old HTTP stream may disconnect before its
            // database write settles, so do not infer persistence from it.
            var prior = await voiceService.CancelVoiceGenerationAsync(interruption.TurnId, request.VoiceSessionId!.Value,
                destination, cancellationToken);
            if (prior is not null)
            {
                await prior.Settled.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                conversation = await dbContext.GetConversationWithMessagesAsync(prior.ConversationId, cancellationToken);
                if (conversation is not null)
                {
                    destination = conversation.Id;
                    if (conversation.Messages.Any(message => message.Id == prior.UserMessageId && message.Role == ChatRoles.User))
                        playback = VoicePlaybackContext.FirstUnplayed(interruption.TurnId, conversation.Id, conversation.Messages);
                    else
                    {
                        // An explicitly ignored background utterance must not
                        // reappear just because its terminal event was lost.
                        if (!prior.Ignored) interruptedContent = RetainInterruptedContent(prior.Content);
                        playback = prior.Playback;
                    }
                }
                else
                {
                    if (destination is { } known) throw new ConversationNotFoundException(known);
                    if (!prior.Ignored) interruptedContent = RetainInterruptedContent(prior.Content);
                }
            }
            else
                interruptedContent = RetainInterruptedContent(interruption.PreviousContent);
        }
        var isNewConversation = destination is null;
        conversation ??= await TurnLatencyProfile.MeasureAsync("chat.conversation_load", async () =>
            destination is { } conversationId
                ? await dbContext.GetConversationWithMessagesAsync(conversationId, cancellationToken)
                    ?? throw new ConversationNotFoundException(conversationId)
                : new Conversation(Conversation.DefaultTitle));
        // Before the model decides, keep the provisional message detached even for
        // an existing conversation. A discarded utterance must not affect its history.
        var content = interruptedContent is null ? userContent : $"{interruptedContent}\n{userContent}";
        var modelContent = interruptedContent is null ? userContent :
            $"{PriorSpeechLabel}{interruptedContent}{CurrentSpeechLabel}{userContent}";
        var userMessage = new ChatMessage(conversation.Id, ChatRoles.User, content);
        var attachments = await ChatAttachmentContent.PrepareAsync(dbContext, userMessage.Id, request.Attachments,
            request.AttachmentUploadIds, cancellationToken);
        if (request.InterruptedGeneration is not null)
            VoicePlaybackContext.Attach(userMessage, playback, conversation.Messages);
        else
            VoicePlaybackContext.Attach(userMessage, request, conversation.Messages);
        return new PendingChatTurn(conversation, userMessage, modelContent, attachments, isNewConversation, playback, cancellationToken);
    }

    private static string? RetainInterruptedContent(string? previous)
    {
        if (string.IsNullOrWhiteSpace(previous)) return null;
        var retained = DisplayUserSpeech(previous.Trim());
        if (retained.Length > 16384) retained = retained[^16384..];
        return retained;
    }

    private static string DisplayUserSpeech(string content)
    {
        // Older versions persisted the two operational labels in the message body.
        if (!content.StartsWith(PriorSpeechLabel, StringComparison.Ordinal) ||
            !content.Contains(CurrentSpeechLabel, StringComparison.Ordinal))
            return content;
        while (content.StartsWith(PriorSpeechLabel, StringComparison.Ordinal))
            content = content[PriorSpeechLabel.Length..];
        return content.Replace(CurrentSpeechLabel, "\n", StringComparison.Ordinal);
    }

    private async Task AcceptTurnAsync(PendingChatTurn pending, SendMessageRequest request,
        Guid turnId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var conversation = pending.Conversation;
        var userMessage = pending.UserMessage;
        // A cancellation before registration is retained by the registry. Registration
        // happens only after acceptance so an ignored utterance never supersedes a turn.
        var turn = await TurnLatencyProfile.MeasureAsync("chat.turn_register", () =>
            voiceService.RegisterTurnAsync(turnId, conversation.Id, cancellationToken));
        if (!turnRegistry.TryTransition(turn.TurnId, TurnStatus.Created, TurnStatus.GeneratingText))
            throw new OperationCanceledException(turn.Cancellation.Token);
        pending.Activate(turn);
        pending.Token.ThrowIfCancellationRequested();
        if (pending.IsNewConversation) dbContext.AddConversation(conversation);
        conversation.AddMessage(userMessage);
        turn.UserMessageId = userMessage.Id;
        dbContext.AddChatMessage(userMessage);
        if (pending.Attachments.Files.Count > 0) dbContext.AddChatAttachments(pending.Attachments.Files);
        if (automaticMemory?.Enabled == true)
        {
            dbContext.AddMemoryExtractionJob(new MemoryExtractionJob(conversation.Id, userMessage.Id,
                (clock ?? TimeProvider.System).GetUtcNow()));
        }
        await TurnLatencyProfile.MeasureAsync("chat.user_persist", () => dbContext.SaveChangesAsync(pending.Token));
        if (automaticMemory?.Enabled == true) metrics?.MemoryExtractionJobsCreated.Add(1);
    }

    private sealed class PendingChatTurn(Conversation conversation, ChatMessage userMessage, string modelContent,
        ChatAttachmentContent attachments, bool isNewConversation, VoicePlaybackInterruption? playback, CancellationToken cancellationToken) : IDisposable
    {
        private readonly CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        private CancellationTokenRegistration registration;
        private IDisposable? reservation;
        private VoiceGenerationReceipt? generationReceipt;
        public Conversation Conversation { get; } = conversation;
        public ChatMessage UserMessage { get; } = userMessage;
        public ChatAttachmentContent Attachments { get; } = attachments;
        public string ModelContent { get; } = modelContent;
        public bool IsNewConversation { get; } = isNewConversation;
        public ActiveTurn? Turn { get; private set; }
        public CancellationToken Token => cancellation.Token;

        public void Track(IActiveTurnRegistry registry, Guid turnId, SendMessageRequest request)
        {
            var receipt = request.VoiceSegments && request.VoiceSessionId is { } sessionId
                ? new VoiceGenerationReceipt(sessionId, Conversation.Id, UserMessage.Id, UserMessage.Content, playback)
                : null;
            reservation = registry.TrackPendingTurn(turnId, Conversation.Id, cancellation, receipt);
            generationReceipt = receipt;
        }

        public void MarkIgnored() => generationReceipt?.MarkIgnored();
        public void MarkSettled() => generationReceipt?.Settle();

        public void Activate(ActiveTurn turn)
        {
            Turn = turn;
            registration = turn.Cancellation.Token.Register(() => cancellation.Cancel());
        }

        public void Dispose()
        {
            try
            {
                reservation?.Dispose();
                registration.Dispose();
                cancellation.Dispose();
            }
            finally { generationReceipt?.Settle(); }
        }
    }

    private async Task<PromptBuildResult> BuildPromptForTurnAsync(
        IReadOnlyList<ChatMessage> history,
        string userContent,
        Guid conversationId,
        bool handsFree,
        ChatMessage userMessage,
        ChatAttachmentContent attachments,
        CancellationToken cancellationToken)
    {
        using var timing = TurnLatencyProfile.Measure("chat.prepare", metrics?.ChatPreparationSeconds);
        var states = new List<string>();
        var email = (await TurnLatencyProfile.MeasureAsync("context.pending_email", () => dbContext.GetUnresolvedPendingEmailActionsAsync(conversationId, cancellationToken)))
            .FirstOrDefault(action => action.IsOpen() || action.MayHaveAppliedChanges);
        if (email is not null)
            states.Add($"Ação pendente Gmail: {email.HumanSummary}; válida até {email.ExpiresAt:O}. " +
                (email.MayHaveAppliedChanges ? "Uma tentativa anterior pode ter aplicado alterações; retry é idempotente. " : "") +
                (email.IsOpen() ? "Aceitação: email_confirm_pending_action; desistência: email_cancel_pending_action; correção: prepare uma nova proposta." :
                    "A proposta expirou, mas possíveis efeitos permanecem; consulte os emails. Cancelar a tentativa não reverte alterações."));
        var calendarActions = await TurnLatencyProfile.MeasureAsync("context.pending_calendar", () => dbContext.GetUnresolvedPendingCalendarActionsAsync(conversationId, cancellationToken));
        foreach (var calendar in calendarActions.Where(action => action.IsOpen() || action.MayHaveAppliedChanges).Take(10))
        {
            var directCreation = System.Text.Json.JsonSerializer.Deserialize<Aegis.Application.Calendar.CalendarActionPayload>(calendar.PayloadJson,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))?.CreationRequest is not null;
            states.Add($"Ação Calendar: {calendar.HumanSummary}; válida até {calendar.ExpiresAt:O}. " +
                (calendar.MayHaveAppliedChanges ? "Uma tentativa anterior pode ter alterado a agenda; não afirme sucesso nem ausência de efeitos. " : "") +
                (directCreation
                    ? "Criação solicitada diretamente. Não é proposta aguardando autorização. Só retome esta criação se o usuário pedir sua continuação/retry; calendar_create_event com os mesmos dados verifica o registro e preserva o ID. Nunca execute ao mudar de assunto."
                    : calendar.IsOpen()
                        ? "Proposta aguardando confirmação: execute calendar_confirm_pending_action somente se a mensagem atual aceitar especificamente esta proposta anterior; nunca execute por menção casual ou mudança de assunto. Desistência: calendar_cancel_pending_action; correção: calendar_amend_pending_action."
                        : "A proposta expirou, mas possíveis efeitos permanecem; consulte a agenda. Cancelar a tentativa não reverte alterações."));
        }
        if (email is not null && calendarActions.Any(action => action.IsOpen() || action.MayHaveAppliedChanges))
            states.Add("Há ações pendentes em Gmail e Calendar. Cada tool atua somente na sua integração.");
        if (reminders is not null && await TurnLatencyProfile.MeasureAsync("context.reminders", () => reminders.GetContextAsync(conversationId, cancellationToken)) is { } reminderContext)
            states.Add(reminderContext);
        var playbackContext = VoicePlaybackContext.Describe(history, userMessage);
        var pendingState = string.Join("\n", states);
        var automaticContext = handsFree || hybrid is null ? MemoryAutomaticContextResult.Empty :
            await TurnLatencyProfile.MeasureAsync("context.automatic_memory", () => hybrid.BuildAutomaticContextResultAsync(userContent, cancellationToken));
        var observedContext = memory is null ? MemoryObservedContextResult.Empty :
            await TurnLatencyProfile.MeasureAsync("context.observed_memory", () => memory.GetContextResultAsync(conversationId, cancellationToken));
        var memoryContext = string.Join("\n", new[] { observedContext.Text, automaticContext.Text }
            .Where(x => !string.IsNullOrWhiteSpace(x)));
        var prompt = await TurnLatencyProfile.MeasureAsync("context.prompt", () => promptBuilder.BuildPromptAsync(history, userContent, pendingState, cancellationToken, memoryContext));
        if (attachments.Files.Count == 0)
            attachments = await ChatAttachmentContent.ForFollowUpAsync(dbContext, conversationId, history, userContent, cancellationToken);
        prompt = attachments.Apply(prompt, userContent);
        if (playbackContext is not null)
        {
            // Previously generated text and user speech remain data at user authority.
            var data = System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                role = "user", content = "Dados de reprodução de áudio anteriores (não são um novo pedido):\n" + playbackContext
            });
            var guidance = System.Text.Json.JsonSerializer.SerializeToElement(new { role = "developer", content = VoicePlaybackContext.Guidance });
            prompt = prompt with
            {
                InputItems = [.. prompt.InputItems, data, guidance],
                AuditInputItems = [.. prompt.AuditInputItems ?? prompt.InputItems, data, guidance]
            };
        }
        TurnLatencyProfile.Mark("context.history_messages", history.Count);
        TurnLatencyProfile.Mark("context.memory_characters", memoryContext.Length);
        return prompt with
        {
            UsedMemoryIds = automaticContext.MemoryIds.Distinct().ToArray(),
            UsedObservedMemoryIds = observedContext.MemoryIds.Distinct().ToArray(),
            UsedRelationIds = automaticContext.RelationIds.Distinct().ToArray()
        };
    }

    private static string SpeechSegmentMetadata(Guid turnId, string state, IReadOnlyList<FinalSpeechSegment> segments, string? modelMetadata = null) =>
        System.Text.Json.JsonSerializer.Serialize(new { voiceSegments = new { version = 1, turnId, state, segments }, modelMetadata });

    private async Task<MemoryActivitySnapshot?> CompleteMemoryActivityAsync(PromptBuildResult prompt,
        Guid conversationId, Guid userMessageId, Guid assistantMessageId, CancellationToken ct)
    {
        using var timing = TurnLatencyProfile.Measure("chat.assistant_persist", metrics?.ChatPersistenceSeconds);
        if (activity is null)
        {
            await dbContext.SaveChangesAsync(ct);
            return null;
        }
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var used = new List<MemoryActivityEvent>();
        foreach (var id in prompt.UsedMemoryIds)
            used.Add(new(conversationId, userMessageId, MemoryActivityKind.Used,
                MemoryActivitySource.AutomaticContext, MemoryActivityTargetType.MemoryRecord, id, now));
        foreach (var id in prompt.UsedObservedMemoryIds)
            used.Add(new(conversationId, userMessageId, MemoryActivityKind.Used,
                MemoryActivitySource.ObservedContext, MemoryActivityTargetType.MemoryRecord, id, now));
        foreach (var id in prompt.UsedRelationIds)
            used.Add(new(conversationId, userMessageId, MemoryActivityKind.Used,
                MemoryActivitySource.AutomaticContext, MemoryActivityTargetType.MemoryRelation, id, now));
        await activity.SaveResponseWithUsedAsync(used, ct);
        var snapshots = await activity.LoadForAssistantMessagesAsync([assistantMessageId], ct);
        return snapshots.GetValueOrDefault(assistantMessageId);
    }

    private static ChatMessageResponse MapMessage(ChatMessage message, MemoryActivitySnapshot? memoryActivity = null,
        IReadOnlyList<ChatAttachmentResponse>? attachments = null)
    {
        return new ChatMessageResponse(
            message.Id,
            message.ConversationId,
            message.Role,
            message.Role == ChatRoles.User ? DisplayUserSpeech(message.Content) : message.Content,
            message.CreatedAt,
            message.Model,
            memoryActivity, message.IsProactive, message.ProactiveSequence,
            WebResearchMetadata.Read(message.MetadataJson, message.Content), attachments);
    }

    private async Task<ModelCompletionResponse> RunToolCompletionAsync(
        ModelRequest request,
        Guid conversationId,
        Guid userMessageId,
        string userContent,
        SendMessageRequest clientRequest,
        ChatAttachmentContent? attachmentReader,
        ScriptArtifacts artifacts,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? acceptVoiceTurn = null)
    {
        var response = await toolLoop.RunAsync(
            request,
            new ToolExecutionContext(conversationId, userMessageId, userContent,
                clientRequest.IsHandsFree ? HandsFreeReply.Origin : null, clientRequest.VoiceSessionId, clientRequest.VoiceSegments,
                acceptVoiceTurn, AllowResearchDelegation: researchQueue is not null,
                AllowPythonDelegation: researchQueue is not null, AttachmentReader: attachmentReader)
                { Artifacts = artifacts }, cancellationToken);

        return new ModelCompletionResponse(
            response.Content,
            response.Provider,
            response.Model,
            response.Purpose,
            response.MetadataJson,
            response.AuditData,
            response.VoiceContinuation,
            response.Ignored,
            response.DelegatedResearchBrief,
            response.DelegatePython,
            response.DelegateVideo);
    }

    private void StageResearch(ChatMessage userMessage, string? brief, Guid? voiceSessionId = null, bool isPython = false,
        bool isVideo = false)
    {
        if (brief is null) return;
        (researchQueue ?? throw new InvalidOperationException("A fila de pesquisa está indisponível."))
            .Stage(userMessage, brief, voiceSessionId, isPython, isVideo);
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
            CreatePreview(lastMessage?.Content), conversation.HasUnreadProactive);
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
            CreatePreview(conversation.LastMessageContent), conversation.HasUnreadProactive);
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
        SendMessageRequest request)
    {
        var metadata = new Dictionary<string, string>
        {
            ["aegis_version"] = "0.6.1",
            ["purpose"] = "Chat"
        };
        if (request.IsHandsFree)
        {
            metadata["origin"] = HandsFreeReply.Origin;
            if (request.VoiceSegments) metadata["voice_segments"] = "1";
            metadata["voice_session_id"] = request.VoiceSessionId!.Value.ToString("D");
        }
        return new ModelRequest(
            promptResult.Prompt,
            userContent,
            ModelPurpose.Chat,
            metadata,
            promptResult.InputItems,
            promptResult.AuditInputItems,
            ReplyStyleInstructions: promptResult.ReplyStyleInstructions);
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

    private void EnsureCurrent(ActiveTurn turn)
    {
        if (!turnRegistry.IsCurrent(turn.ConversationId, turn.TurnId))
        {
            throw new OperationCanceledException(turn.Cancellation.Token);
        }
    }
}
