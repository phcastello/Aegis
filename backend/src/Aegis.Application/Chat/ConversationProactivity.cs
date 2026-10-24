namespace Aegis.Application.Chat;
public sealed record ConversationProactiveActivity(Guid ConversationId, long LastProactiveSequence, long ReadProactiveSequence);
public interface IConversationProactivity
{
    Task<IReadOnlyList<ConversationProactiveActivity>> ActivityAsync(CancellationToken ct);
    Task<IReadOnlyList<ChatMessageResponse>?> MessagesAsync(Guid conversationId, long after, CancellationToken ct);
    Task<bool> MarkReadAsync(Guid conversationId, long sequence, CancellationToken ct);
}
