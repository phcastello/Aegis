namespace Aegis.Application.Voice;

public interface IAegisSpeechClient
{
    Task<SpeechStreamResponse> StreamSpeechAsync(SpeechRequest request, CancellationToken cancellationToken = default);
    Task CancelAsync(string nativeSpeechRequestId, CancellationToken cancellationToken = default);
    Task<SpeechServiceStatus> GetStatusAsync(CancellationToken cancellationToken = default);
}
