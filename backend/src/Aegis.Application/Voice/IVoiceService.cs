using Aegis.Application.Turns;

namespace Aegis.Application.Voice;

public interface IVoiceService
{
    Task<VoiceStream> StartSpeechAsync(StartSpeechRequest request, CancellationToken cancellationToken = default);
    Task<CancelTurnResult> CancelSpeechAsync(Guid speechRequestId, CancellationToken cancellationToken = default);
    void CompleteSpeech(Guid speechRequestId);
    Task<SpeechServiceStatus> GetStatusAsync(CancellationToken cancellationToken = default);
}
