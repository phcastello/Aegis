// Copyright (C) 2026 Pedro Hasson Castello.
// GNU AGPL v3 only; attribution terms: /ADDITIONAL_TERMS.md.
namespace Aegis.Application.Turns;

/// <summary>Atomic cancellation outcome plus the native request that must be stopped upstream.</summary>
public sealed record TurnCancellationInfo(
    CancelTurnResult Result,
    string? NativeSpeechRequestId);
