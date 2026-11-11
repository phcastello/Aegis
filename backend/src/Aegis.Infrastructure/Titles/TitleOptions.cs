// Copyright (C) 2026 Pedro Hasson Castello.
// GNU AGPL v3 only; attribution terms: /ADDITIONAL_TERMS.md.
namespace Aegis.Infrastructure.Titles;

public sealed class TitleOptions
{
    public const string SectionName = "AegisTitle";
    public const string DefaultModel = "gpt-5-nano";

    public string Model { get; set; } = DefaultModel;
    public string ReasoningEffort { get; set; } = "minimal";
    public int TimeoutSeconds { get; set; } = 30;
    public int MaxOutputTokens { get; set; } = 64;
}
