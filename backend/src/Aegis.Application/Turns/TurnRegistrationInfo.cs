// Copyright (C) 2026 Pedro Hasson Castello.
// GNU AGPL v3 only; attribution terms: /ADDITIONAL_TERMS.md.
namespace Aegis.Application.Turns;

/// <summary>Registration result plus the upstream speech request superseded by this turn.</summary>
public sealed record TurnRegistrationInfo(ActiveTurn Turn, string? SupersededNativeSpeechRequestId);
