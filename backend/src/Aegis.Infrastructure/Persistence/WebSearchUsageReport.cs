using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;

namespace Aegis.Infrastructure.Persistence;

public sealed record WebSearchUsageReport(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtcExclusive,
    long Search,
    [property: JsonPropertyName("open_page")] long OpenPage,
    [property: JsonPropertyName("find_in_page")] long FindInPage,
    long Unknown,
    long Other,
    long Total,
    IReadOnlyDictionary<string, long> Statuses)
{
    public static async Task<WebSearchUsageReport> ReadAsync(AegisDbContext db,
        DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        from = from.ToUniversalTime();
        to = to.ToUniversalTime();
        if (from >= to) throw new ArgumentException("The UTC report interval must be nonempty.");
        var rows = await db.WebSearchUsage.AsNoTracking()
            .Where(item => item.FirstObservedAt >= from && item.FirstObservedAt < to)
            .GroupBy(item => new { item.ActionType, item.Status })
            .Select(group => new { group.Key.ActionType, group.Key.Status, Count = group.LongCount() })
            .ToListAsync(cancellationToken);
        long Count(string action) => rows.Where(row => row.ActionType == action).Sum(row => row.Count);
        var search = Count("search");
        var open = Count("open_page");
        var find = Count("find_in_page");
        var unknown = Count("unknown");
        var total = rows.Sum(row => row.Count);
        // Future or unrecognized action types remain visible in the aggregate.
        var other = total - search - open - find - unknown;
        var statusCounts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var status in new[] { "in_progress", "searching", "completed", "failed", "incomplete", "unknown" })
            statusCounts[status] = rows.Where(row => row.Status == status).Sum(row => row.Count);
        statusCounts["unknown"] += total - statusCounts.Values.Sum();
        return new WebSearchUsageReport(from, to, search, open, find, unknown, other, total, statusCounts);
    }
}
