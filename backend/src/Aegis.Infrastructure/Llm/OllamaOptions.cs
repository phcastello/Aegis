namespace Aegis.Infrastructure.Llm;

public sealed class OllamaOptions
{
    public const string SectionName = "Ollama";
    public const string DefaultBaseUrl = "http://host.docker.internal:11434";
    public const string DefaultModel = "qwen2.5:14b";

    public string BaseUrl { get; set; } = DefaultBaseUrl;

    public string Model { get; set; } = DefaultModel;

    public double? Temperature { get; set; } = 0.6;

    public int? NumCtx { get; set; } = 4096;
}
