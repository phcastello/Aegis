// Copyright (C) 2026 Pedro Hasson Castello.
// GNU AGPL v3 only; attribution terms: /ADDITIONAL_TERMS.md.
using System.Text.Json;

namespace Aegis.Application.Models;

public sealed record ModelRequest(
    string Instructions,
    string Input,
    ModelPurpose Purpose = ModelPurpose.Chat,
    IReadOnlyDictionary<string, string>? Metadata = null,
    IReadOnlyList<JsonElement>? InputItems = null,
    IReadOnlyList<JsonElement>? AuditInputItems = null);
