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
        Assert.Equal(first, second);
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

    private static ActiveTurnRegistry CreateRegistry() => new(new AegisMetrics());
}
