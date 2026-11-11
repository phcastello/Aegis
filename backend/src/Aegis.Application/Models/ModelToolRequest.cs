// Copyright (C) 2026 Pedro Hasson Castello.
// GNU AGPL v3 only; attribution terms: /ADDITIONAL_TERMS.md.
using System.Text.Json;

namespace Aegis.Application.Models;

public sealed record ModelToolRequest(
    ModelRequest Request,
    IReadOnlyList<ModelToolDefinition> Tools,
    int MaxIterations = 4,
    string? PreviousResponseId = null,
    IReadOnlyList<ModelToolOutput>? ToolOutputs = null,
    IReadOnlyList<JsonElement>? InputItems = null,
    IReadOnlyList<JsonElement>? AuditInputItems = null);
