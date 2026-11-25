using Aegis.Application.Nodes;
using System.Text.Json.Serialization;

namespace Aegis.Api.Nodes;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VoiceActivationClaim(Guid ActivationId, double? SnrDb = null, double? Confidence = null);
public sealed record VoiceActivationDecision(Guid ActivationId, string Decision);

// One private owner and one API process. Ownership covers an acoustic activation,
// independently of the eventual chat/turn ID. No timer or participant inventory:
// the first confirmed, authenticated claim wins atomically.
public sealed class VoiceActivationArbiter(TimeProvider clock) : IHostedService
{
    public const int MaximumDetectionAgeMs = 2000;
    public static readonly TimeSpan CatchUpWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReceiptRetention = TimeSpan.FromMinutes(2);
    private const int ReceiptCapacity = 4096;

    private sealed record Receipt(long Received, bool Granted);
    private readonly object gate = new();
    private readonly Dictionary<(Guid Node, Guid Activation), Receipt> receipts = new();
    private long? suppressSince;
    private bool stopped;

    // A replacement process has no receipts from its predecessor. Quarantine
    // inbound claims for one client request lifetime before accepting a new winner.
    public Task StartAsync(CancellationToken ct)
    {
        lock (gate)
        {
            receipts.Clear();
            suppressSince = clock.GetTimestamp();
            stopped = false;
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        lock (gate) stopped = true;
        return Task.CompletedTask;
    }

    public VoiceActivationDecision Claim(Guid node, VoiceActivationClaim claim, CancellationToken ct,
        int? detectionAgeMs = null)
    {
        ct.ThrowIfCancellationRequested();
        // Legacy clients still supply quality evidence and/or the optional age.
        // Validate the unchanged wire contract, but never rank or wait on it.
        if (claim.ActivationId == Guid.Empty || claim.SnrDb is { } snr && (!double.IsFinite(snr) || snr is < -60 or > 100)
            || claim.Confidence is { } confidence && (!double.IsFinite(confidence) || confidence is < 0 or > 1)
            || detectionAgeMs is < 0 or > MaximumDetectionAgeMs)
            throw new NodeException("invalid_voice_activation", "Os dados da ativação de voz são inválidos.");

        lock (gate)
        {
            // A cancelled caller that was waiting for the lock must not take ownership.
            ct.ThrowIfCancellationRequested();
            var now = clock.GetTimestamp();
            Advance(now);
            var key = (node, claim.ActivationId);
            if (stopped) return new(claim.ActivationId, "suppressed");
            if (receipts.TryGetValue(key, out var cached))
            {
                // Never revive an earlier grant after its request lifetime, even
                // during a later winning activation (receipts outlive suppression).
                var validGrant = cached.Granted && clock.GetElapsedTime(cached.Received, now) < CatchUpWindow;
                return new(claim.ActivationId, validGrant ? "granted" : "suppressed");
            }

            var granted = !suppressSince.HasValue;
            Remember(key, new(now, granted));
            if (granted) suppressSince = now;
            // If the client disconnects after commitment, it remains the sole
            // winner. A lost response cannot transfer ownership to a late node.
            return new(claim.ActivationId, granted ? "granted" : "suppressed");
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
            var key = (node, activation);
            // Cancellation can overtake the POST. Preserve a rejected receipt in
            // that case. Cancellation after commitment never revokes a grant or
            // shortens catch-up suppression; otherwise two clients could emit cues.
            if (!receipts.ContainsKey(key)) Remember(key, new(now, false));
        }
    }

    private void Remember((Guid Node, Guid Activation) key, Receipt receipt)
    {
        if (receipts.Count >= ReceiptCapacity)
            throw new NodeException("voice_activation_busy", "O coordenador de voz está ocupado.", 503);
        receipts.Add(key, receipt);
    }

    private void Advance(long now)
    {
        if (suppressSince is { } start && clock.GetElapsedTime(start, now) >= CatchUpWindow)
            suppressSince = null;
        foreach (var entry in receipts)
            if (clock.GetElapsedTime(entry.Value.Received, now) >= ReceiptRetention)
                receipts.Remove(entry.Key);
    }
}
