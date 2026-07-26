namespace Aegis.Application.Turns;

public interface IActiveTurnRegistry
{
    ActiveTurn Register(Guid turnId, Guid conversationId);
    TurnRegistrationInfo RegisterAndGetSuperseded(Guid turnId, Guid conversationId);
    ActiveTurn? Find(Guid turnId);
    ActiveTurn? FindBySpeechRequest(Guid speechRequestId);
    bool IsCurrent(Guid conversationId, Guid turnId);
    bool IsActive(Guid turnId);
    bool TryTransition(Guid turnId, TurnStatus expected, TurnStatus next);
    bool TrySetTextCompleted(Guid turnId, Guid assistantMessageId);
    bool TryBeginSpeech(Guid turnId, Guid speechRequestId, string nativeSpeechRequestId);
    bool TrySetStreamingAudio(Guid turnId, Guid speechRequestId);
    bool TryCompleteWithoutSpeech(Guid turnId);
    void Fail(Guid turnId);
    void Complete(Guid turnId);
    TurnCancellationInfo CancelAndGetInfo(Guid turnId, string reason, CancellationToken cancellationToken = default);
    IReadOnlyList<TurnCancellationInfo> BeginShutdownAndCancelAll(string reason);
    Task<CancelTurnResult> CancelAsync(Guid turnId, string reason, CancellationToken cancellationToken = default);
    Task CancelAllAsync(string reason, CancellationToken cancellationToken = default);
}
