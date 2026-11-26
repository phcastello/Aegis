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
    private readonly Dictionary<Guid, PendingTurn> pendingTurns = [];
    private readonly Dictionary<Guid, VoiceGenerationReceipt> voiceGenerations = [];
    private long turnSequence;
    private readonly Timer cleanupTimer;
    private readonly AegisMetrics metrics;
    private bool stopping;

    public ActiveTurnRegistry(AegisMetrics metrics)
    {
        this.metrics = metrics;
        cleanupTimer = new(_ => Cleanup(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    // A speculative voice turn can be cancelled without making it the active turn.
    // A silent discard simply disposes the reservation, leaving the current turn untouched.
    public IDisposable TrackPendingTurn(Guid turnId, Guid conversationId, CancellationTokenSource cancellation, VoiceGenerationReceipt? receipt = null)
    {
        if (turnId == Guid.Empty || conversationId == Guid.Empty)
            throw new ArgumentException("Turn and conversation identifiers are required.");
        if (receipt is not null && (receipt.SessionId == Guid.Empty || receipt.ConversationId != conversationId))
            throw new ArgumentException("The voice receipt belongs to another session or conversation.");
        lock (gate)
        {
            ThrowIfStopping();
            CleanupUnsafe();
            if (turns.ContainsKey(turnId) || pendingTurns.ContainsKey(turnId) || voiceGenerations.ContainsKey(turnId))
                throw new InvalidOperationException("The turn identifier has already been used.");
            if (pendingTurns.Count >= MaxTurns)
                throw new InvalidOperationException("Too many pending turns.");
            if (receipt is not null && voiceGenerations.Count >= MaxTurns)
            {
                var oldest = voiceGenerations.Where(pair => pair.Value.SettledAt is not null &&
                        (!turns.TryGetValue(pair.Key, out var active) || !IsActiveUnsafe(active)))
                    .OrderBy(pair => pair.Value.SettledAt).FirstOrDefault();
                if (oldest.Value is null) throw new InvalidOperationException("Too many pending voice turns.");
                voiceGenerations.Remove(oldest.Key);
            }
            if (cancelledBeforeRegistration.ContainsKey(turnId)) cancellation.Cancel();
            var pending = new PendingTurn(conversationId, ++turnSequence, cancellation);
            pendingTurns.Add(turnId, pending);
            // The authorization/reservation and its handoff receipt are one atomic
            // registration. A rejected duplicate cannot publish a forged receipt.
            if (receipt is not null) voiceGenerations.Add(turnId, receipt);
            return new PendingRegistration(this, turnId, pending);
        }
    }

    private sealed record PendingTurn(Guid ConversationId, long Sequence, CancellationTokenSource Cancellation);

    public VoiceGenerationReceipt? ReadVoiceGeneration(Guid turnId, Guid sessionId, Guid? destination)
    {
        lock (gate)
        {
            ThrowIfStopping();
            return ValidateVoiceGenerationUnsafe(turnId, sessionId, destination);
        }
    }

    public VoiceGenerationHandoff BeginVoiceGenerationHandoff(Guid turnId, Guid sessionId, Guid? destination,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ThrowIfStopping();
            CleanupUnsafe();
            var receipt = ValidateVoiceGenerationUnsafe(turnId, sessionId, destination);
            // Validate ownership and reserve cancellation in one critical section.
            // If no prior registration exists, the tombstone prevents a late POST
            // from accepting while a successor is already being prepared.
            return new(receipt, CancelAndGetInfoUnsafe(turnId, "voice_generation_interrupted", activeSegmentsOnly: true));
        }
    }

    private VoiceGenerationReceipt? ValidateVoiceGenerationUnsafe(Guid turnId, Guid sessionId, Guid? destination)
    {
        if (turnId == Guid.Empty || sessionId == Guid.Empty)
            throw new ArgumentException("A interrupção da geração por voz é inválida.");
        var receipt = voiceGenerations.GetValueOrDefault(turnId);
        if (receipt is not null)
        {
            if (receipt.SessionId != sessionId || destination is { } requested && requested != receipt.ConversationId)
                throw new ArgumentException("A interrupção não pertence a esta sessão de voz.");
        }
        else if (turns.ContainsKey(turnId) || pendingTurns.ContainsKey(turnId))
            throw new ArgumentException("O turno interrompido não é uma geração desta sessão de voz.");
        return receipt;
    }

    private sealed class PendingRegistration(ActiveTurnRegistry registry, Guid turnId, PendingTurn pending) : IDisposable
    {
        public void Dispose()
        {
            lock (registry.gate)
            {
                if (registry.pendingTurns.TryGetValue(turnId, out var tracked) && ReferenceEquals(tracked, pending))
                    registry.pendingTurns.Remove(turnId);
            }
        }
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
            // A settled segmented receipt is also a used identifier even if its
            // speculative turn never became active. It may only be registered
            // by its own still-live pending reservation.
            if (turns.ContainsKey(turnId) || (voiceGenerations.ContainsKey(turnId) && !pendingTurns.ContainsKey(turnId)))
            {
                throw new InvalidOperationException("The turn identifier has already been used.");
            }

            pendingTurns.TryGetValue(turnId, out var pending);
            if (pending is not null && pending.ConversationId != conversationId)
                throw new InvalidOperationException("The pending turn belongs to another conversation.");
            pendingTurns.Remove(turnId);
            var turn = new ActiveTurn(turnId, conversationId);
            if (cancelledBeforeRegistration.Remove(turnId) || pending?.Cancellation.IsCancellationRequested == true)
            {
                CancelUnsafe(turn, "cancelled_before_registration");
                turns.Add(turnId, turn);
                return new TurnRegistrationInfo(turn, null);
            }

            // Only an accepted turn cancels older speculative candidates. A newer
            // reservation by itself does not interrupt speech or an active text turn.
            var acceptedSequence = pending?.Sequence ?? ++turnSequence;
            foreach (var candidate in pendingTurns.Values.Where(candidate =>
                candidate.ConversationId == conversationId && candidate.Sequence < acceptedSequence).ToList())
                candidate.Cancellation.Cancel();

            string? supersededNativeSpeechRequestId = null;
            IReadOnlyList<string>? supersededSegments = null;
            if (currentByConversation.TryGetValue(conversationId, out var priorId) &&
                turns.TryGetValue(priorId, out var prior))
            {
                supersededNativeSpeechRequestId = prior.NativeSpeechRequestId;
                supersededSegments = prior.SegmentRequests.Values.ToArray();
                CancelUnsafe(prior, "superseded_by_new_turn");
            }

            currentByConversation[conversationId] = turnId;
            turns.Add(turnId, turn);
            metrics.TurnsStarted.Add(1);
            metrics.ActiveTurnStarted();
            return new TurnRegistrationInfo(turn, supersededNativeSpeechRequestId, supersededSegments);
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

    public bool TryAuthorizeSpeechSegment(Guid turnId, FinalSpeechSegment segment)
    {
        lock (gate)
        {
            if (!turns.TryGetValue(turnId, out var turn) || !IsActiveUnsafe(turn) || turn.Status != TurnStatus.GeneratingText ||
                segment.Sequence != turn.SpeechSegments.Count || segment.Sequence >= 48 ||
                turn.SpeechSegments.Any(s => s.Id == segment.Id) ||
                (turn.AssistantMessageId is { } messageId && messageId != segment.AssistantMessageId)) return false;
            turn.AssistantMessageId = segment.AssistantMessageId;
            turn.SpeechSegments.Add(segment);
            return true;
        }
    }

    public FinalSpeechSegment? FindSpeechSegment(Guid turnId, Guid segmentId)
    {
        lock (gate)
            return turns.TryGetValue(turnId, out var turn) && IsActiveUnsafe(turn)
                ? turn.SpeechSegments.FirstOrDefault(s => s.Id == segmentId) : null;
    }

    public bool TryBeginSegmentSpeech(Guid turnId, Guid segmentId, Guid speechRequestId, string nativeRequestId)
    {
        lock (gate)
        {
            if (!turns.TryGetValue(turnId, out var turn) || !IsActiveUnsafe(turn) ||
                turn.Status is not (TurnStatus.GeneratingText or TurnStatus.TextCompleted) || turn.SpeechRequestId is not null ||
                turnBySpeechRequest.ContainsKey(speechRequestId) ||
                turn.SegmentRequests.Count - turn.CompletedSegmentRequests.Count >= 2) return false;
            var segment = turn.SpeechSegments.FirstOrDefault(s => s.Id == segmentId);
            if (segment is null || segment.Sequence != turn.SegmentRequests.Count) return false;
            turn.SegmentRequests[speechRequestId] = nativeRequestId;
            turnBySpeechRequest[speechRequestId] = turnId;
            return true;
        }
    }

    public bool CompleteSegmentSpeech(Guid speechRequestId)
    {
        lock (gate)
        {
            if (!turnBySpeechRequest.TryGetValue(speechRequestId, out var id) || !turns.TryGetValue(id, out var turn) ||
                !turn.SegmentRequests.ContainsKey(speechRequestId)) return false;
            turn.CompletedSegmentRequests.Add(speechRequestId);
            if (turn.TextCompletedAt is not null) turn.TextCompletedAt = DateTimeOffset.UtcNow;
            return true;
        }
    }

    public bool TrySetTextCompleted(Guid turnId, Guid assistantMessageId)
    {
        lock (gate)
        {
            if (!turns.TryGetValue(turnId, out var turn) || !IsActiveUnsafe(turn) ||
                turn.Status != TurnStatus.GeneratingText || (turn.AssistantMessageId is { } known && known != assistantMessageId))
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
                turn.Status != TurnStatus.TextCompleted || turn.SpeechSegments.Count > 0)
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
            var readingStopped = voiceGenerations.GetValueOrDefault(turnId)?.ReadingStopped == true;
            if (!IsActiveUnsafe(turn) || turn.Status != TurnStatus.TextCompleted || turn.SpeechRequestId is not null ||
                (turn.SpeechSegments.Count > 0 &&
                    ((!readingStopped && turn.SegmentRequests.Count != turn.SpeechSegments.Count) ||
                    turn.CompletedSegmentRequests.Count != turn.SegmentRequests.Count)))
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
        lock (gate) return CancelAndGetInfoUnsafe(turnId, reason);
    }

    private TurnCancellationInfo CancelAndGetInfoUnsafe(Guid turnId, string reason, bool activeSegmentsOnly = false)
    {
        if (!turns.TryGetValue(turnId, out var turn))
        {
            cancelledBeforeRegistration[turnId] = DateTimeOffset.UtcNow;
            TrimCancelledBeforeRegistrationUnsafe();
            if (pendingTurns.TryGetValue(turnId, out var pending)) pending.Cancellation.Cancel();
            return new TurnCancellationInfo(new CancelTurnResult(turnId, "cancelled", true, true), null);
        }

        // A native DELETE or old stream's finally may race with the handoff.
        // Receipt-backed segmented turns use the same unfinished-only cleanup
        // policy regardless of which of those cancellation paths arrived first.
        activeSegmentsOnly |= voiceGenerations.ContainsKey(turnId);

        // Recovering a receipt must not turn a completed/failed earlier response
        // into a new cancellation or decrement the active-turn metric twice.
        if (activeSegmentsOnly && turn.Status is TurnStatus.Completed or TurnStatus.Failed)
            return new TurnCancellationInfo(new CancelTurnResult(turnId, "cancelled", false, false), null);

        var segmentRequests = activeSegmentsOnly
            ? turn.SegmentRequests.Where(pair => !turn.CompletedSegmentRequests.Contains(pair.Key)).Select(pair => pair.Value).ToArray()
            : turn.SegmentRequests.Values.ToArray();
        var wasSpeech = turn.SpeechRequestId is not null || segmentRequests.Length > 0;
        var nativeSpeechRequestId = turn.NativeSpeechRequestId;
        var newlyCancelled = !turn.Cancellation.IsCancellationRequested;
        CancelUnsafe(turn, reason);
        return new TurnCancellationInfo(
            new CancelTurnResult(turnId, "cancelled", newlyCancelled, newlyCancelled && wasSpeech),
            nativeSpeechRequestId, segmentRequests);
    }

    public IReadOnlyList<TurnCancellationInfo> BeginShutdownAndCancelAll(string reason)
    {
        lock (gate)
        {
            stopping = true;
            foreach (var pending in pendingTurns.Values.ToList()) pending.Cancellation.Cancel();
            var cancellations = new List<TurnCancellationInfo>();
            foreach (var turn in turns.Values.Where(IsActiveUnsafe).ToList())
            {
                var wasSpeech = turn.SpeechRequestId is not null || turn.SegmentRequests.Count > 0;
                var nativeSpeechRequestId = turn.NativeSpeechRequestId;
                CancelUnsafe(turn, reason);
                cancellations.Add(new TurnCancellationInfo(
                    new CancelTurnResult(turn.TurnId, "cancelled", true, wasSpeech),
                    nativeSpeechRequestId, turn.SegmentRequests.Values.ToArray()));
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
            foreach (var pending in pendingTurns.Values.ToList()) pending.Cancellation.Cancel();
            pendingTurns.Clear();
            voiceGenerations.Clear();
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
        foreach (var turn in turns.Values.Where(turn => turn.Status == TurnStatus.TextCompleted && turn.SegmentRequests.Count == turn.CompletedSegmentRequests.Count && turn.TextCompletedAt < DateTimeOffset.UtcNow - TextCompletionGrace).ToList())
        {
            CompleteUnsafe(turn);
        }

        foreach (var pair in turns.Where(pair => pair.Value.CompletedAt < cutoff || pair.Value.CancelledAt < cutoff).ToList())
        {
            turns.Remove(pair.Key);
            if (pair.Value.SpeechRequestId is { } speechRequestId) turnBySpeechRequest.Remove(speechRequestId);
            foreach (var requestId in pair.Value.SegmentRequests.Keys) turnBySpeechRequest.Remove(requestId);
            pair.Value.Dispose();
        }

        foreach (var pair in cancelledBeforeRegistration.Where(pair => pair.Value < cutoff).ToList())
        {
            cancelledBeforeRegistration.Remove(pair.Key);
        }
        foreach (var pair in voiceGenerations.Where(pair => pair.Value.SettledAt < cutoff &&
            (!turns.TryGetValue(pair.Key, out var active) || !IsActiveUnsafe(active))).ToList())
            voiceGenerations.Remove(pair.Key);
        TrimCancelledBeforeRegistrationUnsafe();

        while (turns.Count > MaxTurns)
        {
            var oldest = turns.Values.OrderBy(turn => turn.CreatedAt).First();
            if (IsActiveUnsafe(oldest)) break;
            turns.Remove(oldest.TurnId);
            if (oldest.SpeechRequestId is { } speechRequestId) turnBySpeechRequest.Remove(speechRequestId);
            foreach (var requestId in oldest.SegmentRequests.Keys) turnBySpeechRequest.Remove(requestId);
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
