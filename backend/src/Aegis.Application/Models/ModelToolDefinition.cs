using System.Text.Json;

namespace Aegis.Application.Models;

public sealed record ModelToolDefinition(
    string Name,
    string Description,
    JsonElement ParametersSchema);
