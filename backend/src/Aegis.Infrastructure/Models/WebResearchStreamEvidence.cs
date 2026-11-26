using System.Text.Json;
using Aegis.Application.Models;

namespace Aegis.Infrastructure.Models;

/// <summary>Projects observed source URLs only, deduping streaming events and the final response.</summary>
internal sealed class WebResearchStreamEvidence
{
    // Eight URLs of up to 1,024 UTF-16 units fit a legacy native 64 KiB NDJSON
    // line even with six-byte JSON escapes for every unit.
    private const int MaxUrlLength = 1024;
    private const int BatchSize = 8;
    private readonly Dictionary<string, HashSet<string>> seen = new(StringComparer.Ordinal);

    public IEnumerable<WebResearchSourceCount[]> Observe(JsonElement root, string? eventType)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        switch (eventType)
        {
            case "response.output_text.annotation.added":
                if (root.TryGetProperty("annotation", out var annotation) &&
                    WebResearchMetadata.ReadProgressCitationUrl(annotation) is { } url)
                    ObserveUrl(ItemId(root, "item_id"), url, counts);
                break;
            case "response.output_item.done":
                if (root.TryGetProperty("item", out var item))
                    ObserveItem(item, counts);
                break;
            case "response.completed":
                if (root.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object &&
                    response.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
                    foreach (var outputItem in output.EnumerateArray())
                        ObserveItem(outputItem, counts);
                break;
        }
        return counts.Select(pair => new WebResearchSourceCount(pair.Key, pair.Value)).Chunk(BatchSize);
    }

    private void ObserveItem(JsonElement item, Dictionary<string, int> counts)
    {
        var id = ItemId(item, "id");
        if (id is null) return;
        foreach (var url in WebResearchMetadata.ReadProgressUrls(item))
            ObserveUrl(id, url, counts);
    }

    private void ObserveUrl(string? id, string url, Dictionary<string, int> counts)
    {
        if (id is null || url.Length > MaxUrlLength) return;
        if (!seen.TryGetValue(id, out var urls))
            seen.Add(id, urls = new HashSet<string>(StringComparer.Ordinal));
        if (urls.Add(url))
            counts[url] = counts.GetValueOrDefault(url) + 1;
    }

    private static string? ItemId(JsonElement item, string key) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 200 } id
            ? id
            : null;
}
