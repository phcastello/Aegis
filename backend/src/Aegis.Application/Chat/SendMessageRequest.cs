namespace Aegis.Application.Chat;

public sealed class SendMessageRequest
{
    public Guid? TurnId { get; init; }

    public Guid? ConversationId { get; init; }

    public string? Origin { get; init; }

    public Guid? VoiceSessionId { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsHandsFree => Origin == "hands_free";

    public bool AllowSilentIgnore { get; init; }

    public bool VoiceSegments { get; init; }
    public bool IncludeWebResearchSources { get; init; }

    public Aegis.Application.Voice.VoicePlaybackInterruption? InterruptedPlayback { get; init; }

    public VoiceGenerationInterruption? InterruptedGeneration { get; init; }

    public Guid? InterruptionReceipt { get; init; }
    public Guid? ResearchJobId { get; init; }

    public string Content { get; init; } = string.Empty;
    public IReadOnlyList<ChatAttachmentRequest>? Attachments { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasMessage => !string.IsNullOrWhiteSpace(Content) || Attachments is { Count: > 0 };
}

/// <summary>A new utterance cut off a voice turn before any audio was played.
/// Prior content is only a fallback when the server no longer has its receipt.</summary>
public sealed record VoiceGenerationInterruption(Guid TurnId, string? PreviousContent = null);
