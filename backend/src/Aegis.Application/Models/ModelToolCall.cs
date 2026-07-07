using System.Text.Json;

namespace Aegis.Application.Models;

public sealed record ModelToolCall(
    string Id,
    string Name,
    JsonElement Arguments);
