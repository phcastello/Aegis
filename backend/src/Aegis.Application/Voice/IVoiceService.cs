using Aegis.Application.Turns;

namespace Aegis.Application.Voice;

public interface IVoiceService
{
    Task<ActiveTurn> RegisterTurnAsync(Guid turnId, Guid conversationId, CancellationToken cancellationToken = default);
    Task<VoiceStream> StartSegmentSpeechAsync(StartSegmentSpeechRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    Task<VoiceStream> StartSpeechAsync(StartSpeechRequest request, CancellationToken cancellationToken = default);
    Task<CancelTurnResult> CancelSpeechAsync(Guid speechRequestId, CancellationToken cancellationToken = default);
    Task<CancelTurnResult> CancelTurnAsync(Guid turnId, string reason, CancellationToken cancellationToken = default);
    Task<VoiceGenerationReceipt?> CommitProvisionalVoiceInterruptionAsync(Guid candidateTurnId, Guid predecessorTurnId,
        Guid sessionId, Guid conversationId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    Task<VoiceGenerationReceipt?> CancelVoiceGenerationAsync(Guid turnId, Guid sessionId, Guid? destination,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task CancelAllTurnsAsync(string reason, CancellationToken cancellationToken = default);
    bool TryCompleteTurnWithoutSpeech(Guid turnId);
    void CompleteSpeech(Guid speechRequestId);
    void FailSpeech(Guid speechRequestId);
    Task<SpeechServiceStatus> GetStatusAsync(CancellationToken cancellationToken = default);
}
