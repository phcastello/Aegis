namespace Aegis.Application.Turns;

public sealed record CancelTurnResult(
    Guid TurnId,
    string Status,
    bool LlmCancellationRequested,
    bool SpeechCancellationRequested);
