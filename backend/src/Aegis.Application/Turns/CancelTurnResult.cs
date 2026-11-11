// Copyright (C) 2026 Pedro Hasson Castello.
// GNU AGPL v3 only; attribution terms: /ADDITIONAL_TERMS.md.
namespace Aegis.Application.Turns;

public sealed record CancelTurnResult(
    Guid TurnId,
    string Status,
    bool LlmCancellationRequested,
    bool SpeechCancellationRequested);
