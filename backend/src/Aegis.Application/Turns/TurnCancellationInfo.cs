namespace Aegis.Application.Turns;

/// <summary>Atomic cancellation outcome plus the native request that must be stopped upstream.</summary>
public sealed record TurnCancellationInfo(
    CancelTurnResult Result,
    string? NativeSpeechRequestId);
