using Aegis.Domain.Entities;

namespace Aegis.Application.Common;

public interface IAegisDbContext
{
    IQueryable<Conversation> Conversations { get; }

    IQueryable<ChatMessage> ChatMessages { get; }

    IQueryable<MessageFeedback> MessageFeedback { get; }

    void AddConversation(Conversation conversation);

    void AddChatMessage(ChatMessage message);

    void AddMessageFeedback(MessageFeedback feedback);

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

    Task<IReadOnlyList<Conversation>> GetRecentConversationsAsync(
        int limit,
        CancellationToken cancellationToken = default);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
