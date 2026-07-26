namespace Aegis.Application.Chat;

public sealed record ChatStreamEvent(
    string Type,
    Guid? TurnId = null,
    Guid? ConversationId = null,
    string? Content = null,
    Guid? MessageId = null,
    Guid? AssistantMessageId = null,
    string? ConversationTitle = null,
    string? TitleSource = null)
{
    public static ChatStreamEvent Conversation(Guid turnId, Guid conversationId) =>
        new("conversation", turnId, ConversationId: conversationId);

    public static ChatStreamEvent Token(Guid turnId, string content) =>
        new("token", turnId, Content: content);

    public static ChatStreamEvent Done(
        Guid turnId,
        Guid conversationId,
        Guid messageId,
        string? conversationTitle,
        string? titleSource) =>
        new(
            "done",
            turnId,
            conversationId,
            MessageId: messageId,
            AssistantMessageId: messageId,
            ConversationTitle: conversationTitle,
            TitleSource: titleSource);
}
