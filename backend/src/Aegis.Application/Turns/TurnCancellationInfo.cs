namespace Aegis.Application.Turns;

public sealed record TurnCancellationInfo(Guid TurnId, string? NativeSpeechRequestId);
