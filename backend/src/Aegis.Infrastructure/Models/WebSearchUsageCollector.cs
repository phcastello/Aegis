using System.Text.Json;

namespace Aegis.Infrastructure.Models;

/// <summary>Provider observation only; no query, URL, prompt, or response content is retained.</summary>
public sealed record WebSearchUsageObservation(
    string CallId,
    string? ResponseId,
    string ActionType,
    string Status,
    DateTimeOffset ObservedAt,
    DateTimeOffset? FirstObservedAt = null);

public interface IWebSearchUsageRecorder
{
    Task<bool> TryRecordAsync(WebSearchUsageObservation observation);
}

/// <summary>Deduplicates native search events within one provider response. The durable store
/// also deduplicates replays across collector instances.</summary>
internal sealed class WebSearchUsageCollector(IWebSearchUsageRecorder? recorder)
{
    private readonly Dictionary<string, WebSearchUsageObservation> seen = new(StringComparer.Ordinal);
    private readonly HashSet<string> pending = new(StringComparer.Ordinal);
    private string? responseId;

    public async Task ObserveResponseAsync(JsonElement response)
    {
        if (recorder is null || response.ValueKind != JsonValueKind.Object) return;
        responseId ??= Identifier(response, "id");
        if (response.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
            foreach (var item in output.EnumerateArray())
                await ObserveItemAsync(item);
    }

    public async Task ObserveFailureBodyAsync(string body)
    {
        if (recorder is null) return;
        try
        {
            using var document = JsonDocument.Parse(body);
            await ObserveResponseAsync(document.RootElement);
        }
        catch (JsonException)
        {
            // Error bodies need not contain JSON or a model response.
        }
    }

    public async Task ObserveEventAsync(JsonElement frame, string? eventType)
    {
        if (recorder is null || frame.ValueKind != JsonValueKind.Object) return;
        responseId ??= Identifier(frame, "response_id");
        if (frame.TryGetProperty("response", out var response))
            await ObserveResponseAsync(response);

        if (eventType is "response.output_item.added" or "response.output_item.done")
        {
            if (frame.TryGetProperty("item", out var item))
                await ObserveItemAsync(item);
        }
        else if (eventType is not null && eventType.StartsWith("response.web_search_call.", StringComparison.Ordinal))
        {
            var state = Status(eventType["response.web_search_call.".Length..]);
            await ObserveAsync(Identifier(frame, "item_id"), "unknown", state);
        }
    }

    private Task ObserveItemAsync(JsonElement item)
    {
        if (StringValue(item, "type") != "web_search_call") return Task.CompletedTask;
        var actionType = item.TryGetProperty("action", out var action) ? StringValue(action, "type") : null;
        return ObserveAsync(Identifier(item, "id"), Action(actionType), Status(StringValue(item, "status")));
    }

    private async Task ObserveAsync(string? itemId, string action, string status)
    {
        if (recorder is null || itemId is null) return;
        var previous = seen.GetValueOrDefault(itemId);
        if (previous is not null)
        {
            if (action == "unknown" || previous.ActionType is "search" or "open_page" or "find_in_page")
                action = previous.ActionType;
            if (StatusRank(status) <= StatusRank(previous.Status)) status = previous.Status;
            if (previous.ActionType == action && previous.Status == status && previous.ResponseId == responseId &&
                !pending.Contains(itemId)) return;
        }

        var now = DateTimeOffset.UtcNow;
        var observation = new WebSearchUsageObservation(itemId, responseId, action, status, now,
            previous?.FirstObservedAt ?? previous?.ObservedAt ?? now);
        seen[itemId] = observation;
        // Do not tie persistence to cancellation of the chat/stream: the provider
        // already reported this call. A duplicate event can retry a failed write.
        if (await recorder.TryRecordAsync(observation)) pending.Remove(itemId);
        else pending.Add(itemId);
    }

    private static int StatusRank(string status) => status switch
    {
        "completed" or "failed" or "incomplete" => 3,
        "searching" => 2,
        "in_progress" => 1,
        _ => 0
    };

    private static string Action(string? value) => value switch
    {
        "search" or "open_page" or "find_in_page" => value,
        null or "" => "unknown",
        _ => "other"
    };

    private static string Status(string? value) => value switch
    {
        "in_progress" or "searching" or "completed" or "failed" or "incomplete" => value,
        _ => "unknown"
    };

    private static string? Identifier(JsonElement item, string property)
    {
        var value = StringValue(item, property);
        return value is { Length: > 0 and <= 200 } &&
            !value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)) ? value : null;
    }

    private static string? StringValue(JsonElement item, string property) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
