namespace Aegis.Application.Chat;

public sealed record ConversationTitleJob(
    Guid ConversationId,
    string UserContent,
    string AssistantContent);
