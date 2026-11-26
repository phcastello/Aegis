using Aegis.Application.Voice;

namespace Aegis.Application.Turns;

/// <summary>Bounded in-memory handoff data for a segmented voice request. The
/// next request waits for settlement before looking for the prior user message;
/// a guessed conversation ID or a client-side cancellation is not persistence.</summary>
public sealed class VoiceGenerationReceipt(Guid sessionId, Guid conversationId, Guid userMessageId,
    string content, VoicePlaybackInterruption? playback)
{
    private readonly TaskCompletionSource settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long settledUtcTicks;
    private int ignored;
    private int readingStopped;
    private string retainedContent = content.Length <= 16384 ? content : content[^16384..];
    public Guid SessionId { get; } = sessionId;
    public Guid ConversationId { get; } = conversationId;
    public Guid UserMessageId { get; } = userMessageId;
    // Only the bounded recent user text, never PCM or generated output.
    public string Content => Volatile.Read(ref retainedContent);
    public VoicePlaybackInterruption? Playback { get; } = playback;
    public Task Settled => settled.Task;
    public bool Ignored => Volatile.Read(ref ignored) != 0;
    internal bool ReadingStopped => Volatile.Read(ref readingStopped) != 0;
    internal void MarkReadingStopped() => Volatile.Write(ref readingStopped, 1);
    internal void MarkIgnored()
    {
        // Keep the minimal ownership/settlement receipt, not the discarded speech.
        Volatile.Write(ref retainedContent, string.Empty);
        Volatile.Write(ref ignored, 1);
    }
    internal DateTimeOffset? SettledAt => Interlocked.Read(ref settledUtcTicks) is var ticks && ticks != 0
        ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
    internal void Settle()
    {
        Interlocked.CompareExchange(ref settledUtcTicks, DateTimeOffset.UtcNow.UtcTicks, 0);
        settled.TrySetResult();
    }
}

/// <summary>The validated receipt and cancellation captured under the same registry lock.</summary>
public sealed record VoiceGenerationHandoff(VoiceGenerationReceipt? Receipt, TurnCancellationInfo Cancellation);
