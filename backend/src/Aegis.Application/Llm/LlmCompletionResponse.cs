namespace Aegis.Application.Llm;

public sealed record LlmCompletionResponse(
    string Content,
    string Model,
    string? MetadataJson);
