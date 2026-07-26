namespace Aegis.Application.Voice;

/// <summary>Short-lived readiness cache so UI polling never probes TTS per chat chunk.</summary>
public sealed class VoiceStatusCache(IAegisSpeechClient speechClient)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private SpeechServiceStatus? cached;
    private DateTimeOffset validUntil;

    public async Task<SpeechServiceStatus> GetAsync(CancellationToken cancellationToken = default)
    {
        if (cached is not null && DateTimeOffset.UtcNow < validUntil) return cached;
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (cached is not null && DateTimeOffset.UtcNow < validUntil) return cached;
            cached = await speechClient.GetStatusAsync(cancellationToken);
            validUntil = DateTimeOffset.UtcNow.AddSeconds(10);
            return cached;
        }
        finally
        {
            gate.Release();
        }
    }
}
