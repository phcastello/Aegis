// Copyright (C) 2026 Pedro Hasson Castello.
// GNU AGPL v3 only; attribution terms: /ADDITIONAL_TERMS.md.
namespace Aegis.Infrastructure.Models;

public sealed class OpenAIOptions
{
    public const string SectionName = "Aegis";
    public const string DefaultBaseUrl = "https://api.openai.com";
    public const string DefaultChatModel = "gpt-6-luna";

    public string? ApiKey { get; set; }

    public string BaseUrl { get; set; } = DefaultBaseUrl;

    public string ChatModel { get; set; } = DefaultChatModel;

    public string ChatReasoningEffort { get; set; } = "medium";

    public bool StoreResponses { get; set; }

    public string ServiceTier { get; set; } = "auto";

    public int MaxOutputTokens { get; set; } = 4000;

}
