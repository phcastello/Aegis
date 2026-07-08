using Aegis.Application.Llm;
using System.Text.Json;

namespace Aegis.Application.Models;

public sealed record ModelToolStreamChunk(
    string? Content,
    bool IsDone,
    IReadOnlyList<ModelToolCall> ToolCalls,
    IReadOnlyList<JsonElement> OutputItems,
    string? ResponseId = null,
    string? Provider = null,
    string? Model = null,
    ModelPurpose? Purpose = null,
    string? MetadataJson = null,
    LlmRequestAuditData? AuditData = null);
