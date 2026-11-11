// Copyright (C) 2026 Pedro Hasson Castello.
// GNU AGPL v3 only; attribution terms: /ADDITIONAL_TERMS.md.
using System.Text.Json;

namespace Aegis.Application.Models;

public sealed record ModelToolCall(
    string Id,
    string Name,
    JsonElement Arguments);
