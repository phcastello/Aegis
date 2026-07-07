namespace Aegis.Application.Tools;

public sealed record AegisToolResult(
    bool Success,
    string Content,
    string? ErrorCode = null,
    string? AuditMetadataJson = null);
