using System.Text.Json;

namespace Aegis.Application.Prompts;

public sealed record PromptBuildResult(
    string Prompt,
    string? RuntimeContext,
    IReadOnlyList<JsonElement> InputItems);
