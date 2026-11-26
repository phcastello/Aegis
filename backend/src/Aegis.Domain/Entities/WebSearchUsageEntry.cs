namespace Aegis.Domain.Entities;

/// <summary>One observed native web action. No conversation data is necessary to count usage.</summary>
public sealed class WebSearchUsageEntry
{
    public string CallId { get; init; } = string.Empty;
    public string? ResponseId { get; init; }
    public string ActionType { get; init; } = "unknown";
    public string Status { get; init; } = "unknown";
    public DateTimeOffset FirstObservedAt { get; init; }
    public DateTimeOffset LastObservedAt { get; init; }
}
