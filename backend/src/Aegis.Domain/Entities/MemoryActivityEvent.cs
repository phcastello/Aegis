using System.Security.Cryptography;
using System.Text;

namespace Aegis.Domain.Entities;

public enum MemoryActivityKind { Used, Consulted, Created, Updated, Deleted }
public enum MemoryActivitySource { AutomaticContext, ObservedContext, AutomaticExtraction, ExplicitTool }
public enum MemoryActivityTargetType { MemoryRecord, MemoryRelation }

// This is turn metadata. Knowledge remains exclusively in MemoryRecord/MemoryRelation.
public sealed class MemoryActivityEvent
{
    private MemoryActivityEvent() { }

    public MemoryActivityEvent(Guid conversationId, Guid userMessageId, MemoryActivityKind kind,
        MemoryActivitySource source, MemoryActivityTargetType targetType, Guid targetId, DateTimeOffset occurredAt)
    {
        Id = Guid.NewGuid();
        ConversationId = conversationId;
        UserMessageId = userMessageId;
        Kind = kind;
        Source = source;
        TargetType = targetType;
        TargetId = targetId;
        OccurredAt = occurredAt;
        DedupeKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{userMessageId:N}:{kind}:{source}:{targetType}:{targetId:N}")));
    }

    public Guid Id { get; private set; }
    public Guid? ConversationId { get; private set; }
    public Guid? UserMessageId { get; private set; }
    public MemoryActivityKind Kind { get; private set; }
    public MemoryActivitySource Source { get; private set; }
    public MemoryActivityTargetType TargetType { get; private set; }
    public Guid TargetId { get; private set; }
    public string DedupeKey { get; private set; } = "";
    public DateTimeOffset OccurredAt { get; private set; }
}
