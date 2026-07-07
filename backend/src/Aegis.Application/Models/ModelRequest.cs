namespace Aegis.Application.Models;

public sealed record ModelRequest(
    string Instructions,
    string Input,
    ModelPurpose Purpose = ModelPurpose.Default,
    IReadOnlyDictionary<string, string>? Metadata = null);
