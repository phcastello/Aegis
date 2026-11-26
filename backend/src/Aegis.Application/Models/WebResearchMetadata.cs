using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aegis.Application.Models;

public sealed record WebResearchSource(string Url, string? Title, string Kind, bool Cited = false);
public sealed record WebResearchCitation(string Url, string? Title, int StartIndex, int EndIndex);
public sealed record WebResearchSourceCount(string Url, int Count);
public sealed record WebResearchEvidence(
    IReadOnlyList<WebResearchSource> Sources,
    IReadOnlyList<WebResearchCitation> Citations);

/// <summary>Projects provider-reported web evidence into the existing message metadata.</summary>
public static class WebResearchMetadata
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string PropertyName = "webResearch";

    public static string? Merge(string? finalMetadataJson, IEnumerable<JsonElement> outputItems,
        string? finalContent, bool hasTextAnnotations)
    {
        var sources = new Dictionary<string, WebResearchSource>(StringComparer.Ordinal);
        var allText = new List<TextPart>();
        List<TextPart> lastMessageText = [];

        foreach (var item in outputItems)
        {
            switch (StringProperty(item, "type"))
            {
                case "web_search_call":
                    if (StringProperty(item, "status") == "completed" &&
                        item.TryGetProperty("action", out var action))
                        AddActionSources(action, sources);
                    break;
                case "message":
                    lastMessageText = [];
                    if (!TryArray(item, "content", out var content)) break;
                    foreach (var part in content.EnumerateArray())
                    {
                        if (StringProperty(part, "type") != "output_text" ||
                            StringProperty(part, "text") is not { } partText) continue;

                        var annotations = ReadAnnotations(part, sources);
                        var phase = StringProperty(item, "phase");
                        if (phase is not null and not "final_answer") continue;
                        var textPart = new TextPart(partText, annotations);
                        allText.Add(textPart);
                        lastMessageText.Add(textPart);
                    }
                    break;
            }
        }

        // Never attach a citation position from hidden commentary, a prior round,
        // or function arguments to a different piece of user-facing text.
        var citations = new List<WebResearchCitation>();
        if (hasTextAnnotations && finalContent is not null)
        {
            var displayed = finalContent.Trim();
            if (!TryMapCitations(allText, displayed, citations))
                TryMapCitations(lastMessageText, displayed, citations);
        }

        if (sources.Count == 0 && citations.Count == 0) return finalMetadataJson;
        JsonObject metadata;
        try
        {
            metadata = string.IsNullOrWhiteSpace(finalMetadataJson)
                ? new JsonObject()
                : JsonNode.Parse(finalMetadataJson) as JsonObject ?? new JsonObject();
        }
        catch (JsonException) { metadata = new JsonObject(); }

        metadata[PropertyName] = JsonSerializer.SerializeToNode(
            new WebResearchEvidence(sources.Values.ToArray(), citations.ToArray()), JsonOptions);
        return metadata.ToJsonString(JsonOptions);
    }

    /// <summary>Returns only the public web evidence, tolerating old or partial metadata.</summary>
    public static WebResearchEvidence? Read(string? metadataJson, string? displayedContent = null)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty(PropertyName, out var evidence))
                return ReadEvidence(evidence, displayedContent);

            // SpeechSegmentMetadata keeps the model metadata as a JSON string.
            if (StringProperty(root, "modelMetadata") is not { } nested) return null;
            using var modelDocument = JsonDocument.Parse(nested);
            var modelRoot = modelDocument.RootElement;
            return modelRoot.ValueKind == JsonValueKind.Object &&
                modelRoot.TryGetProperty(PropertyName, out evidence)
                ? ReadEvidence(evidence, displayedContent)
                : null;
        }
        catch (JsonException) { return null; }
    }

    public static IReadOnlyCollection<string> ReadProgressUrls(JsonElement item)
    {
        var sources = new Dictionary<string, WebResearchSource>(StringComparer.Ordinal);
        if (StringProperty(item, "type") == "web_search_call" &&
            StringProperty(item, "status") == "completed" && item.TryGetProperty("action", out var action))
        {
            AddActionSources(action, sources);
        }
        else if (StringProperty(item, "type") == "message" && TryArray(item, "content", out var content))
        {
            foreach (var part in content.EnumerateArray())
                if (StringProperty(part, "type") == "output_text")
                    ReadAnnotations(part, sources);
        }
        return sources.Keys;
    }

    public static string? ReadProgressCitationUrl(JsonElement annotation) =>
        StringProperty(annotation, "type") == "url_citation"
            ? NormalizeSourceUrl(StringProperty(annotation, "url"))
            : null;

    private static void AddActionSources(JsonElement action, Dictionary<string, WebResearchSource> sources)
    {
        var type = StringProperty(action, "type");
        if (type is "open_page" or "find_in_page")
            AddSource(sources, StringProperty(action, "url"), null,
                type == "open_page" ? "opened" : "consulted");

        if (type == "search" && TryArray(action, "sources", out var retrieved))
        {
            foreach (var source in retrieved.EnumerateArray())
            {
                if (StringProperty(source, "type") == "url")
                    AddSource(sources, StringProperty(source, "url"), StringProperty(source, "title"), "consulted");
            }
        }
    }

    private static List<WebResearchCitation> ReadAnnotations(JsonElement part,
        Dictionary<string, WebResearchSource> sources)
    {
        var citations = new List<WebResearchCitation>();
        if (!TryArray(part, "annotations", out var annotations)) return citations;
        foreach (var annotation in annotations.EnumerateArray())
        {
            if (StringProperty(annotation, "type") != "url_citation") continue;
            var title = StringProperty(annotation, "title");
            var url = AddSource(sources, StringProperty(annotation, "url"), title, "citation");
            if (url is null || !TryInt(annotation, "start_index", out var start) ||
                !TryInt(annotation, "end_index", out var end) || start < 0 || end <= start) continue;
            citations.Add(new WebResearchCitation(url, title, start, end));
        }
        return citations;
    }

    private static bool TryMapCitations(IReadOnlyList<TextPart> parts, string displayed,
        List<WebResearchCitation> citations)
    {
        // The message entity trims outer whitespace. Any other transformation is
        // deliberately rejected because the positions would no longer be reliable.
        var original = string.Concat(parts.Select(part => part.Text));
        if (!string.Equals(original.Trim(), displayed, StringComparison.Ordinal)) return false;
        var leading = original.Length - original.TrimStart().Length;
        var position = -leading;
        var seen = new HashSet<(string Url, int Start, int End)>();
        foreach (var part in parts)
        {
            foreach (var citation in part.Citations)
            {
                if (citation.EndIndex > part.Text.Length) continue;
                // The provider documents character indices without a UTF-16 guarantee.
                // When astral characters make the two conventions ambiguous, keep the
                // verified source but omit the inline position.
                if (part.Text.Take(citation.EndIndex).Any(char.IsSurrogate)) continue;
                var start = position + citation.StartIndex;
                var end = position + citation.EndIndex;
                if (start < 0 || end > displayed.Length || !seen.Add((citation.Url, start, end))) continue;
                citations.Add(citation with { StartIndex = start, EndIndex = end });
            }
            position += part.Text.Length;
        }
        return true;
    }

    private static WebResearchEvidence? ReadEvidence(JsonElement evidence, string? displayed)
    {
        var sources = new Dictionary<string, WebResearchSource>(StringComparer.Ordinal);
        if (TryArray(evidence, "sources", out var sourceArray))
        {
            foreach (var source in sourceArray.EnumerateArray())
            {
                var kind = StringProperty(source, "kind");
                if (kind is "consulted" or "opened" or "citation")
                    AddSource(sources, StringProperty(source, "url"), StringProperty(source, "title"), kind,
                        source.TryGetProperty("cited", out var cited) && cited.ValueKind == JsonValueKind.True);
            }
        }
        var citations = new List<WebResearchCitation>();
        var seen = new HashSet<(string Url, int Start, int End)>();
        if (TryArray(evidence, "citations", out var citationArray))
        {
            foreach (var item in citationArray.EnumerateArray())
            {
                var title = StringProperty(item, "title");
                var url = AddSource(sources, StringProperty(item, "url"), title, "citation");
                if (url is null || !TryInt(item, "startIndex", out var start) ||
                    !TryInt(item, "endIndex", out var end) || start < 0 || end <= start ||
                    (displayed is not null && (end > displayed.Length ||
                        (start > 0 && char.IsLowSurrogate(displayed[start])) ||
                        (end < displayed.Length && char.IsLowSurrogate(displayed[end])))) ||
                    !seen.Add((url, start, end))) continue;
                citations.Add(new WebResearchCitation(url, title, start, end));
            }
        }
        return sources.Count == 0 ? null : new WebResearchEvidence(sources.Values.ToArray(), citations.ToArray());
    }

    private static string? AddSource(Dictionary<string, WebResearchSource> sources,
        string? value, string? title, string kind, bool cited = false)
    {
        var url = NormalizeSourceUrl(value);
        if (url is null) return null;
        title = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        cited = cited || kind == "citation";
        if (sources.TryGetValue(url, out var existing))
        {
            kind = Rank(kind) > Rank(existing.Kind) ? kind : existing.Kind;
            title = existing.Title ?? title;
            cited = cited || existing.Cited;
        }
        sources[url] = new WebResearchSource(url, title, kind, cited);
        return url;
    }

    private static string? NormalizeSourceUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '\\') ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !value.StartsWith(uri.Scheme + "://", StringComparison.OrdinalIgnoreCase)) return null;
        return uri.AbsoluteUri;
    }

    private static int Rank(string kind) => kind switch { "opened" => 3, "consulted" => 2, _ => 1 };

    private static string? StringProperty(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static bool TryArray(JsonElement item, string name, out JsonElement array)
    {
        array = default;
        return item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out array) &&
            array.ValueKind == JsonValueKind.Array;
    }

    private static bool TryInt(JsonElement item, string name, out int value)
    {
        value = 0;
        return item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value);
    }

    private sealed record TextPart(string Text, IReadOnlyList<WebResearchCitation> Citations);
}
