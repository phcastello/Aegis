namespace Aegis.Application.Llm;

public sealed class LlmRequestException(
    string message,
    LlmRequestAuditData auditData,
    Exception? innerException = null) : Exception(message, innerException)
{
    public LlmRequestAuditData AuditData { get; } = auditData;
}
