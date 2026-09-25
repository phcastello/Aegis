using System.Text.Json;

namespace Aegis.Application.Models;

public sealed record ModelRequest(
    string Instructions,
    string Input,
    ModelPurpose Purpose = ModelPurpose.Chat,
    IReadOnlyDictionary<string, string>? Metadata = null,
    IReadOnlyList<JsonElement>? InputItems = null);
