using Aegis.Domain.Entities;

namespace Aegis.Application.Common;

public interface IAegisDbContext
{
    IQueryable<Conversation> Conversations { get; }

    IQueryable<ChatMessage> ChatMessages { get; }

    void AddConversation(Conversation conversation);

    void AddChatMessage(ChatMessage message);

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
