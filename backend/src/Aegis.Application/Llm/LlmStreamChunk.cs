namespace Aegis.Application.Llm;

public sealed record LlmStreamChunk(
    string? Content,
    bool IsDone,
    string? Model = null,
    string? MetadataJson = null,
    LlmRequestAuditData? AuditData = null);
