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

    private static ActiveTurnRegistry CreateRegistry() => new(new AegisMetrics());
}
