using System.Text.Json;

namespace Aegis.Application.Prompts;

public sealed record PromptBuildResult(
    string Prompt,
    string? RuntimeContext,
    IReadOnlyList<JsonElement> InputItems,
    string? AuditRuntimeContext = null,
    IReadOnlyList<JsonElement>? AuditInputItems = null);
