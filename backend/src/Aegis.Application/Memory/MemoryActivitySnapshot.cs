using Aegis.Domain.Entities;

namespace Aegis.Application.Memory;

public sealed record MemoryActivitySection(string Kind, IReadOnlyList<string> Items, int TotalCount);
public sealed record MemoryActivitySnapshot(bool Pending, IReadOnlyList<MemoryActivitySection> Sections);
public sealed record MemoryAutomaticContextResult(string? Text, IReadOnlyList<Guid> MemoryIds,
    IReadOnlyList<Guid> RelationIds)
{
    public static readonly MemoryAutomaticContextResult Empty = new(null, [], []);
}
public sealed record MemoryObservedContextResult(string? Text, IReadOnlyList<Guid> MemoryIds)
{
    public static readonly MemoryObservedContextResult Empty = new(null, []);
}

public interface IMemoryActivityStore
{
    Task RecordAsync(MemoryActivityEvent activity, CancellationToken ct);
    Task SaveResponseWithUsedAsync(IReadOnlyList<MemoryActivityEvent> used, CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, MemoryActivitySnapshot>> LoadForAssistantMessagesAsync(
        IReadOnlyList<Guid> assistantMessageIds, CancellationToken ct);
}
