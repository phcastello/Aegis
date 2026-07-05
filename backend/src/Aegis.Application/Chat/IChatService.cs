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

    Task<ConversationPageResponse> GetRecentConversationsAsync(
        int limit = 30,
        string? cursor = null,
        CancellationToken cancellationToken = default);

    Task<ConversationSummaryResponse?> RenameConversationAsync(
        Guid conversationId,
        string? title,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);
}
