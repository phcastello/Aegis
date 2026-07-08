using Aegis.Application.Llm;
using System.Text.Json;

namespace Aegis.Application.Models;

public sealed record ModelToolResponse(
    string Content,
    string Provider,
    string Model,
    ModelPurpose Purpose,
    IReadOnlyList<ModelToolCall> ToolCalls,
    IReadOnlyList<JsonElement> OutputItems,
    string? ResponseId,
    string? MetadataJson,
    LlmRequestAuditData AuditData);
