namespace Aegis.Application.Models;

public sealed record ModelToolRequest(
    ModelRequest Request,
    IReadOnlyList<ModelToolDefinition> Tools,
    int MaxIterations = 4);
