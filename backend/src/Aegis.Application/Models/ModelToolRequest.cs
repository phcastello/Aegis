using System.Text.Json;

namespace Aegis.Application.Models;

public sealed record ModelToolRequest(
    ModelRequest Request,
    IReadOnlyList<ModelToolDefinition> Tools,
    int MaxIterations = 4,
    string? PreviousResponseId = null,
    IReadOnlyList<ModelToolOutput>? ToolOutputs = null,
    IReadOnlyList<JsonElement>? InputItems = null);
