namespace Aegis.Application.Chat;

public sealed record ChatStreamEvent(
    string Type,
    Guid? ConversationId = null,
    string? Content = null,
    Guid? MessageId = null,
    string? ConversationTitle = null,
    string? TitleSource = null)
{
    public static ChatStreamEvent Conversation(Guid conversationId) =>
        new("conversation", ConversationId: conversationId);

    public static ChatStreamEvent Token(string content) =>
        new("token", Content: content);

    public static ChatStreamEvent Done(
        Guid conversationId,
        Guid messageId,
        string? conversationTitle,
        string? titleSource) =>
        new(
            "done",
            conversationId,
            MessageId: messageId,
            ConversationTitle: conversationTitle,
            TitleSource: titleSource);
}
