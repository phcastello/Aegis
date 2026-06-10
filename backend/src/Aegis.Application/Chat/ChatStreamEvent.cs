namespace Aegis.Application.Chat;

public sealed record ChatStreamEvent(
    string Type,
    Guid? ConversationId = null,
    string? Content = null,
    Guid? MessageId = null)
{
    public static ChatStreamEvent Conversation(Guid conversationId) =>
        new("conversation", ConversationId: conversationId);

    public static ChatStreamEvent Token(string content) =>
        new("token", Content: content);

    public static ChatStreamEvent Done(Guid conversationId, Guid messageId) =>
        new("done", conversationId, MessageId: messageId);
}
