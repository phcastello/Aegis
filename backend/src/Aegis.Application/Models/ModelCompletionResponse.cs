using Aegis.Application.Llm;

namespace Aegis.Application.Models;

public sealed record ModelCompletionResponse(
    string Content,
    string Provider,
    string Model,
    ModelPurpose Purpose,
    string? MetadataJson,
    LlmRequestAuditData AuditData);
