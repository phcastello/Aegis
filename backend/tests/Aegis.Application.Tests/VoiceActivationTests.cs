using Aegis.Api.Nodes;
using Aegis.Application.Nodes;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class VoiceActivationTests
{
    // Deterministic race tests advance monotonic time, then submit another HTTP-like
    // operation to force deadline processing. The API integration test covers the timer.
    private sealed class Clock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public void Advance(TimeSpan amount) => timestamp += amount.Ticks;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => new Timer(callback, state, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private static readonly Guid Phone = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Desktop = Guid.Parse("00000000-0000-0000-0000-000000000002");

    private static Task<VoiceActivationDecision> Claim(VoiceActivationArbiter arbiter, Guid node, Guid? activation = null,
        double? snr = null, double? confidence = null, int priority = 0, CancellationToken ct = default) =>
        arbiter.ClaimAsync(node, new(activation ?? Guid.NewGuid(), snr, confidence), priority, ct);

    private static void Settle(Clock clock, VoiceActivationArbiter arbiter)
    {
        clock.Advance(VoiceActivationArbiter.ElectionWindow);
        arbiter.Abandon(Guid.NewGuid(), Guid.NewGuid());
    }

    [Fact]
    public async Task StrongerLaterNodeWinsOnceAndExactRetriesShareTheSameDecision()
    {
        var clock = new Clock();
        var arbiter = new VoiceActivationArbiter(clock);
        var commonId = Guid.NewGuid(); // key is (authenticated node, local activation)
        var first = Claim(arbiter, Phone, commonId, snr: 5, confidence: 1, priority: 100);
        var second = Claim(arbiter, Desktop, commonId, snr: 15, confidence: 0.8);
        var concurrentRetry = Claim(arbiter, Desktop, commonId, snr: -40);
        Assert.Equal("suppressed", (await Claim(arbiter, Desktop, snr: 80)).Decision);
        Settle(clock, arbiter);
        Assert.Equal("suppressed", (await first).Decision);
        Assert.Equal("granted", (await second).Decision);
        Assert.Equal(await second, await concurrentRetry);
        Assert.Equal(await second, await Claim(arbiter, Desktop, commonId));
        arbiter.Abandon(Desktop, commonId);
        Assert.Equal("suppressed", (await Claim(arbiter, Phone)).Decision);
        clock.Advance(VoiceActivationArbiter.CatchUpWindow);
        Assert.Equal("suppressed", (await Claim(arbiter, Desktop, commonId)).Decision);
        var next = Claim(arbiter, Phone);
        Settle(clock, arbiter);
        Assert.Equal("granted", (await next).Decision);
    }

    [Fact]
    public async Task ASingleKnownPairedNodeReceivesItsGrantWithoutWaitingForTheWindow()
    {
        var clock = new Clock();
        var arbiter = new VoiceActivationArbiter(clock);
        var activation = Guid.NewGuid();
        var only = arbiter.ClaimAsync(Phone, new(activation), 0, default, [Phone]);
        Assert.True(only.IsCompletedSuccessfully);
        Assert.Equal("granted", (await only).Decision);
        Assert.Equal("granted", (await arbiter.ClaimAsync(Phone, new(activation), 0, default, [Phone])).Decision);
        Assert.Equal("suppressed", (await arbiter.ClaimAsync(Desktop, new(Guid.NewGuid()), 0, default, [Phone, Desktop])).Decision);
    }

    [Fact]
    public async Task EarlyDecisionWaitsForEveryKnownNodeAndRetainsTheQualityRanking()
    {
        var clock = new Clock();
        var arbiter = new VoiceActivationArbiter(clock);
        Guid[] participants = [Phone, Desktop];
        var desktop = arbiter.ClaimAsync(Desktop, new(Guid.NewGuid(), 6), 0, default, participants);
        Assert.False(desktop.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(400));
        var phone = arbiter.ClaimAsync(Phone, new(Guid.NewGuid(), 20), 0, default, participants);
        // No clock advancement to the 650 ms deadline is needed.
        Assert.Equal("granted", (await phone.WaitAsync(TimeSpan.FromSeconds(1))).Decision);
        Assert.Equal("suppressed", (await desktop.WaitAsync(TimeSpan.FromSeconds(1))).Decision);
        Assert.Equal("suppressed", (await Claim(arbiter, Guid.NewGuid(), snr: 100)).Decision);
    }

    [Fact]
    public async Task OfflineOrUncertainInventoryRetainsTheFullElectionWindow()
    {
        var clock = new Clock();
        var arbiter = new VoiceActivationArbiter(clock);
        var pending = arbiter.ClaimAsync(Desktop, new(Guid.NewGuid(), 6), 0, default, [Phone, Desktop]);
        Assert.False(pending.IsCompleted);
        // A new snapshot that omits the phone cannot erase its possible claim.
        var third = Guid.NewGuid();
        var another = arbiter.ClaimAsync(third, new(Guid.NewGuid(), 10), 0, default, [Desktop, third]);
        Assert.False(another.IsCompleted);
        Assert.False(pending.IsCompleted);
        Settle(clock, arbiter);
        Assert.Equal("granted", (await another).Decision);
        Assert.Equal("suppressed", (await pending).Decision);
        clock.Advance(VoiceActivationArbiter.CatchUpWindow);
        var unknown = arbiter.ClaimAsync(Phone, new(Guid.NewGuid()), 0, default, null);
        Assert.False(unknown.IsCompleted);
        Settle(clock, arbiter);
        Assert.Equal("granted", (await unknown).Decision);
    }

    [Fact]
    public async Task StrongerNodeWithMeasuredDetectorSkewRemainsEligibleUntilDeadline()
    {
        var clock = new Clock();
        var arbiter = new VoiceActivationArbiter(clock);
        var desktop = Claim(arbiter, Desktop, snr: 6);
        var measuredSkew = TimeSpan.FromMilliseconds(440);
        clock.Advance(measuredSkew);
        var phone = Claim(arbiter, Phone, snr: 20);
        Assert.False(phone.IsCompleted);
        clock.Advance(VoiceActivationArbiter.ElectionWindow - measuredSkew);
        var late = await Claim(arbiter, Guid.NewGuid(), snr: 40);
        Assert.Equal("suppressed", late.Decision);
        Assert.Equal("suppressed", (await desktop).Decision);
        Assert.Equal("granted", (await phone).Decision);
    }

    [Fact]
    public async Task AnyNumberOfNodesCanCompeteAndOnlyOneReceivesAGrant()
    {
        var clock = new Clock();
        var arbiter = new VoiceActivationArbiter(clock);
        var nodes = Enumerable.Range(0, 32).Select(_ => Guid.NewGuid()).ToArray();
        var requests = nodes.Select((node, i) => Claim(arbiter, node, priority: i)).ToArray();
        Settle(clock, arbiter);
        var decisions = await Task.WhenAll(requests);
        Assert.Single(decisions, decision => decision.Decision == "granted");
        Assert.Equal("granted", decisions[^1].Decision);
        Assert.Equal("suppressed", (await Claim(arbiter, Guid.NewGuid())).Decision);
    }

    [Theory]
    [InlineData(10.0, 12.0, 0.9, 0.6, 0, 10, true)]
    [InlineData(10.0, 12.0, 0.9, 0.89, -2, 10, false)]
    [InlineData(null, 24.0, null, 0.99, 0, 0, true)]
    public async Task SimilarOrUnavailableAcousticsUseConfidenceThenPriorityThenStableNodeId(
        double? phoneSnr, double? pcSnr, double? phoneConfidence, double? pcConfidence,
        int phonePriority, int pcPriority, bool phoneWins)
    {
        var clock = new Clock();
        var arbiter = new VoiceActivationArbiter(clock);
        var pc = Claim(arbiter, Desktop, snr: pcSnr, confidence: pcConfidence, priority: pcPriority);
        var phone = Claim(arbiter, Phone, snr: phoneSnr, confidence: phoneConfidence, priority: phonePriority);
        Settle(clock, arbiter);
        Assert.Equal(phoneWins ? "granted" : "suppressed", (await phone).Decision);
        Assert.Equal(phoneWins ? "suppressed" : "granted", (await pc).Decision);
    }

    [Fact]
    public async Task CancelDoesNotRetireAnotherNodesClaimOrAnotherWaiterAndAbandonMayArriveFirst()
    {
        var clock = new Clock();
        var arbiter = new VoiceActivationArbiter(clock);
        using var stop = new CancellationTokenSource();
        var attempt = Guid.NewGuid();
        var first = Claim(arbiter, Phone, attempt, ct: stop.Token);
        var retry = Claim(arbiter, Phone, attempt);
        arbiter.Abandon(Desktop, attempt);
        Assert.Equal("suppressed", (await Claim(arbiter, Desktop, attempt)).Decision);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Settle(clock, arbiter);
        Assert.Equal("granted", (await retry).Decision);
    }

    [Fact]
    public async Task WithdrawnOrDisconnectedWinnerCannotStealTheElection()
    {
        var clock = new Clock();
        var arbiter = new VoiceActivationArbiter(clock);
        using var stop = new CancellationTokenSource();
        var first = Claim(arbiter, Phone, snr: 50, ct: stop.Token);
        var fallback = Claim(arbiter, Desktop, snr: 3);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Settle(clock, arbiter);
        Assert.Equal("granted", (await fallback).Decision);
    }

    [Fact]
    public async Task RestartAndExpiredRequestsFailClosed()
    {
        var clock = new Clock();
        var arbiter = new VoiceActivationArbiter(clock);
        await arbiter.StartAsync(default);
        Assert.Equal("suppressed", (await Claim(arbiter, Desktop)).Decision);
        clock.Advance(VoiceActivationArbiter.CatchUpWindow);
        var stale = Claim(arbiter, Desktop);
        clock.Advance(TimeSpan.FromSeconds(5)); // simulate stalled backend
        var fresh = Claim(arbiter, Phone);
        Settle(clock, arbiter);
        Assert.Equal("suppressed", (await stale).Decision);
        Assert.Equal("granted", (await fresh).Decision);
    }

    [Fact]
    public async Task BoundsEvidenceAndRefusesPressureWithoutEvictingLiveReceipts()
    {
        var clock = new Clock();
        var arbiter = new VoiceActivationArbiter(clock);
        await Assert.ThrowsAsync<NodeException>(() => Claim(arbiter, Phone, Guid.Empty));
        await Assert.ThrowsAsync<NodeException>(() => Claim(arbiter, Phone, snr: double.NaN));
        await Assert.ThrowsAsync<NodeException>(() => Claim(arbiter, Phone, confidence: 1.01));
        var real = Claim(arbiter, Phone);
        NodeException? busy = null;
        for (var i = 0; i < 10_000; i++)
        {
            try { arbiter.Abandon(Desktop, Guid.NewGuid()); }
            catch (NodeException ex) { busy = ex; break; }
        }
        Assert.Equal(503, busy?.Status);
        clock.Advance(VoiceActivationArbiter.ElectionWindow);
        await Assert.ThrowsAsync<NodeException>(() => Claim(arbiter, Phone));
        Assert.Equal("granted", (await real).Decision);
        clock.Advance(TimeSpan.FromMinutes(3));
        var another = Claim(arbiter, Desktop);
        Settle(clock, arbiter);
        Assert.Equal("granted", (await another).Decision);
    }
}
