using Aegis.Api.Nodes;
using Aegis.Application.Nodes;
using System.Text.Json;
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
        Assert.NotNull(first.EpisodeId);
        Assert.NotEqual(Guid.Empty, first.EpisodeId.Value);
        Assert.NotEqual(first.ActivationId, first.EpisodeId.Value);
        var later = Claim(arbiter, Desktop, snr: 40, confidence: 1, detectionAgeMs: 0);
        Assert.Equal("suppressed", later.Decision);
        Assert.Equal(first.EpisodeId, later.EpisodeId);
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
        Assert.NotNull(winner.decision.EpisodeId);
        Assert.Equal(winner.decision, Claim(arbiter, winner.node, winner.decision.ActivationId));
        foreach (var loser in decisions.Where(item => item.node != winner.node))
        {
            Assert.Equal("suppressed", loser.decision.Decision);
            Assert.Equal(winner.decision.EpisodeId, loser.decision.EpisodeId);
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
        Assert.Equal(first.EpisodeId, loser.EpisodeId);
        Assert.Equal(loser, Claim(arbiter, Desktop, sameId));
        Assert.Equal("suppressed", Claim(arbiter, Phone).Decision);
        clock.Advance(VoiceActivationArbiter.CatchUpWindow);
        var expired = Claim(arbiter, Phone, sameId);
        Assert.Equal("suppressed", expired.Decision);
        Assert.Equal(first.EpisodeId, expired.EpisodeId);
        Assert.Equal("suppressed", Claim(arbiter, Desktop, sameId).Decision);
        var next = Claim(arbiter, Desktop);
        Assert.Equal("granted", next.Decision);
        Assert.NotNull(next.EpisodeId);
        Assert.NotEqual(first.EpisodeId, next.EpisodeId);
        Assert.Equal(expired, Claim(arbiter, Phone, sameId));
        Assert.Equal(loser, Claim(arbiter, Desktop, sameId));
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
        var unwitnessed = Claim(arbiter, Desktop, id);
        Assert.Equal("suppressed", unwitnessed.Decision);
        Assert.Null(unwitnessed.EpisodeId);
        // Another node's same local ID is independent.
        var winner = Claim(arbiter, Phone, id);
        Assert.Equal("granted", winner.Decision);
        arbiter.Abandon(Phone, id);
        Assert.Equal(winner, Claim(arbiter, Phone, id));
        Assert.Equal(unwitnessed, Claim(arbiter, Desktop, id));
        var caughtUp = Claim(arbiter, Desktop);
        Assert.Equal("suppressed", caughtUp.Decision);
        Assert.Equal(winner.EpisodeId, caughtUp.EpisodeId);
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
        Assert.Null(stale.EpisodeId);
        clock.Advance(VoiceActivationArbiter.CatchUpWindow);
        Assert.Equal(stale, Claim(arbiter, Desktop, stale.ActivationId));
        var winner = Claim(arbiter, Phone);
        Assert.Equal("granted", winner.Decision);
        await arbiter.StopAsync(default);
        var stopped = Claim(arbiter, Phone, winner.ActivationId);
        Assert.Equal("suppressed", stopped.Decision);
        Assert.Equal(winner.EpisodeId, stopped.EpisodeId);
        clock.Advance(VoiceActivationArbiter.CatchUpWindow);
        var freshDuringShutdown = Claim(arbiter, Desktop);
        Assert.Equal("suppressed", freshDuringShutdown.Decision);
        Assert.Null(freshDuringShutdown.EpisodeId);
        await arbiter.StartAsync(default);
        var quarantined = Claim(arbiter, Phone, winner.ActivationId);
        Assert.Equal("suppressed", quarantined.Decision);
        Assert.Null(quarantined.EpisodeId);
        clock.Advance(VoiceActivationArbiter.CatchUpWindow);
        var restarted = Claim(arbiter, Desktop);
        Assert.Equal("granted", restarted.Decision);
        Assert.NotEqual(winner.EpisodeId, restarted.EpisodeId);
        Assert.Equal(quarantined, Claim(arbiter, Phone, winner.ActivationId));
    }

    private sealed record LegacyDecision(Guid ActivationId, string Decision);

    [Fact]
    public void EpisodeIdIsAnAdditiveNullableWebJsonField()
    {
        var arbiter = new VoiceActivationArbiter(new Clock());
        var decision = Claim(arbiter, Phone);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var granted = JsonSerializer.SerializeToElement(decision, json);
        Assert.Equal(decision.EpisodeId!.Value, granted.GetProperty("episodeId").GetGuid());
        Assert.Equal(new LegacyDecision(decision.ActivationId, decision.Decision), granted.Deserialize<LegacyDecision>(json));
        var id = Guid.NewGuid();
        arbiter.Abandon(Desktop, id);
        var unlinked = JsonSerializer.SerializeToElement(Claim(arbiter, Desktop, id), json);
        Assert.Equal(JsonValueKind.Null, unlinked.GetProperty("episodeId").ValueKind);
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
