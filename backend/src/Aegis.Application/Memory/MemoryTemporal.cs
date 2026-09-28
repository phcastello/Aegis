using Aegis.Domain.Entities;

namespace Aegis.Application.Memory;

internal static class MemoryTemporal
{
    public static (MemoryRecord? Existing, MemoryRecord Candidate) Resolve(
        IReadOnlyList<MemoryRecord> sameContent, MemoryRecord candidate, DateTimeOffset now, bool implicitCurrent)
    {
        var exact = sameContent.FirstOrDefault(x => x.ValidFrom == candidate.ValidFrom && x.ValidUntil == candidate.ValidUntil);
        if (exact is not null) return (exact, candidate);
        if (implicitCurrent)
        {
            var current = sameContent.Where(x => IsValid(x.ValidFrom, x.ValidUntil, now)).ToArray();
            if (current.Length > 1) throw new MemoryException("memory_validity_conflict", "Há memórias temporais sobrepostas para esse conteúdo.");
            if (current.Length == 1) return (current[0], candidate);
            if (sameContent.Count > 0)
                candidate = new MemoryRecord(candidate.Content, now, null, now);
        }
        if (sameContent.Any(x => Overlaps(x.ValidFrom, x.ValidUntil, candidate.ValidFrom, candidate.ValidUntil)))
            throw new MemoryException("memory_validity_conflict", "Já existe uma memória Active com intervalo sobreposto.");
        return (null, candidate);
    }

    public static bool IsValid(DateTimeOffset? from, DateTimeOffset? until, DateTimeOffset at) =>
        (from is null || from <= at) && (until is null || until > at);

    public static bool Overlaps(DateTimeOffset? firstFrom, DateTimeOffset? firstUntil,
        DateTimeOffset? secondFrom, DateTimeOffset? secondUntil) =>
        (firstUntil is null || secondFrom is null || firstUntil > secondFrom) &&
        (secondUntil is null || firstFrom is null || secondUntil > firstFrom);
}
