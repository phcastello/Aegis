namespace Aegis.Application.Chat;

public sealed class SendMessageRequest
{
    public Guid? TurnId { get; init; }

    public Guid? ConversationId { get; init; }

    public string? Origin { get; init; }

    public Guid? VoiceSessionId { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsHandsFree => Origin == "hands_free";

    public string Content { get; init; } = string.Empty;
}
