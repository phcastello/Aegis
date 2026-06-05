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
