using Aegis.Application.Chat;
using Aegis.Domain.Entities;

namespace Aegis.Application.Common;

public interface IAegisDbContext
{
    IQueryable<Conversation> Conversations { get; }

    IQueryable<ChatMessage> ChatMessages { get; }

    IQueryable<MessageFeedback> MessageFeedback { get; }

    IQueryable<LlmRequestAudit> LlmRequestAudits { get; }

    IQueryable<EmailAccountConnection> EmailAccountConnections { get; }

    IQueryable<PendingEmailAction> PendingEmailActions { get; }

    IQueryable<EmailActionAudit> EmailActionAudits { get; }

    IQueryable<ToolContextEntry> ToolContextEntries { get; }

    void AddConversation(Conversation conversation);

    void AddChatMessage(ChatMessage message);

    void AddMessageFeedback(MessageFeedback feedback);

    void AddLlmRequestAudit(LlmRequestAudit audit);

    void AddEmailAccountConnection(EmailAccountConnection connection);

    void AddPendingEmailAction(PendingEmailAction action);

    void AddEmailActionAudit(EmailActionAudit audit);

    void AddToolContextEntry(ToolContextEntry entry);

    Task<ChatMessage?> GetChatMessageAsync(
        Guid messageId,
        CancellationToken cancellationToken = default);

    Task<ChatMessage?> GetPreviousUserMessageAsync(
        Guid conversationId,
        DateTimeOffset before,
        CancellationToken cancellationToken = default);

    Task<MessageFeedback?> GetMessageFeedbackWithMessageAsync(
        Guid feedbackId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MessageFeedback>> GetRecentMessageFeedbackAsync(
        int limit,
        CancellationToken cancellationToken = default);

    Task<Conversation?> GetConversationWithMessagesAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ChatMessage>> GetRecentMessagesAsync(
        Guid conversationId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConversationSummaryData>> GetRecentConversationSummariesAsync(
        int limit,
        ConversationCursor? cursor = null,
        CancellationToken cancellationToken = default);

    Task<PendingEmailAction?> GetLatestOpenPendingEmailActionAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ToolContextEntry>> GetActiveToolContextEntriesAsync(
        Guid conversationId,
        string scope,
        string? entryType = null,
        string? key = null,
        CancellationToken cancellationToken = default);

    Task<bool> HasRecentToolContextEntriesAsync(
        Guid conversationId,
        string scope,
        DateTimeOffset since,
        CancellationToken cancellationToken = default);

    Task ReplaceActiveToolContextEntriesAsync(
        Guid conversationId,
        string scope,
        string entryType,
        string key,
        CancellationToken cancellationToken = default);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
