using Aegis.Domain;

namespace Aegis.Domain.Entities;

public sealed class ChatMessage : AuditableEntity
{
    private ChatMessage()
    {
    }

    public ChatMessage(Guid conversationId, string role, string content)
    {
        if (!ChatRoles.IsKnown(role))
        {
            throw new ArgumentException($"Unsupported chat role '{role}'.", nameof(role));
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ArgumentException("Message content cannot be empty.", nameof(content));
        }

        InitializeAudit();
        ConversationId = conversationId;
        Role = role;
        Content = content.Trim();
    }

    public Guid ConversationId { get; private set; }

    public string Role { get; private set; } = string.Empty;

    public string Content { get; private set; } = string.Empty;

    public string? Model { get; private set; }

    public string? PromptSnapshot { get; private set; }

    public string? RuntimeContextSnapshot { get; private set; }

    public string? MetadataJson { get; private set; }

    public Conversation? Conversation { get; private set; }

    public void UpdateContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ArgumentException("Message content cannot be empty.", nameof(content));
        }

        Content = content.Trim();
        Touch();
    }

    public void AttachAuditData(
        string? model,
        string? promptSnapshot,
        string? runtimeContextSnapshot,
        string? metadataJson)
    {
        Model = model;
        PromptSnapshot = promptSnapshot;
        RuntimeContextSnapshot = runtimeContextSnapshot;
        MetadataJson = metadataJson;
        Touch();
    }
}
