using System.Text.RegularExpressions;
using Aegis.Domain.Entities;

namespace Aegis.Application.Memory;

public static class MemoryEntityMentions
{
    public static async Task<IReadOnlyList<MemoryEntity>> FindAsync(IMemoryStore store, string text, int limit, CancellationToken ct)
    {
        var haystack = MemoryText.Normalize(text, Math.Max(200, text.Length));
        var names = await store.ListEntityNamesAsync(ct);
        var matches = names.Select(x => (x.Entity, Normalized: MemoryText.Normalize(x.Name, 200)))
            .Where(x => x.Normalized.Length >= 4 && Regex.IsMatch(haystack,
                $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(x.Normalized)}(?![\p{{L}}\p{{N}}])", RegexOptions.CultureInvariant))
            .OrderByDescending(x => x.Normalized.Length).ThenBy(x => x.Normalized, StringComparer.Ordinal).ToArray();
        var ambiguousNames = matches.GroupBy(x => x.Normalized).Where(x => x.Select(y => y.Entity.Id).Distinct().Count() > 1)
            .Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        return matches.Where(x => !ambiguousNames.Contains(x.Normalized)).Select(x => x.Entity)
            .DistinctBy(x => x.Id).Take(limit).ToArray();
    }
}
