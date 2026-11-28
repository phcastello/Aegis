namespace Aegis.Application.Tools;

public sealed record ToolExecutionContext(
    Guid ConversationId,
    Guid UserMessageId,
    string UserContent,
    string? Origin = null,
    Guid? VoiceSessionId = null,
    bool VoiceSegments = false,
    Func<CancellationToken, Task>? AcceptVoiceTurn = null,
    bool AllowResearchDelegation = false,
    bool AllowPythonDelegation = false,
    Aegis.Application.Chat.ChatAttachmentContent? AttachmentReader = null)
{
    public ScriptArtifacts Artifacts { get; init; } = new();
}
