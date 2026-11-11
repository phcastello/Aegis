// Copyright (C) 2026 Pedro Hasson Castello.
// GNU AGPL v3 only; attribution terms: /ADDITIONAL_TERMS.md.
using Aegis.Application.Llm;

namespace Aegis.Application.Models;

public sealed record ModelCompletionResponse(
    string Content,
    string Provider,
    string Model,
    ModelPurpose Purpose,
    string? MetadataJson,
    LlmRequestAuditData AuditData);
