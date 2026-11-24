namespace Aegis.Application.Chat;

/// <summary>
/// Marks persisted conversation state for live clients to reconcile. Delivery is
/// best effort and must not block the transaction or convey conversation content.
/// </summary>
public interface IConversationChangePublisher
{
    void Publish(Guid conversationId);
}
