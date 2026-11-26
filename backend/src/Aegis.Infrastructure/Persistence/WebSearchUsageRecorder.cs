using Aegis.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aegis.Infrastructure.Persistence;

/// <summary>Persists provider observations independently of conversation persistence/cancellation.</summary>
public sealed class WebSearchUsageRecorder(IServiceScopeFactory scopes, ILogger<WebSearchUsageRecorder> logger)
    : IWebSearchUsageRecorder
{
    public async Task<bool> TryRecordAsync(WebSearchUsageObservation observation)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AegisDbContext>();
            var recorded = await UpsertAsync(db, observation, timeout.Token);
            if (!recorded)
                logger.LogWarning("Web search usage call identifier conflicts with an existing response.");
            return recorded;
        }
        catch (Exception exception)
        {
            // Availability of the usage counter must not suppress an assistant reply.
            // Do not log provider response contents or connection configuration.
            logger.LogWarning("Web search usage write failed: {ErrorType}", exception.GetType().Name);
            return false;
        }
    }

    /// <summary>Atomic, parameterized upsert. An output item may appear in multiple SSE
    /// events and subsequently in the final response. A replay adds no second row.</summary>
    public static async Task<bool> UpsertAsync(AegisDbContext db, WebSearchUsageObservation observation,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(observation.CallId) || observation.CallId.Length > 200 ||
            observation.ResponseId is { Length: > 200 } ||
            observation.ActionType is not ("search" or "open_page" or "find_in_page" or "unknown" or "other") ||
            observation.Status is not ("in_progress" or "searching" or "completed" or "failed" or "incomplete" or "unknown"))
            throw new ArgumentException("Invalid web search usage observation.");
        var observedAt = observation.ObservedAt.ToUniversalTime();
        var firstObservedAt = observation.FirstObservedAt?.ToUniversalTime() ?? observedAt;
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO web_search_usage ("CallId", "ResponseId", "ActionType", "Status", "FirstObservedAt", "LastObservedAt")
            VALUES ({observation.CallId}, {observation.ResponseId}, {observation.ActionType}, {observation.Status}, {firstObservedAt}, {observedAt})
            ON CONFLICT ("CallId") DO UPDATE SET
                "ResponseId" = COALESCE(web_search_usage."ResponseId", EXCLUDED."ResponseId"),
                "ActionType" = CASE
                    WHEN EXCLUDED."ActionType" = 'unknown' THEN web_search_usage."ActionType"
                    WHEN web_search_usage."ActionType" IN ('unknown', 'other') THEN EXCLUDED."ActionType"
                    ELSE web_search_usage."ActionType" END,
                "Status" = CASE
                    WHEN web_search_usage."Status" IN ('completed', 'failed', 'incomplete') THEN web_search_usage."Status"
                    WHEN EXCLUDED."Status" IN ('completed', 'failed', 'incomplete') THEN EXCLUDED."Status"
                    WHEN web_search_usage."Status" = 'searching' OR EXCLUDED."Status" = 'unknown' THEN web_search_usage."Status"
                    ELSE EXCLUDED."Status" END,
                "FirstObservedAt" = LEAST(web_search_usage."FirstObservedAt", EXCLUDED."FirstObservedAt"),
                "LastObservedAt" = GREATEST(web_search_usage."LastObservedAt", EXCLUDED."LastObservedAt")
            WHERE web_search_usage."ResponseId" IS NULL OR EXCLUDED."ResponseId" IS NULL
                OR web_search_usage."ResponseId" = EXCLUDED."ResponseId"
            """, cancellationToken);
        return affected > 0;
    }
}
