using Aegis.Domain.Entities;

namespace Aegis.Application.Memory;

public sealed class MemoryAutomaticOptions
{
    public bool Enabled { get; set; } = true;
    public string Model { get; set; } = "gpt-5.6-luna";
    public string ReasoningEffort { get; set; } = "low";
    public string BaseUrl { get; set; } = "https://api.openai.com";
    public string ApiKey { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 30;
    public int MaxOutputTokens { get; set; } = 2000;
    public int PollSeconds { get; set; } = 5;
}

public sealed record MemoryRecentMessage(string Role, string Content);
public sealed record MemoryExtractionSource(Guid ConversationId, Guid UserMessageId, string Target,
    DateTimeOffset ObservedAt, IReadOnlyList<MemoryRecentMessage> Recent,
    Guid? JobId = null, Guid? LeaseId = null);
public sealed record MemoryExtractionMemory(string Ref, MemoryRecord Record);
public sealed record MemoryExtractionRelation(string Ref, MemoryRelation Relation, string SubjectName, string ObjectName);
public sealed record MemoryExtractionInput(string Target, IReadOnlyList<MemoryRecentMessage> Recent,
    IReadOnlyList<MemoryExtractionMemory> ExistingMemories,
    IReadOnlyList<MemoryExtractionRelation> ExistingRelations,
    IReadOnlyList<string> ExistingPredicates);
public sealed record MemoryExtractionOutput(IReadOnlyList<MemoryExtractionCandidate> Candidates);
public sealed record MemoryExtractionCandidate(string Content, string Action, string? ExistingMemoryRef,
    DateTimeOffset? TransitionAt, DateTimeOffset? ValidFrom, DateTimeOffset? ValidUntil,
    IReadOnlyList<MemoryExtractionEntity> Entities, IReadOnlyList<MemoryExtractionRelationAction> Relations);
public sealed record MemoryExtractionEntity(string Key, string Mention, string CanonicalName, string? EntityType,
    IReadOnlyList<string> Aliases);
public sealed record MemoryExtractionRelationAction(string Action, string? ExistingRelationRef,
    string? SubjectKey, string? Predicate, string? ObjectKey,
    DateTimeOffset? ValidFrom, DateTimeOffset? ValidUntil, DateTimeOffset? CloseAt);
public sealed record MemoryExtractionSummary(int Candidates, int Created, int Reinforced, int Corrected,
    int Transitioned, int GraphMutations, int Skipped);

public interface IMemoryExtractionClient
{
    Task<MemoryExtractionOutput> ExtractAsync(MemoryExtractionInput input, CancellationToken ct);
}

public interface IMemoryExtractionJobStore
{
    Task<MemoryExtractionJob?> ClaimAsync(DateTimeOffset now, TimeSpan lease, CancellationToken ct);
    Task<MemoryExtractionSource?> ReadSourceAsync(MemoryExtractionJob job, CancellationToken ct);
    Task<bool> CompleteAsync(MemoryExtractionJob job, MemoryExtractionSummary summary, DateTimeOffset now, CancellationToken ct);
    Task<bool> FailAsync(MemoryExtractionJob job, string code, DateTimeOffset now, TimeSpan? delay, CancellationToken ct);
}

public sealed class MemoryExtractionException(string code, bool retryable = true) : Exception(code)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}
