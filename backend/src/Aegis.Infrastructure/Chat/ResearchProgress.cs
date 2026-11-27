using Aegis.Application.Models;

namespace Aegis.Infrastructure.Chat;

internal sealed class ResearchProgress(bool isPython = false)
{
    private readonly Dictionary<string, int> counts = new(StringComparer.Ordinal);

    public string? Phase { get; private set; }
    public bool HasSuccessfulScript { get; private set; }
    public IReadOnlyList<WebResearchSourceCount> Sources =>
        counts.Select(pair => new WebResearchSourceCount(pair.Key, pair.Value)).ToArray();

    public bool Update(ModelStreamChunk chunk)
    {
        var prior = Phase;
        if (!string.IsNullOrEmpty(chunk.Content))
            Phase = "finishing";
        else if (isPython && chunk.ToolStatus is { Category: "python" } status)
        {
            if (status.State == "started") Phase = "executing";
            if (status.State == "completed") HasSuccessfulScript = true;
        }
        else if (Phase is null && chunk.ToolStatus?.Category == "web_search")
            Phase = "searching";

        var changed = prior != Phase;
        if (!isPython && chunk.WebResearchSources is not null)
        {
            foreach (var source in chunk.WebResearchSources)
                changed |= ObserveSource(source.Url, source.Count);
        }
        return changed;
    }

    private bool ObserveSource(string url, int count)
    {
        if (count <= 0 || url.Length > ResearchJobStore.MaximumSourceUrlLength ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
            return false;

        if (!counts.TryGetValue(url, out var previous) && counts.Count >= ResearchJobStore.MaximumSources)
            return false;
        var next = (int)Math.Min(int.MaxValue, (long)previous + count);
        if (next == previous) return false;
        counts[url] = next;
        return true;
    }
}
