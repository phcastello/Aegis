using Aegis.Application.Nodes;
using System.Text.Json.Serialization;

namespace Aegis.Api.Nodes;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VoiceActivationClaim(Guid ActivationId, double? SnrDb = null, double? Confidence = null);
public sealed record VoiceActivationDecision(Guid ActivationId, string Decision);

// One private owner and one API process, matching the existing node connection registry.
// Each round is for an acoustic activation, independent of the eventual chat/turn ID.
public sealed class VoiceActivationArbiter(TimeProvider clock) : IHostedService
{
    // Recorded shared calls showed up to ~440ms of detector skew before
    // inference and transport overhead on the mobile node.
    public static readonly TimeSpan ElectionWindow = TimeSpan.FromMilliseconds(650);
    public static readonly TimeSpan CatchUpWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReceiptRetention = TimeSpan.FromMinutes(2);
    private const int ReceiptCapacity = 4096;

    private sealed class Candidate(Guid nodeId, VoiceActivationClaim claim, int priority, long received)
    {
        public Guid NodeId => nodeId;
        public VoiceActivationClaim Claim => claim;
        public int Priority => priority;
        public long Received => received;
        public int Waiters;
        public TaskCompletionSource<bool> Decision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Round(long opened)
    {
        public long Opened => opened;
        public Dictionary<Guid, Candidate> Candidates { get; } = new();
    }

    private readonly object gate = new();
    private readonly Dictionary<(Guid Node, Guid Activation), Candidate> receipts = new();
    private Round? current;
    private long? suppressSince;

    // A replacement process has no receipts from its predecessor. Quarantine
    // inbound claims for one client request lifetime before admitting a new round.
    public Task StartAsync(CancellationToken ct)
    {
        lock (gate) suppressSince = clock.GetTimestamp();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        lock (gate)
        {
            if (current is { } round)
                foreach (var candidate in round.Candidates.Values)
                    candidate.Decision.TrySetResult(false);
            current = null;
        }
        return Task.CompletedTask;
    }

    public async Task<VoiceActivationDecision> ClaimAsync(Guid node, VoiceActivationClaim claim, int priority, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (claim.ActivationId == Guid.Empty || claim.SnrDb is { } snr && (!double.IsFinite(snr) || snr is < -60 or > 100)
            || claim.Confidence is { } confidence && (!double.IsFinite(confidence) || confidence is < 0 or > 1))
            throw new NodeException("invalid_voice_activation", "Os dados da ativação de voz são inválidos.");

        Candidate candidate;
        lock (gate)
        {
            var now = clock.GetTimestamp();
            Advance(now);
            var key = (node, claim.ActivationId);
            if (receipts.TryGetValue(key, out var cached))
            {
                if (clock.GetElapsedTime(cached.Received, now) >= CatchUpWindow)
                    return new(claim.ActivationId, "suppressed");
                candidate = cached;
            }
            else
            {
                candidate = Remember(node, claim, priority, now);
                if (suppressSince.HasValue || current?.Candidates.ContainsKey(node) == true)
                    candidate.Decision.SetResult(false);
                else
                {
                    if (current is null)
                    {
                        current = new Round(now);
                        _ = CloseAfterWindowAsync(current);
                    }
                    current.Candidates.Add(node, candidate);
                }
            }
            candidate.Waiters++;
        }
        try
        {
            var granted = await candidate.Decision.Task.WaitAsync(ct);
            // Thread-pool stalls or a queued continuation must not revive an
            // old grant. Clients apply the same deadline around the entire HTTP call.
            lock (gate)
                return new(claim.ActivationId, granted && clock.GetElapsedTime(candidate.Received) < CatchUpWindow
                    ? "granted" : "suppressed");
        }
        finally
        {
            lock (gate)
            {
                candidate.Waiters--;
                if (candidate.Waiters == 0 && !candidate.Decision.Task.IsCompleted)
                    WithdrawPending(candidate);
            }
        }
    }

    public void Abandon(Guid node, Guid activation)
    {
        if (activation == Guid.Empty)
            throw new NodeException("invalid_voice_activation", "O identificador da ativação é inválido.");
        lock (gate)
        {
            var now = clock.GetTimestamp();
            Advance(now);
            if (!receipts.TryGetValue((node, activation), out var candidate))
                // Cancellation can overtake the original request.
                candidate = Remember(node, new(activation), 0, now);
            WithdrawPending(candidate);
        }
    }

    private Candidate Remember(Guid node, VoiceActivationClaim claim, int priority, long now)
    {
        if (receipts.Count >= ReceiptCapacity)
            throw new NodeException("voice_activation_busy", "O coordenador de voz está ocupado.", 503);
        var candidate = new Candidate(node, claim, priority, now);
        receipts.Add((node, claim.ActivationId), candidate);
        return candidate;
    }

    private void WithdrawPending(Candidate candidate)
    {
        if (!candidate.Decision.TrySetResult(false)) return;
        if (current?.Candidates.GetValueOrDefault(candidate.NodeId) == candidate)
            current.Candidates.Remove(candidate.NodeId);
    }

    private void Advance(long now)
    {
        if (current is { } round && clock.GetElapsedTime(round.Opened, now) >= ElectionWindow)
            Close(round, now);
        if (suppressSince is { } start && clock.GetElapsedTime(start, now) >= CatchUpWindow)
            suppressSince = null;
        foreach (var entry in receipts)
            if (clock.GetElapsedTime(entry.Value.Received, now) >= ReceiptRetention)
                receipts.Remove(entry.Key);
    }

    private async Task CloseAfterWindowAsync(Round round)
    {
        await Task.Delay(ElectionWindow, clock, CancellationToken.None);
        lock (gate)
            if (ReferenceEquals(round, current)) Close(round, clock.GetTimestamp());
    }

    private void Close(Round round, long now)
    {
        current = null;
        // A suspended or overloaded server can miss the entire lifetime of a
        // round. In that case release all waiters silently instead of granting late.
        var eligible = round.Candidates.Values.Where(c => clock.GetElapsedTime(c.Received, now) < CatchUpWindow).ToArray();
        if (eligible.Length > 0)
        {
            // Compare like-for-like evidence only. 3 dB of SNR and 0.05 of
            // detector confidence are tie bands, not distance estimates.
            if (eligible.All(c => c.Claim.SnrDb.HasValue))
            {
                var best = eligible.Max(c => c.Claim.SnrDb!.Value);
                eligible = eligible.Where(c => c.Claim.SnrDb >= best - 3).ToArray();
            }
            if (eligible.All(c => c.Claim.Confidence.HasValue))
            {
                var best = eligible.Max(c => c.Claim.Confidence!.Value);
                eligible = eligible.Where(c => c.Claim.Confidence >= best - 0.05).ToArray();
            }
            var winner = eligible.OrderByDescending(c => c.Priority).ThenBy(c => c.NodeId).First();
            winner.Decision.SetResult(true);
            suppressSince = now;
        }
        foreach (var candidate in round.Candidates.Values)
            candidate.Decision.TrySetResult(false);
    }
}
