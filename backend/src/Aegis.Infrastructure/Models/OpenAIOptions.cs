namespace Aegis.Infrastructure.Models;

public sealed class OpenAIOptions
{
    public const string SectionName = "Aegis";
    public const string DefaultBaseUrl = "https://api.openai.com";
    public const string DefaultModelProvider = "openai";
    public const string DefaultDefaultModel = "gpt-5.4-nano";
    public const string DefaultMainModel = "gpt-5.4-mini";
    public const string DefaultEscalationModel = "gpt-5.4";

    public string? ApiKey { get; set; }

    public string ModelProvider { get; set; } = DefaultModelProvider;

    public string BaseUrl { get; set; } = DefaultBaseUrl;

    public string DefaultModel { get; set; } = DefaultDefaultModel;

    public string MainModel { get; set; } = DefaultMainModel;

    public string EscalationModel { get; set; } = DefaultEscalationModel;

    public bool UseEscalationAutomatically { get; set; }

    public bool StoreResponses { get; set; }

    public string ServiceTier { get; set; } = "auto";

    public int MaxOutputTokens { get; set; } = 4000;

    public bool WebSearchEnabled { get; set; }

    public bool WebSearchRequireExplicitRequest { get; set; } = true;
}
