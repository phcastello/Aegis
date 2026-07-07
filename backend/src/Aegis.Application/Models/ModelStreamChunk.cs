using Aegis.Application.Llm;

namespace Aegis.Application.Models;

public sealed record ModelStreamChunk(
    string? Content,
    bool IsDone,
    string? Provider = null,
    string? Model = null,
    ModelPurpose? Purpose = null,
    string? MetadataJson = null,
    LlmRequestAuditData? AuditData = null);
