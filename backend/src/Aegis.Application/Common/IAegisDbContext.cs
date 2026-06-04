using Aegis.Domain.Entities;

namespace Aegis.Application.Common;

public interface IAegisDbContext
{
    IQueryable<Conversation> Conversations { get; }

    IQueryable<ChatMessage> ChatMessages { get; }

    void AddConversation(Conversation conversation);

    void AddChatMessage(ChatMessage message);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
