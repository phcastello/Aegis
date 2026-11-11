// Copyright (C) 2026 Pedro Hasson Castello.
// GNU AGPL v3 only; attribution terms: /ADDITIONAL_TERMS.md.
namespace Aegis.Application.Turns;

public enum TurnStatus
{
    Created,
    GeneratingText,
    TextCompleted,
    RequestingSpeech,
    StreamingAudio,
    Completed,
    Cancelling,
    Cancelled,
    Failed
}
