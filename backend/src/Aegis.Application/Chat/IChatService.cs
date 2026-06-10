namespace Aegis.Application.Chat;

public interface IChatService
{
    Task<SendMessageResponse> SendMessageAsync(
        SendMessageRequest request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<ChatStreamEvent> StreamMessageAsync(
        SendMessageRequest request,
        CancellationToken cancellationToken = default);

    Task<ConversationResponse?> GetConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConversationSummaryResponse>> GetRecentConversationsAsync(
        int limit = 20,
        CancellationToken cancellationToken = default);
}
