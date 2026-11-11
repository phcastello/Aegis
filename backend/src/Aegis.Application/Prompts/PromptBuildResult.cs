// Copyright (C) 2026 Pedro Hasson Castello.
// GNU AGPL v3 only; attribution terms: /ADDITIONAL_TERMS.md.
using System.Text.Json;

namespace Aegis.Application.Prompts;

public sealed record PromptBuildResult(
    string Prompt,
    string? RuntimeContext,
    IReadOnlyList<JsonElement> InputItems,
    string? AuditRuntimeContext = null,
    IReadOnlyList<JsonElement>? AuditInputItems = null)
{
    public IReadOnlyList<Guid> UsedMemoryIds { get; init; } = [];
    public IReadOnlyList<Guid> UsedObservedMemoryIds { get; init; } = [];
    public IReadOnlyList<Guid> UsedRelationIds { get; init; } = [];
}
