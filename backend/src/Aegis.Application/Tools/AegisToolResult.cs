// Copyright (C) 2026 Pedro Hasson Castello.
// GNU AGPL v3 only; attribution terms: /ADDITIONAL_TERMS.md.
namespace Aegis.Application.Tools;

public sealed record AegisToolResult(
    bool Success,
    string Content,
    string? ErrorCode = null,
    string? AuditMetadataJson = null);
