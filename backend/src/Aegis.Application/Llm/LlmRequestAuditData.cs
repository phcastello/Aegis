// Copyright (C) 2026 Pedro Hasson Castello.
// GNU AGPL v3 only; attribution terms: /ADDITIONAL_TERMS.md.
namespace Aegis.Application.Llm;

public sealed record LlmRequestAuditData(
    string Provider,
    string Model,
    bool Success,
    long DurationMilliseconds,
    string RequestPayloadJson,
    int? HttpStatusCode,
    string? ResponseBody,
    string? FailureReason,
    string? ErrorType);
