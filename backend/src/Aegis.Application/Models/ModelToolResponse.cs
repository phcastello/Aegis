using Aegis.Application.Llm;

namespace Aegis.Application.Models;

public sealed record ModelToolResponse(
    string Content,
    string Provider,
    string Model,
    ModelPurpose Purpose,
    IReadOnlyList<ModelToolCall> ToolCalls,
    string? MetadataJson,
    LlmRequestAuditData AuditData);
