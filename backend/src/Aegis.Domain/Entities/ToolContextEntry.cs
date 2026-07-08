namespace Aegis.Domain.Entities;

public sealed class ToolContextEntry : AuditableEntity
{
    private ToolContextEntry()
    {
    }

    public ToolContextEntry(
        Guid conversationId,
        string scope,
        string entryType,
        string key,
        string dataJson,
        string sourceToolName,
        DateTimeOffset expiresAt)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            throw new ArgumentException("Scope is required.", nameof(scope));
        }

        if (string.IsNullOrWhiteSpace(entryType))
        {
            throw new ArgumentException("Entry type is required.", nameof(entryType));
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Key is required.", nameof(key));
        }

        if (string.IsNullOrWhiteSpace(dataJson))
        {
            throw new ArgumentException("Data JSON is required.", nameof(dataJson));
        }

        if (string.IsNullOrWhiteSpace(sourceToolName))
        {
            throw new ArgumentException("Source tool name is required.", nameof(sourceToolName));
        }

        InitializeAudit();
        ConversationId = conversationId;
        Scope = scope.Trim();
        EntryType = entryType.Trim();
        Key = key.Trim();
        DataJson = dataJson;
        SourceToolName = sourceToolName.Trim();
        ExpiresAt = expiresAt;
    }

    public Guid ConversationId { get; private set; }

    public string Scope { get; private set; } = string.Empty;

    public string EntryType { get; private set; } = string.Empty;

    public string Key { get; private set; } = string.Empty;

    public string DataJson { get; private set; } = "{}";

    public string SourceToolName { get; private set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? ReplacedAt { get; private set; }

    public Conversation? Conversation { get; private set; }

    public bool IsActive(DateTimeOffset? now = null)
    {
        var timestamp = now ?? DateTimeOffset.UtcNow;
        return ReplacedAt is null && ExpiresAt > timestamp;
    }

    public void Replace(DateTimeOffset? now = null)
    {
        ReplacedAt ??= now ?? DateTimeOffset.UtcNow;
        Touch(ReplacedAt);
    }
}
