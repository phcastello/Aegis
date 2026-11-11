// Copyright (C) 2026 Pedro Hasson Castello.
// GNU AGPL v3 only; attribution terms: /ADDITIONAL_TERMS.md.
using Aegis.Application.Llm;

namespace Aegis.Application.Models;

public sealed record ModelStreamChunk(
    string? Content,
    bool IsDone,
    string? Provider = null,
    string? Model = null,
    ModelPurpose? Purpose = null,
    string? MetadataJson = null,
    LlmRequestAuditData? AuditData = null,
    ToolStatus? ToolStatus = null);
