namespace Aegis.Application.Turns;

public sealed class ActiveTurn : IDisposable
{
    internal ActiveTurn(Guid turnId, Guid conversationId)
    {
        TurnId = turnId;
        ConversationId = conversationId;
        CreatedAt = DateTimeOffset.UtcNow;
        Cancellation = new CancellationTokenSource();
    }

    internal List<FinalSpeechSegment> SpeechSegments { get; } = [];
    internal Dictionary<Guid, string> SegmentRequests { get; } = [];
    internal HashSet<Guid> CompletedSegmentRequests { get; } = [];
    public Guid TurnId { get; }
    public Guid ConversationId { get; }
    public Guid? UserMessageId { get; internal set; }
    public Guid? AssistantMessageId { get; internal set; }
    public Guid? SpeechRequestId { get; internal set; }
    public string? NativeSpeechRequestId { get; internal set; }
    public TurnStatus Status { get; internal set; } = TurnStatus.Created;
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? TextStartedAt { get; internal set; }
    public DateTimeOffset? TextCompletedAt { get; internal set; }
    public DateTimeOffset? SpeechStartedAt { get; internal set; }
    public DateTimeOffset? CompletedAt { get; internal set; }
    public DateTimeOffset? CancelledAt { get; internal set; }
    public CancellationTokenSource Cancellation { get; }

    public void Dispose() => Cancellation.Dispose();
}

public sealed record FinalSpeechSegment(Guid Id, int Sequence, Guid AssistantMessageId, string Content);
