using Aegis.Application.Turns;

namespace Aegis.Application.Voice;

public interface IVoiceService
{
    Task<ActiveTurn> RegisterTurnAsync(Guid turnId, Guid conversationId, CancellationToken cancellationToken = default);
    Task<VoiceStream> StartSpeechAsync(StartSpeechRequest request, CancellationToken cancellationToken = default);
    Task<CancelTurnResult> CancelSpeechAsync(Guid speechRequestId, CancellationToken cancellationToken = default);
    Task<CancelTurnResult> CancelTurnAsync(Guid turnId, string reason, CancellationToken cancellationToken = default);
    Task CancelAllTurnsAsync(string reason, CancellationToken cancellationToken = default);
    bool TryCompleteTurnWithoutSpeech(Guid turnId);
    void CompleteSpeech(Guid speechRequestId);
    void FailSpeech(Guid speechRequestId);
    Task<SpeechServiceStatus> GetStatusAsync(CancellationToken cancellationToken = default);
}
