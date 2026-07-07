namespace Aegis.Infrastructure.Titles;

public sealed class LocalTitleOptions
{
    public const string DefaultProvider = "local";
    public const string DefaultBaseUrl = "http://host.docker.internal:11434";
    public const string DefaultModel = "qwen2.5:3b";

    public string Provider { get; set; } = DefaultProvider;

    public string LocalBaseUrl { get; set; } = DefaultBaseUrl;

    public string LocalModel { get; set; } = DefaultModel;

    public int TimeoutSeconds { get; set; } = 30;

    public int MaxOutputTokens { get; set; } = 32;
}
