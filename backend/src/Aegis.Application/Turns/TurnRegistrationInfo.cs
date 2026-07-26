namespace Aegis.Application.Turns;

/// <summary>Registration result plus the upstream speech request superseded by this turn.</summary>
public sealed record TurnRegistrationInfo(ActiveTurn Turn, string? SupersededNativeSpeechRequestId);
