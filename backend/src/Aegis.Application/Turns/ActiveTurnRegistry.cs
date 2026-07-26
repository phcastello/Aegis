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
        => RegisterAndGetSuperseded(turnId, conversationId).Turn;

    public TurnRegistrationInfo RegisterAndGetSuperseded(Guid turnId, Guid conversationId)
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
                return new TurnRegistrationInfo(turn, null);
            }

            string? supersededNativeSpeechRequestId = null;
            if (currentByConversation.TryGetValue(conversationId, out var priorId) &&
                turns.TryGetValue(priorId, out var prior))
            {
                supersededNativeSpeechRequestId = prior.NativeSpeechRequestId;
                CancelUnsafe(prior, "superseded_by_new_turn");
            }

            currentByConversation[conversationId] = turnId;
            turns.Add(turnId, turn);
            metrics.TurnsStarted.Add(1);
            metrics.ActiveTurnStarted();
            return new TurnRegistrationInfo(turn, supersededNativeSpeechRequestId);
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
            CompleteUnsafe(turn);
        }
    }

    public bool TryCompleteWithoutSpeech(Guid turnId)
    {
        lock (gate)
        {
            if (!turns.TryGetValue(turnId, out var turn)) return false;
            if (turn.Status == TurnStatus.Completed) return true;
            if (!IsActiveUnsafe(turn) || turn.Status != TurnStatus.TextCompleted || turn.SpeechRequestId is not null)
            {
                return false;
            }

            CompleteUnsafe(turn);
            return true;
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
        => Task.FromResult(CancelAndGetInfo(turnId, reason, cancellationToken).Result);

    public TurnCancellationInfo CancelAndGetInfo(Guid turnId, string reason, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!turns.TryGetValue(turnId, out var turn))
            {
                cancelledBeforeRegistration[turnId] = DateTimeOffset.UtcNow;
                TrimCancelledBeforeRegistrationUnsafe();
                return new TurnCancellationInfo(new CancelTurnResult(turnId, "cancelled", true, true), null);
            }

            var wasSpeech = turn.SpeechRequestId is not null;
            var nativeSpeechRequestId = turn.NativeSpeechRequestId;
            var newlyCancelled = !turn.Cancellation.IsCancellationRequested;
            CancelUnsafe(turn, reason);
            return new TurnCancellationInfo(
                new CancelTurnResult(turnId, "cancelled", newlyCancelled, newlyCancelled && wasSpeech),
                nativeSpeechRequestId);
        }
    }

    public IReadOnlyList<TurnCancellationInfo> BeginShutdownAndCancelAll(string reason)
    {
        lock (gate)
        {
            stopping = true;
            var cancellations = new List<TurnCancellationInfo>();
            foreach (var turn in turns.Values.Where(IsActiveUnsafe).ToList())
            {
                var wasSpeech = turn.SpeechRequestId is not null;
                var nativeSpeechRequestId = turn.NativeSpeechRequestId;
                CancelUnsafe(turn, reason);
                cancellations.Add(new TurnCancellationInfo(
                    new CancelTurnResult(turn.TurnId, "cancelled", true, wasSpeech),
                    nativeSpeechRequestId));
            }

            return cancellations;
        }
    }

    public Task CancelAllAsync(string reason, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BeginShutdownAndCancelAll(reason);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        cleanupTimer.Dispose();
        lock (gate)
        {
            foreach (var turn in turns.Values) turn.Dispose();
            turns.Clear();
            currentByConversation.Clear();
            turnBySpeechRequest.Clear();
            cancelledBeforeRegistration.Clear();
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

    private void CompleteUnsafe(ActiveTurn turn)
    {
        turn.Status = TurnStatus.Completed;
        turn.CompletedAt = DateTimeOffset.UtcNow;
        metrics.TurnsCompleted.Add(1);
        metrics.ActiveTurnEnded();
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
            CompleteUnsafe(turn);
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
        TrimCancelledBeforeRegistrationUnsafe();

        while (turns.Count > MaxTurns)
        {
            var oldest = turns.Values.OrderBy(turn => turn.CreatedAt).First();
            if (IsActiveUnsafe(oldest)) break;
            turns.Remove(oldest.TurnId);
            if (oldest.SpeechRequestId is { } speechRequestId) turnBySpeechRequest.Remove(speechRequestId);
            oldest.Dispose();
        }
    }

    private void TrimCancelledBeforeRegistrationUnsafe()
    {
        while (cancelledBeforeRegistration.Count > MaxTurns)
        {
            var oldest = cancelledBeforeRegistration.MinBy(pair => pair.Value);
            cancelledBeforeRegistration.Remove(oldest.Key);
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
