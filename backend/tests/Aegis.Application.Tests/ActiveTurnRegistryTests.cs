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

    private static ActiveTurnRegistry CreateRegistry() => new(new AegisMetrics());
}
