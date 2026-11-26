using Aegis.Application.Observability;
using Aegis.Application.Turns;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class ActiveTurnRegistryTests
{
    [Fact]
    public void RegisteringANewTurnForTheConversationCancelsThePreviousTurn()
    {
        using var registry = CreateRegistry();
        var conversationId = Guid.NewGuid();
        var first = registry.Register(Guid.NewGuid(), conversationId);
        var second = registry.Register(Guid.NewGuid(), conversationId);

        Assert.True(first.Cancellation.IsCancellationRequested);
        Assert.Equal(TurnStatus.Cancelled, first.Status);
        Assert.True(registry.IsCurrent(conversationId, second.TurnId));
    }

    [Fact]
    public async Task CancellationIsIdempotent()
    {
        using var registry = CreateRegistry();
        var turn = registry.Register(Guid.NewGuid(), Guid.NewGuid());

        var first = await registry.CancelAsync(turn.TurnId, "test");
        var second = await registry.CancelAsync(turn.TurnId, "test");

        Assert.Equal("cancelled", first.Status);
        Assert.True(first.LlmCancellationRequested);
        Assert.False(second.LlmCancellationRequested);
        Assert.True(turn.Cancellation.IsCancellationRequested);
    }

    [Fact]
    public void InvalidTransitionsAreRejected()
    {
        using var registry = CreateRegistry();
        var turn = registry.Register(Guid.NewGuid(), Guid.NewGuid());

        Assert.False(registry.TrySetTextCompleted(turn.TurnId, Guid.NewGuid()));
        Assert.False(registry.TryTransition(turn.TurnId, TurnStatus.TextCompleted, TurnStatus.StreamingAudio));
        Assert.True(registry.TryTransition(turn.TurnId, TurnStatus.Created, TurnStatus.GeneratingText));
    }

    [Fact]
    public async Task CancellationBeforeRegistrationPreventsTheLateTurnFromStarting()
    {
        using var registry = CreateRegistry();
        var turnId = Guid.NewGuid();
        await registry.CancelAsync(turnId, "early_cancel");

        var turn = registry.Register(turnId, Guid.NewGuid());

        Assert.True(turn.Cancellation.IsCancellationRequested);
        Assert.Equal(TurnStatus.Cancelled, turn.Status);
    }

    [Fact]
    public async Task ConcurrentRegistrationsLeaveOnlyOneActiveTurn()
    {
        using var registry = CreateRegistry();
        var conversationId = Guid.NewGuid();
        var ids = Enumerable.Range(0, 32).Select(_ => Guid.NewGuid()).ToArray();

        await Task.WhenAll(ids.Select(id => Task.Run(() => registry.Register(id, conversationId))));

        var active = ids.Where(registry.IsActive).ToList();
        Assert.Single(active);
        Assert.True(registry.IsCurrent(conversationId, active[0]));
    }

    [Fact]
    public async Task ConversationsKeepIndependentCancellationAndCompletion()
    {
        using var registry = CreateRegistry();
        var first = registry.Register(Guid.NewGuid(), Guid.NewGuid());
        var second = registry.Register(Guid.NewGuid(), Guid.NewGuid());
        var third = registry.Register(Guid.NewGuid(), Guid.NewGuid());
        Assert.True(registry.TryTransition(first.TurnId, TurnStatus.Created, TurnStatus.GeneratingText));
        Assert.True(registry.TryTransition(second.TurnId, TurnStatus.Created, TurnStatus.GeneratingText));

        // The later conversation may finish while the first is still generating.
        Assert.True(registry.TrySetTextCompleted(second.TurnId, Guid.NewGuid()));
        Assert.True(registry.TryCompleteWithoutSpeech(second.TurnId));
        Assert.True(registry.IsActive(first.TurnId));
        await registry.CancelAsync(first.TurnId, "user_stop");

        Assert.True(first.Cancellation.IsCancellationRequested);
        Assert.False(second.Cancellation.IsCancellationRequested);
        Assert.Equal(TurnStatus.Completed, second.Status);
        Assert.True(registry.IsActive(third.TurnId));
        Assert.False(third.Cancellation.IsCancellationRequested);
    }

    [Fact]
    public void SupersessionStaysInsideTheOwningConversation()
    {
        using var registry = CreateRegistry();
        var firstConversation = Guid.NewGuid();
        var first = registry.Register(Guid.NewGuid(), firstConversation);
        var other = registry.Register(Guid.NewGuid(), Guid.NewGuid());

        var replacement = registry.Register(Guid.NewGuid(), firstConversation);

        Assert.True(first.Cancellation.IsCancellationRequested);
        Assert.True(registry.IsActive(replacement.TurnId));
        Assert.True(registry.IsActive(other.TurnId));
        Assert.False(other.Cancellation.IsCancellationRequested);
    }

    [Fact]
    public void CompletionIsTerminalAndIdempotent()
    {
        using var registry = CreateRegistry();
        var turn = registry.Register(Guid.NewGuid(), Guid.NewGuid());

        registry.Complete(turn.TurnId);
        registry.Complete(turn.TurnId);

        Assert.Equal(TurnStatus.Completed, turn.Status);
        Assert.False(registry.IsActive(turn.TurnId));
    }

    [Fact]
    public void CompleteWithoutSpeechOnlyAcceptsTextCompletedTurns()
    {
        using var registry = CreateRegistry();
        var turn = registry.Register(Guid.NewGuid(), Guid.NewGuid());

        Assert.False(registry.TryCompleteWithoutSpeech(turn.TurnId));
        Assert.True(registry.TryTransition(turn.TurnId, TurnStatus.Created, TurnStatus.GeneratingText));
        Assert.False(registry.TryCompleteWithoutSpeech(turn.TurnId));
        Assert.True(registry.TrySetTextCompleted(turn.TurnId, Guid.NewGuid()));
        Assert.True(registry.TryCompleteWithoutSpeech(turn.TurnId));
        Assert.Equal(TurnStatus.Completed, turn.Status);
    }

    [Fact]
    public void AtomicCancellationCapturesTheNativeSpeechRequest()
    {
        using var registry = CreateRegistry();
        var turn = registry.Register(Guid.NewGuid(), Guid.NewGuid());
        var speechRequestId = Guid.NewGuid();
        Assert.True(registry.TryTransition(turn.TurnId, TurnStatus.Created, TurnStatus.GeneratingText));
        Assert.True(registry.TrySetTextCompleted(turn.TurnId, Guid.NewGuid()));
        Assert.True(registry.TryBeginSpeech(turn.TurnId, speechRequestId, "01JATOMICNATIVESPEECHID000"));

        var cancellation = registry.CancelAndGetInfo(turn.TurnId, "test");

        Assert.True(cancellation.Result.SpeechCancellationRequested);
        Assert.Equal("01JATOMICNATIVESPEECHID000", cancellation.NativeSpeechRequestId);
        Assert.True(turn.Cancellation.IsCancellationRequested);
    }

    [Fact]
    public void ShutdownAtomicallyCancelsTurnsAndRejectsNewRegistrations()
    {
        using var registry = CreateRegistry();
        var existing = registry.Register(Guid.NewGuid(), Guid.NewGuid());

        var cancelled = registry.BeginShutdownAndCancelAll("shutdown");

        Assert.Single(cancelled);
        Assert.True(existing.Cancellation.IsCancellationRequested);
        Assert.Throws<InvalidOperationException>(() => registry.Register(Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public void NewRegistrationAtomicallyCapturesSupersededNativeSpeech()
    {
        using var registry = CreateRegistry();
        var conversationId = Guid.NewGuid();
        var first = registry.Register(Guid.NewGuid(), conversationId);
        Assert.True(registry.TryTransition(first.TurnId, TurnStatus.Created, TurnStatus.GeneratingText));
        Assert.True(registry.TrySetTextCompleted(first.TurnId, Guid.NewGuid()));
        Assert.True(registry.TryBeginSpeech(first.TurnId, Guid.NewGuid(), "01JSUPERSEDEDNATIVESPEECHID00"));

        var registration = registry.RegisterAndGetSuperseded(Guid.NewGuid(), conversationId);

        Assert.Equal("01JSUPERSEDEDNATIVESPEECHID00", registration.SupersededNativeSpeechRequestId);
        Assert.True(first.Cancellation.IsCancellationRequested);
        Assert.True(registry.IsCurrent(conversationId, registration.Turn.TurnId));
    }

    [Fact]
    public void PendingDiscardLeavesTheCurrentTurnUntouchedAndReleasesReservation()
    {
        using var registry = CreateRegistry();
        var conversation = Guid.NewGuid();
        var active = registry.Register(Guid.NewGuid(), conversation);
        var pendingId = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();

        using (registry.TrackPendingTurn(pendingId, conversation, cancellation))
        {
            Assert.True(registry.IsCurrent(conversation, active.TurnId));
            Assert.Null(registry.Find(pendingId));
            Assert.False(cancellation.IsCancellationRequested);
        }

        using var second = registry.TrackPendingTurn(pendingId, conversation, cancellation);
        Assert.True(registry.IsCurrent(conversation, active.TurnId));
    }

    [Fact]
    public void NewAcceptedTurnCancelsOlderCandidatesInItsConversationOnly()
    {
        using var registry = CreateRegistry();
        var conversation = Guid.NewGuid();
        using var oldCancellation = new CancellationTokenSource();
        using var newerCancellation = new CancellationTokenSource();
        using var otherCancellation = new CancellationTokenSource();
        var oldId = Guid.NewGuid();
        var newerId = Guid.NewGuid();
        using var old = registry.TrackPendingTurn(oldId, conversation, oldCancellation);
        using var newer = registry.TrackPendingTurn(newerId, conversation, newerCancellation);
        using var other = registry.TrackPendingTurn(Guid.NewGuid(), Guid.NewGuid(), otherCancellation);

        var accepted = registry.Register(newerId, conversation);
        Assert.True(oldCancellation.IsCancellationRequested);
        Assert.False(newerCancellation.IsCancellationRequested);
        Assert.False(otherCancellation.IsCancellationRequested);
        Assert.Equal(TurnStatus.Cancelled, registry.Register(oldId, conversation).Status);
        Assert.True(registry.IsCurrent(conversation, accepted.TurnId));
    }

    [Fact]
    public void CancelPendingTurnInterruptsCandidateAndKeepsCurrentTurn()
    {
        using var registry = CreateRegistry();
        var conversation = Guid.NewGuid();
        var active = registry.Register(Guid.NewGuid(), conversation);
        var pendingId = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        using var reservation = registry.TrackPendingTurn(pendingId, conversation, cancellation);

        registry.CancelAndGetInfo(pendingId, "user_stop");

        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(registry.IsCurrent(conversation, active.TurnId));
        Assert.Equal(TurnStatus.Cancelled, registry.Register(pendingId, conversation).Status);
        Assert.True(registry.IsCurrent(conversation, active.TurnId));
    }

    [Fact]
    public void RejectedDuplicateCannotCreateAReceiptOrAuthorizeHandoffOfAnotherTurn()
    {
        using var registry = CreateRegistry();
        var conversation = Guid.NewGuid(); var id = Guid.NewGuid(); var session = Guid.NewGuid();
        var existing = registry.Register(id, conversation);
        using var cancellation = new CancellationTokenSource();
        var receipt = new VoiceGenerationReceipt(session, conversation, Guid.NewGuid(), "unrelated", null);
        Assert.Throws<InvalidOperationException>(() => registry.TrackPendingTurn(id, conversation, cancellation, receipt));
        Assert.Throws<ArgumentException>(() => registry.BeginVoiceGenerationHandoff(id, session, conversation));
        Assert.False(existing.Cancellation.IsCancellationRequested);
        Assert.True(registry.IsCurrent(conversation, id));
    }

    [Fact]
    public void HandoffCannotCancelPendingLegacyOrAnotherVoiceSessionButCanCancelItsOwn()
    {
        using var registry = CreateRegistry();
        var conversation = Guid.NewGuid(); var session = Guid.NewGuid();
        var legacyId = Guid.NewGuid(); var voiceId = Guid.NewGuid();
        using var legacy = new CancellationTokenSource(); using var voice = new CancellationTokenSource();
        using var legacyReservation = registry.TrackPendingTurn(legacyId, conversation, legacy);
        var receipt = new VoiceGenerationReceipt(session, conversation, Guid.NewGuid(), "request", null);
        using var voiceReservation = registry.TrackPendingTurn(voiceId, conversation, voice, receipt);
        Assert.Throws<ArgumentException>(() => registry.BeginVoiceGenerationHandoff(legacyId, session, conversation));
        Assert.Throws<ArgumentException>(() => registry.BeginVoiceGenerationHandoff(voiceId, Guid.NewGuid(), conversation));
        Assert.Throws<ArgumentException>(() => registry.BeginVoiceGenerationHandoff(voiceId, session, Guid.NewGuid()));
        Assert.False(legacy.IsCancellationRequested); Assert.False(voice.IsCancellationRequested);
        var handoff = registry.BeginVoiceGenerationHandoff(voiceId, session, conversation);
        Assert.Same(receipt, handoff.Receipt);
        Assert.True(voice.IsCancellationRequested); Assert.False(legacy.IsCancellationRequested);
    }

    [Fact]
    public void UnknownHandoffTombstonesLateRegistrationAndCannotBeReusedAfterRelease()
    {
        using var registry = CreateRegistry();
        var id = Guid.NewGuid(); var conversation = Guid.NewGuid(); var session = Guid.NewGuid();
        Assert.Null(registry.BeginVoiceGenerationHandoff(id, session, null).Receipt);
        using var cancellation = new CancellationTokenSource();
        var receipt = new VoiceGenerationReceipt(session, conversation, Guid.NewGuid(), "late", null);
        using (registry.TrackPendingTurn(id, conversation, cancellation, receipt))
            Assert.True(cancellation.IsCancellationRequested);
        Assert.Throws<InvalidOperationException>(() => registry.TrackPendingTurn(id, conversation, cancellation, receipt));
        Assert.Throws<InvalidOperationException>(() => registry.Register(id, conversation));
    }

    [Fact]
    public void HandoffOnlyCancelsUnfinishedSpeechAndDoesNotRewriteCompletedTurn()
    {
        using var registry = CreateRegistry();
        var id = Guid.NewGuid(); var session = Guid.NewGuid(); var conversation = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        var receipt = new VoiceGenerationReceipt(session, conversation, Guid.NewGuid(), "request", null);
        using var pending = registry.TrackPendingTurn(id, conversation, cancellation, receipt);
        var turn = registry.Register(id, conversation);
        Assert.True(registry.TryTransition(id, TurnStatus.Created, TurnStatus.GeneratingText));
        var assistant = Guid.NewGuid(); var first = new FinalSpeechSegment(Guid.NewGuid(), 0, assistant, "first");
        var second = new FinalSpeechSegment(Guid.NewGuid(), 1, assistant, "second");
        Assert.True(registry.TryAuthorizeSpeechSegment(id, first)); Assert.True(registry.TryAuthorizeSpeechSegment(id, second));
        var firstRequest = Guid.NewGuid(); var secondRequest = Guid.NewGuid();
        Assert.True(registry.TryBeginSegmentSpeech(id, first.Id, firstRequest, "native-first"));
        Assert.True(registry.TryBeginSegmentSpeech(id, second.Id, secondRequest, "native-second"));
        Assert.True(registry.CompleteSegmentSpeech(firstRequest));
        var handoff = registry.BeginVoiceGenerationHandoff(id, session, conversation);
        Assert.Equal(new[] { "native-second" }, handoff.Cancellation.SegmentNativeSpeechRequestIds);
        Assert.Equal(new[] { "native-second" }, registry.CancelAndGetInfo(id, "old_stream_cleanup").SegmentNativeSpeechRequestIds);
        Assert.Equal(TurnStatus.Cancelled, turn.Status);

        var completedId = Guid.NewGuid();
        using var completedToken = new CancellationTokenSource();
        using var completedPending = registry.TrackPendingTurn(completedId, conversation, completedToken,
            new(session, conversation, Guid.NewGuid(), "finished", null));
        var completed = registry.Register(completedId, conversation);
        registry.Complete(completedId);
        Assert.Equal(TurnStatus.Completed, completed.Status);
        var completedHandoff = registry.BeginVoiceGenerationHandoff(completedId, session, conversation);
        Assert.False(completedHandoff.Cancellation.Result.LlmCancellationRequested);
        Assert.Equal(TurnStatus.Completed, completed.Status);
    }

    [Fact]
    public void ProvisionalReservationRejectsAnotherSessionAndPreservesPredecessorAfterCandidateCancellation()
    {
        using var registry = CreateRegistry();
        var conversation = Guid.NewGuid();
        var session = Guid.NewGuid();
        var predecessorId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();
        using var earlier = new CancellationTokenSource();
        var priorReceipt = new VoiceGenerationReceipt(session, conversation, Guid.NewGuid(), "anterior", null);
        using var priorReservation = registry.TrackPendingTurn(predecessorId, conversation, earlier, priorReceipt);
        var predecessor = registry.Register(predecessorId, conversation);
        using var candidate = new CancellationTokenSource();
        var candidateReceipt = new VoiceGenerationReceipt(session, conversation, Guid.NewGuid(), "nova fala", null);
        Assert.Throws<ArgumentException>(() => registry.TrackProvisionalVoiceInterruption(candidateId, predecessorId,
            Guid.NewGuid(), conversation, candidate, candidateReceipt));
        Assert.Throws<ArgumentException>(() => registry.TrackProvisionalVoiceInterruption(candidateId, Guid.NewGuid(),
            session, conversation, candidate, candidateReceipt));
        var tracked = registry.TrackProvisionalVoiceInterruption(candidateId, predecessorId,
            session, conversation, candidate, candidateReceipt);
        using var reservation = tracked.Reservation;
        Assert.Same(priorReceipt, tracked.Predecessor);
        Assert.Throws<InvalidOperationException>(() => registry.Register(candidateId, conversation));
        Assert.Throws<ArgumentException>(() => registry.BeginVoiceGenerationHandoff(candidateId, session, conversation));
        Assert.True(registry.IsCurrent(conversation, predecessorId));
        registry.CancelAndGetInfo(candidateId, "explicit");
        Assert.Throws<OperationCanceledException>(() => registry.CommitProvisionalVoiceInterruption(
            candidateId, predecessorId, session, conversation));
        Assert.False(predecessor.Cancellation.IsCancellationRequested);
        Assert.True(registry.IsCurrent(conversation, predecessorId));
    }

    [Fact]
    public void ProvisionalReservationCannotCancelAReplacementTurnOrCommitTwice()
    {
        using var registry = CreateRegistry();
        var conversation = Guid.NewGuid();
        var session = Guid.NewGuid();
        var predecessorId = Guid.NewGuid();
        using var earlier = new CancellationTokenSource();
        using var priorReservation = registry.TrackPendingTurn(predecessorId, conversation, earlier,
            new VoiceGenerationReceipt(session, conversation, Guid.NewGuid(), "anterior", null));
        var predecessor = registry.Register(predecessorId, conversation);
        var candidateId = Guid.NewGuid();
        using var candidate = new CancellationTokenSource();
        using var reservation = registry.TrackProvisionalVoiceInterruption(candidateId, predecessorId,
            session, conversation, candidate,
            new VoiceGenerationReceipt(session, conversation, Guid.NewGuid(), "nova fala", null)).Reservation;
        var handoff = registry.CommitProvisionalVoiceInterruption(candidateId, predecessorId, session, conversation);
        Assert.True(handoff.Cancellation.Result.LlmCancellationRequested);
        Assert.True(predecessor.Cancellation.IsCancellationRequested);
        Assert.Throws<OperationCanceledException>(() => registry.CommitProvisionalVoiceInterruption(
            candidateId, predecessorId, session, conversation));

        var replacement = registry.Register(Guid.NewGuid(), conversation);
        Assert.True(candidate.IsCancellationRequested);
        var lateCandidate = registry.Register(candidateId, conversation);
        Assert.True(lateCandidate.Cancellation.IsCancellationRequested);
        Assert.True(registry.IsCurrent(conversation, replacement.TurnId));
        Assert.False(replacement.Cancellation.IsCancellationRequested);
    }

    private static ActiveTurnRegistry CreateRegistry() => new(new AegisMetrics());
}
