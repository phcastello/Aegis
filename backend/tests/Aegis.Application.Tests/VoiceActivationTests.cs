using Aegis.Api.Nodes;
using Aegis.Application.Nodes;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class VoiceActivationTests
{
    private sealed class Clock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public void Advance(TimeSpan amount) => timestamp += amount.Ticks;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => throw new InvalidOperationException("First-confirmed ownership must not schedule a timer.");
    }

    private static readonly Guid Phone = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Desktop = Guid.Parse("00000000-0000-0000-0000-000000000002");

    private static VoiceActivationDecision Claim(VoiceActivationArbiter arbiter, Guid node, Guid? activation = null,
        double? snr = null, double? confidence = null, CancellationToken ct = default, int? detectionAgeMs = null) =>
        arbiter.Claim(node, new(activation ?? Guid.NewGuid(), snr, confidence), ct, detectionAgeMs);

    [Fact]
    public void FirstValidClaimWinsWithoutWaitingEvenForMuchStrongerLaterSignal()
    {
        var clock = new Clock();
        var arbiter = new VoiceActivationArbiter(clock);
        // No clock advancement or timer is required to grant the first candidate.
        var first = Claim(arbiter, Phone, snr: 5, confidence: .4, detectionAgeMs: 300);
        Assert.Equal("granted", first.Decision);
        var later = Claim(arbiter, Desktop, snr: 40, confidence: 1, detectionAgeMs: 0);
        Assert.Equal("suppressed", later.Decision);
        Assert.Equal(0, clock.GetTimestamp());
    }

    [Fact]
    public async Task ConcurrentClaimsFromManyNodesReturnExactlyOneWinner()
    {
        var arbiter = new VoiceActivationArbiter(new Clock());
        var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nodes = Enumerable.Range(0, 32).Select(_ => Guid.NewGuid()).ToArray();
        var requests = nodes.Select(async (node, i) => {
            await launch.Task;
            return (node, decision: Claim(arbiter, node, snr: i));
        }).ToArray();
        launch.SetResult();
        var decisions = await Task.WhenAll(requests);
        var winner = Assert.Single(decisions, item => item.decision.Decision == "granted");
        Assert.Equal(winner.decision, Claim(arbiter, winner.node, winner.decision.ActivationId));
        foreach (var loser in decisions.Where(item => item.node != winner.node))
        {
            Assert.Equal("suppressed", loser.decision.Decision);
            Assert.Equal(loser.decision, Claim(arbiter, loser.node, loser.decision.ActivationId));
        }
    }

    [Fact]
    public void ReceiptsAreScopedToNodeAndActivationAndNeverRegrantExpiredClaims()
    {
        var clock = new Clock();
        var arbiter = new VoiceActivationArbiter(clock);
        var sameId = Guid.NewGuid();
        var first = Claim(arbiter, Phone, sameId, snr: 1);
        Assert.Equal("granted", first.Decision);
        Assert.Equal(first, Claim(arbiter, Phone, sameId, snr: 40));
        var loser = Claim(arbiter, Desktop, sameId);
        Assert.Equal("suppressed", loser.Decision);
        Assert.Equal(loser, Claim(arbiter, Desktop, sameId));
        Assert.Equal("suppressed", Claim(arbiter, Phone).Decision);
        clock.Advance(VoiceActivationArbiter.CatchUpWindow);
        Assert.Equal("suppressed", Claim(arbiter, Phone, sameId).Decision);
        Assert.Equal("suppressed", Claim(arbiter, Desktop, sameId).Decision);
        var next = Claim(arbiter, Desktop);
        Assert.Equal("granted", next.Decision);
        Assert.Equal("suppressed", Claim(arbiter, Phone, sameId).Decision);
        Assert.Equal(next, Claim(arbiter, Desktop, next.ActivationId));
    }

    [Fact]
    public void LateClaimsDoNotExtendCatchUpWindow()
    {
        var clock = new Clock();
        var arbiter = new VoiceActivationArbiter(clock);
        Assert.Equal("granted", Claim(arbiter, Phone).Decision);
        clock.Advance(TimeSpan.FromMilliseconds(1999));
        var late = Claim(arbiter, Desktop);
        Assert.Equal("suppressed", late.Decision);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(late, Claim(arbiter, Desktop, late.ActivationId));
        Assert.Equal("granted", Claim(arbiter, Desktop).Decision);
    }

    [Fact]
    public void CancellationBeforePostCreatesTombstoneButCancellingWinnerNeverTransfersOwnership()
    {
        var clock = new Clock();
        var arbiter = new VoiceActivationArbiter(clock);
        var id = Guid.NewGuid();
        arbiter.Abandon(Desktop, id);
        Assert.Equal("suppressed", Claim(arbiter, Desktop, id).Decision);
        // Another node's same local ID is independent.
        var winner = Claim(arbiter, Phone, id);
        Assert.Equal("granted", winner.Decision);
        arbiter.Abandon(Phone, id);
        Assert.Equal(winner, Claim(arbiter, Phone, id));
        Assert.Equal("suppressed", Claim(arbiter, Desktop).Decision);
        clock.Advance(VoiceActivationArbiter.CatchUpWindow);
        Assert.Equal("suppressed", Claim(arbiter, Phone, id).Decision);
        Assert.Equal("suppressed", Claim(arbiter, Desktop, id).Decision);
        Assert.Equal("granted", Claim(arbiter, Desktop).Decision);
    }

    [Fact]
    public void AlreadyCancelledRequestCannotConsumeOwnershipOrReceipt()
    {
        var arbiter = new VoiceActivationArbiter(new Clock());
        var id = Guid.NewGuid();
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => Claim(arbiter, Phone, id, ct: stop.Token));
        Assert.Equal("granted", Claim(arbiter, Phone, id).Decision);
        Assert.Equal("suppressed", Claim(arbiter, Desktop).Decision);
    }

    [Fact]
    public async Task StartupAndShutdownFailClosedAndQuarantineNewProcess()
    {
        var clock = new Clock();
        var arbiter = new VoiceActivationArbiter(clock);
        await arbiter.StartAsync(default);
        var stale = Claim(arbiter, Desktop);
        Assert.Equal("suppressed", stale.Decision);
        clock.Advance(VoiceActivationArbiter.CatchUpWindow);
        Assert.Equal(stale, Claim(arbiter, Desktop, stale.ActivationId));
        var winner = Claim(arbiter, Phone);
        Assert.Equal("granted", winner.Decision);
        await arbiter.StopAsync(default);
        Assert.Equal("suppressed", Claim(arbiter, Phone, winner.ActivationId).Decision);
        clock.Advance(VoiceActivationArbiter.CatchUpWindow);
        Assert.Equal("suppressed", Claim(arbiter, Desktop).Decision);
        await arbiter.StartAsync(default);
        Assert.Equal("suppressed", Claim(arbiter, Desktop).Decision);
        clock.Advance(VoiceActivationArbiter.CatchUpWindow);
        Assert.Equal("granted", Claim(arbiter, Desktop).Decision);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(299)]
    [InlineData(300)]
    [InlineData(2000)]
    public void ValidLegacyDetectionAgeIsAcceptedWithoutDelay(int? age)
    {
        var clock = new Clock();
        var arbiter = new VoiceActivationArbiter(clock);
        Assert.Equal("granted", Claim(arbiter, Phone, detectionAgeMs: age).Decision);
        Assert.Equal(0, clock.GetTimestamp());
    }

    [Fact]
    public void InvalidEvidenceCannotReserveOwnershipOrReceipt()
    {
        var arbiter = new VoiceActivationArbiter(new Clock());
        var id = Guid.NewGuid();
        Assert.Throws<NodeException>(() => Claim(arbiter, Phone, Guid.Empty));
        Assert.Throws<NodeException>(() => Claim(arbiter, Phone, id, snr: double.NaN));
        Assert.Throws<NodeException>(() => Claim(arbiter, Phone, id, snr: 101));
        Assert.Throws<NodeException>(() => Claim(arbiter, Phone, id, confidence: 1.01));
        Assert.Throws<NodeException>(() => Claim(arbiter, Phone, id, detectionAgeMs: -1));
        Assert.Throws<NodeException>(() => Claim(arbiter, Phone, id, detectionAgeMs: 2001));
        Assert.Throws<NodeException>(() => arbiter.Abandon(Phone, Guid.Empty));
        Assert.Equal("granted", Claim(arbiter, Phone, id).Decision);
    }

    [Fact]
    public void ReceiptCapacityFailsClosedWithoutEvictingLiveDecisionsAndRecoversAfterRetention()
    {
        var clock = new Clock();
        var arbiter = new VoiceActivationArbiter(clock);
        var winner = Claim(arbiter, Phone);
        Assert.Equal("granted", winner.Decision);
        for (var i = 0; i < 4095; i++) arbiter.Abandon(Desktop, Guid.NewGuid());
        var busy = Assert.Throws<NodeException>(() => Claim(arbiter, Desktop));
        Assert.Equal(503, busy.Status);
        Assert.Equal(winner, Claim(arbiter, Phone, winner.ActivationId));
        clock.Advance(VoiceActivationArbiter.CatchUpWindow);
        Assert.Equal("suppressed", Claim(arbiter, Phone, winner.ActivationId).Decision);
        Assert.Equal(503, Assert.Throws<NodeException>(() => Claim(arbiter, Desktop)).Status);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal("granted", Claim(arbiter, Desktop).Decision);
    }
}