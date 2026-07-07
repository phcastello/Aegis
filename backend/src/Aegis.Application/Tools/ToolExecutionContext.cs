namespace Aegis.Application.Tools;

public sealed record ToolExecutionContext(
    Guid ConversationId,
    Guid UserMessageId,
    string UserContent);
