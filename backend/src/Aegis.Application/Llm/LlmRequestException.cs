// Copyright (C) 2026 Pedro Hasson Castello.
// GNU AGPL v3 only; attribution terms: /ADDITIONAL_TERMS.md.
namespace Aegis.Application.Llm;

public sealed class LlmRequestException(
    string message,
    LlmRequestAuditData auditData,
    Exception? innerException = null) : Exception(message, innerException)
{
    public LlmRequestAuditData AuditData { get; } = auditData;
}
