using Aegis.Application.Observability;

namespace Aegis.Application.Turns;

/// <summary>In-memory, process-local ownership for cancellable chat and speech turns.</summary>
public sealed class ActiveTurnRegistry : IActiveTurnRegistry, IDisposable
{
    private const int MaxTurns = 512;
    private static readonly TimeSpan TextCompletionGrace = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TerminalRetention = TimeSpan.FromMinutes(10);
    private readonly object gate = new();
    private readonly Dictionary<Guid, ActiveTurn> turns = [];
    private readonly Dictionary<Guid, Guid> currentByConversation = [];
    private readonly Dictionary<Guid, Guid> turnBySpeechRequest = [];
    private readonly Dictionary<Guid, DateTimeOffset> cancelledBeforeRegistration = [];
    private readonly Timer cleanupTimer;
    private readonly AegisMetrics metrics;
    private bool stopping;

    public ActiveTurnRegistry(AegisMetrics metrics)
    {
        this.metrics = metrics;
        cleanupTimer = new(_ => Cleanup(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public ActiveTurn Register(Guid turnId, Guid conversationId)
    {
        if (turnId == Guid.Empty || conversationId == Guid.Empty)
        {
            throw new ArgumentException("Turn and conversation identifiers are required.");
        }

        lock (gate)
        {
            ThrowIfStopping();
            CleanupUnsafe();
            if (turns.ContainsKey(turnId))
            {
                throw new InvalidOperationException("The turn identifier has already been used.");
            }

            var turn = new ActiveTurn(turnId, conversationId);
            if (cancelledBeforeRegistration.Remove(turnId))
            {
                CancelUnsafe(turn, "cancelled_before_registration");
                turns.Add(turnId, turn);
                return turn;
            }

            if (currentByConversation.TryGetValue(conversationId, out var priorId) &&
                turns.TryGetValue(priorId, out var prior))
            {
                CancelUnsafe(prior, "superseded_by_new_turn");
            }

            currentByConversation[conversationId] = turnId;
            turns.Add(turnId, turn);
            metrics.TurnsStarted.Add(1);
            metrics.ActiveTurnStarted();
            return turn;
        }
    }

    public ActiveTurn? Find(Guid turnId)
    {
        lock (gate)
        {
            turns.TryGetValue(turnId, out var turn);
            return turn;
        }
    }

    public ActiveTurn? FindBySpeechRequest(Guid speechRequestId)
    {
        lock (gate)
        {
            return turnBySpeechRequest.TryGetValue(speechRequestId, out var turnId) && turns.TryGetValue(turnId, out var turn)
                ? turn
                : null;
        }
    }

    public TurnCancellationInfo? GetCancellationInfo(Guid turnId)
    {
        lock (gate)
        {
            return turns.TryGetValue(turnId, out var turn)
                ? new TurnCancellationInfo(turn.TurnId, turn.NativeSpeechRequestId)
                : null;
        }
    }

    public IReadOnlyList<Guid> GetActiveTurnIds()
    {
        lock (gate)
        {
            return turns.Values.Where(IsActiveUnsafe).Select(turn => turn.TurnId).ToList();
        }
    }

    public bool IsCurrent(Guid conversationId, Guid turnId)
    {
        lock (gate)
        {
            return currentByConversation.TryGetValue(conversationId, out var current) && current == turnId &&
                turns.TryGetValue(turnId, out var turn) && !turn.Cancellation.IsCancellationRequested;
        }
    }

    public bool IsActive(Guid turnId)
    {
        lock (gate)
        {
            return turns.TryGetValue(turnId, out var turn) && IsActiveUnsafe(turn);
        }
    }

    public bool TryTransition(Guid turnId, TurnStatus expected, TurnStatus next)
    {
        lock (gate)
        {
            if (!turns.TryGetValue(turnId, out var turn) || turn.Status != expected || !IsActiveUnsafe(turn))
            {
                return false;
            }

            turn.Status = next;
            if (next == TurnStatus.GeneratingText) turn.TextStartedAt = DateTimeOffset.UtcNow;
            return true;
        }
    }

    public bool TrySetTextCompleted(Guid turnId, Guid assistantMessageId)
    {
        lock (gate)
        {
            if (!turns.TryGetValue(turnId, out var turn) || !IsActiveUnsafe(turn) ||
                turn.Status != TurnStatus.GeneratingText)
            {
                return false;
            }

            turn.AssistantMessageId = assistantMessageId;
            turn.TextCompletedAt = DateTimeOffset.UtcNow;
            turn.Status = TurnStatus.TextCompleted;
            return true;
        }
    }

    public bool TryBeginSpeech(Guid turnId, Guid speechRequestId, string nativeSpeechRequestId)
    {
        lock (gate)
        {
            if (!turns.TryGetValue(turnId, out var turn) || !IsActiveUnsafe(turn) ||
                turn.Status != TurnStatus.TextCompleted)
            {
                return false;
            }

            if (turn.SpeechRequestId is not null && turn.SpeechRequestId != speechRequestId)
            {
                return false;
            }

            if (turnBySpeechRequest.TryGetValue(speechRequestId, out var owner) && owner != turnId)
            {
                return false;
            }

            turn.SpeechRequestId = speechRequestId;
            turnBySpeechRequest[speechRequestId] = turnId;
            turn.NativeSpeechRequestId = nativeSpeechRequestId;
            turn.Status = TurnStatus.RequestingSpeech;
            return true;
        }
    }

    public bool TrySetStreamingAudio(Guid turnId, Guid speechRequestId)
    {
        lock (gate)
        {
            if (!turns.TryGetValue(turnId, out var turn) || !IsActiveUnsafe(turn) ||
                turn.SpeechRequestId != speechRequestId || turn.Status != TurnStatus.RequestingSpeech)
            {
                return false;
            }

            turn.Status = TurnStatus.StreamingAudio;
            turn.SpeechStartedAt = DateTimeOffset.UtcNow;
            return true;
        }
    }

    public void Complete(Guid turnId)
    {
        lock (gate)
        {
            if (!turns.TryGetValue(turnId, out var turn) || !IsActiveUnsafe(turn))
            {
                return;
            }

            turn.Status = TurnStatus.Completed;
            turn.CompletedAt = DateTimeOffset.UtcNow;
            metrics.TurnsCompleted.Add(1);
            metrics.ActiveTurnEnded();
            RemoveCurrentUnsafe(turn);
        }
    }

    public void Fail(Guid turnId)
    {
        lock (gate)
        {
            if (!turns.TryGetValue(turnId, out var turn) || !IsActiveUnsafe(turn)) return;
            turn.Status = TurnStatus.Failed;
            turn.CompletedAt = DateTimeOffset.UtcNow;
            metrics.TurnsFailed.Add(1);
            metrics.ActiveTurnEnded();
            RemoveCurrentUnsafe(turn);
        }
    }

    public Task<CancelTurnResult> CancelAsync(Guid turnId, string reason, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!turns.TryGetValue(turnId, out var turn))
            {
                cancelledBeforeRegistration[turnId] = DateTimeOffset.UtcNow;
                return Task.FromResult(new CancelTurnResult(turnId, "cancelled", true, true));
            }

            var wasSpeech = turn.SpeechRequestId is not null;
            var newlyCancelled = !turn.Cancellation.IsCancellationRequested;
            CancelUnsafe(turn, reason);
            return Task.FromResult(new CancelTurnResult(turnId, "cancelled", newlyCancelled, newlyCancelled && wasSpeech));
        }
    }

    public async Task CancelAllAsync(string reason, CancellationToken cancellationToken = default)
    {
        Guid[] ids;
        lock (gate)
        {
            stopping = true;
            ids = turns.Keys.ToArray();
        }

        foreach (var id in ids)
        {
            await CancelAsync(id, reason, cancellationToken);
        }
    }

    public void Dispose()
    {
        cleanupTimer.Dispose();
        lock (gate)
        {
            foreach (var turn in turns.Values) turn.Dispose();
            turns.Clear();
            currentByConversation.Clear();
        }
    }

    private void CancelUnsafe(ActiveTurn turn, string reason)
    {
        if (!turn.Cancellation.IsCancellationRequested)
        {
            turn.Status = TurnStatus.Cancelling;
            turn.Cancellation.Cancel();
            turn.CancelledAt = DateTimeOffset.UtcNow;
            turn.Status = TurnStatus.Cancelled;
            metrics.TurnsCancelled.Add(1);
            metrics.LlmCancellations.Add(1);
            metrics.TurnCancellationSeconds.Record((DateTimeOffset.UtcNow - turn.CreatedAt).TotalSeconds);
            metrics.ActiveTurnEnded();
        }

        RemoveCurrentUnsafe(turn);
    }

    private static bool IsActiveUnsafe(ActiveTurn turn) => !turn.Cancellation.IsCancellationRequested &&
        turn.Status is not (TurnStatus.Cancelled or TurnStatus.Cancelling or TurnStatus.Failed or TurnStatus.Completed);

    private void Cleanup()
    {
        lock (gate) CleanupUnsafe();
    }

    private void CleanupUnsafe()
    {
        var cutoff = DateTimeOffset.UtcNow - TerminalRetention;
        foreach (var turn in turns.Values.Where(turn => turn.Status == TurnStatus.TextCompleted && turn.TextCompletedAt < DateTimeOffset.UtcNow - TextCompletionGrace).ToList())
        {
            Complete(turn.TurnId);
        }

        foreach (var pair in turns.Where(pair => pair.Value.CompletedAt < cutoff || pair.Value.CancelledAt < cutoff).ToList())
        {
            turns.Remove(pair.Key);
            if (pair.Value.SpeechRequestId is { } speechRequestId) turnBySpeechRequest.Remove(speechRequestId);
            pair.Value.Dispose();
        }

        foreach (var pair in cancelledBeforeRegistration.Where(pair => pair.Value < cutoff).ToList())
        {
            cancelledBeforeRegistration.Remove(pair.Key);
        }

        while (turns.Count > MaxTurns)
        {
            var oldest = turns.Values.OrderBy(turn => turn.CreatedAt).First();
            if (IsActiveUnsafe(oldest)) break;
            turns.Remove(oldest.TurnId);
            if (oldest.SpeechRequestId is { } speechRequestId) turnBySpeechRequest.Remove(speechRequestId);
            oldest.Dispose();
        }
    }

    private void RemoveCurrentUnsafe(ActiveTurn turn)
    {
        if (currentByConversation.TryGetValue(turn.ConversationId, out var current) && current == turn.TurnId)
        {
            currentByConversation.Remove(turn.ConversationId);
        }
    }

    private void ThrowIfStopping()
    {
        if (stopping) throw new InvalidOperationException("Aegis is shutting down and cannot accept a new turn.");
    }
}
