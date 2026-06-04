namespace Aegis.Application.Prompts;

public sealed record PromptBuildResult(
    string Prompt,
    string? RuntimeContext);
