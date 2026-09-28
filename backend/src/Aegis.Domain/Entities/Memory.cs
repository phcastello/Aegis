using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Aegis.Domain.Entities;

public enum MemoryStatus { Active, Superseded, Forgotten }
public enum MemorySourceKind { ExplicitMemoryRequest, UserStatement, ToolObservation, Inference }
public enum MemoryProjectionTarget { Semantic, Graph }
public enum MemoryAggregateType { MemoryRecord, MemoryEntity, MemoryRelation }
public enum MemoryProjectionOperation { Upsert, Delete }
public enum MemoryProjectionStatus { Pending, Processing, Completed, Failed }
public enum MemoryExtractionStatus { Pending, Processing, Completed, Failed }

public static class MemoryText
{
    public const int MaxContentLength = 2000;
    public static string Clean(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("O texto não pode estar vazio.");
        var clean = Regex.Replace(value.Trim().Normalize(NormalizationForm.FormKC), @"\s+", " ");
        if (clean.Length > maxLength) throw new ArgumentException($"O texto excede {maxLength} caracteres.");
        return clean;
    }
    public static string Normalize(string value, int maxLength) => Clean(value, maxLength).ToUpperInvariant();
    public static string NormalizePredicate(string value)
    {
        var clean = Clean(value, 80);
        var snake = Regex.Replace(clean, "([a-z0-9])([A-Z])", "$1_$2");
        snake = Regex.Replace(snake, @"[\s-]+", "_").ToUpperInvariant();
        if (snake.Length > 80 || !Regex.IsMatch(snake, "^[A-Z][A-Z0-9]*(?:_[A-Z0-9]+)*$"))
            throw new ArgumentException("Predicate deve usar UPPER_SNAKE_CASE.");
        return snake;
    }
    public static string Hash(string normalized) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
}

public sealed class MemoryRecord : AuditableEntity
{
    private MemoryRecord() { }
    public MemoryRecord(string content, DateTimeOffset? validFrom, DateTimeOffset? validUntil, DateTimeOffset now)
    {
        if (validFrom is not null && validUntil is not null && validUntil <= validFrom) throw new ArgumentException("Intervalo de validade inválido.");
        InitializeAudit(now);
        Content = MemoryText.Clean(content, MemoryText.MaxContentLength);
        ContentHash = MemoryText.Hash(MemoryText.Normalize(Content, MemoryText.MaxContentLength));
        ValidFrom = validFrom?.ToUniversalTime(); ValidUntil = validUntil?.ToUniversalTime();
        Status = MemoryStatus.Active; Revision = 1;
    }
    public string Content { get; private set; } = "";
    public string ContentHash { get; private set; } = "";
    public MemoryStatus Status { get; private set; }
    public DateTimeOffset? ValidFrom { get; private set; }
    public DateTimeOffset? ValidUntil { get; private set; }
    public DateTimeOffset? SupersededAt { get; private set; }
    public Guid? SupersededById { get; private set; }
    public DateTimeOffset? ForgottenAt { get; private set; }
    public int Revision { get; private set; }
    public void Supersede(Guid replacementId, DateTimeOffset now)
    {
        if (Status != MemoryStatus.Active || replacementId == Id) throw new InvalidOperationException("Memória não pode ser substituída.");
        Status = MemoryStatus.Superseded; SupersededAt = now; SupersededById = replacementId; Revision++; Touch(now);
    }
    public bool CloseValidity(DateTimeOffset at, DateTimeOffset now)
    {
        at = at.ToUniversalTime();
        if (Status != MemoryStatus.Active || ValidFrom is not null && at <= ValidFrom || ValidUntil is not null && ValidUntil != at)
            throw new InvalidOperationException("Memória não pode ser fechada nesse instante.");
        if (ValidUntil == at) return false;
        ValidUntil = at; Revision++; Touch(now); return true;
    }
    public bool Forget(DateTimeOffset now)
    {
        if (Status == MemoryStatus.Forgotten) return false;
        if (Status != MemoryStatus.Active) throw new InvalidOperationException("Memória não está vigente.");
        Status = MemoryStatus.Forgotten; ForgottenAt = now; Revision++; Touch(now); return true;
    }
}

public sealed class MemoryEvidence : AuditableEntity
{
    private MemoryEvidence() { }
    public MemoryEvidence(Guid memoryId, MemorySourceKind sourceKind, Guid? conversationId, Guid? messageId, DateTimeOffset observedAt, DateTimeOffset now)
    {
        InitializeAudit(now); MemoryId = memoryId; SourceKind = sourceKind;
        SourceConversationId = conversationId; SourceMessageId = messageId; ObservedAt = observedAt;
    }
    public Guid MemoryId { get; private set; }
    public MemorySourceKind SourceKind { get; private set; }
    public Guid? SourceConversationId { get; private set; }
    public Guid? SourceMessageId { get; private set; }
    public DateTimeOffset ObservedAt { get; private set; }
}

public sealed class MemoryEntity : AuditableEntity
{
    private MemoryEntity() { }
    public MemoryEntity(string name, string? entityType, DateTimeOffset now)
    {
        InitializeAudit(now); CanonicalName = MemoryText.Clean(name, 200);
        NormalizedName = MemoryText.Normalize(name, 200);
        EntityType = entityType is null ? null : MemoryText.Normalize(entityType, 40); Revision = 1;
    }
    public string CanonicalName { get; private set; } = "";
    public string NormalizedName { get; private set; } = "";
    public string? EntityType { get; private set; }
    public int Revision { get; private set; }
    public DateTimeOffset? RetiredAt { get; private set; }
    public void AliasChanged(DateTimeOffset now) { Revision++; Touch(now); }
    public void Retire(DateTimeOffset now) { if (RetiredAt is null) { RetiredAt = now; Revision++; Touch(now); } }
}

public sealed class MemoryEntityAlias : AuditableEntity
{
    private MemoryEntityAlias() { }
    public MemoryEntityAlias(Guid entityId, string alias, DateTimeOffset now)
    {
        InitializeAudit(now); EntityId = entityId; Alias = MemoryText.Clean(alias, 200);
        NormalizedAlias = MemoryText.Normalize(alias, 200);
    }
    public Guid EntityId { get; private set; }
    public string Alias { get; private set; } = "";
    public string NormalizedAlias { get; private set; } = "";
}

public sealed class MemoryRelation : AuditableEntity
{
    private MemoryRelation() { }
    public MemoryRelation(Guid subjectEntityId, string predicate, Guid objectEntityId, DateTimeOffset? validFrom, DateTimeOffset? validUntil, DateTimeOffset now)
    {
        if (validFrom is not null && validUntil is not null && validUntil <= validFrom) throw new ArgumentException("Intervalo de validade inválido.");
        InitializeAudit(now); SubjectEntityId = subjectEntityId; ObjectEntityId = objectEntityId;
        Predicate = MemoryText.NormalizePredicate(predicate);
        ValidFrom = validFrom?.ToUniversalTime(); ValidUntil = validUntil?.ToUniversalTime(); Status = MemoryStatus.Active; Revision = 1;
    }
    public Guid SubjectEntityId { get; private set; }
    public string Predicate { get; private set; } = "";
    public Guid ObjectEntityId { get; private set; }
    public MemoryStatus Status { get; private set; }
    public DateTimeOffset? ValidFrom { get; private set; }
    public DateTimeOffset? ValidUntil { get; private set; }
    public DateTimeOffset? SupersededAt { get; private set; }
    public Guid? SupersededById { get; private set; }
    public DateTimeOffset? ForgottenAt { get; private set; }
    public int Revision { get; private set; }
    public void Supersede(Guid replacementId, DateTimeOffset now)
    {
        if (Status != MemoryStatus.Active || replacementId == Id) throw new InvalidOperationException("Relação não pode ser substituída.");
        Status = MemoryStatus.Superseded; SupersededAt = now; SupersededById = replacementId; Revision++; Touch(now);
    }
    public bool CloseValidity(DateTimeOffset at, DateTimeOffset now)
    {
        at = at.ToUniversalTime();
        if (Status != MemoryStatus.Active || ValidFrom is not null && at <= ValidFrom || ValidUntil is not null && ValidUntil != at)
            throw new InvalidOperationException("Relação não pode ser fechada nesse instante.");
        if (ValidUntil == at) return false;
        ValidUntil = at; Revision++; Touch(now); return true;
    }
    public bool Forget(DateTimeOffset now)
    {
        if (Status == MemoryStatus.Forgotten) return false;
        if (Status != MemoryStatus.Active) throw new InvalidOperationException("Relação não está vigente.");
        Status = MemoryStatus.Forgotten; ForgottenAt = now; Revision++; Touch(now); return true;
    }
}

public sealed class MemoryRelationEvidence
{
    private MemoryRelationEvidence() { }
    public MemoryRelationEvidence(Guid relationId, Guid memoryId, DateTimeOffset now)
    { RelationId = relationId; MemoryId = memoryId; CreatedAt = now; }
    public Guid RelationId { get; private set; }
    public Guid MemoryId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}

public sealed class MemoryProjectionJob : AuditableEntity
{
    private MemoryProjectionJob() { }
    public MemoryProjectionJob(MemoryProjectionTarget target, MemoryAggregateType type, Guid aggregateId, int revision, MemoryProjectionOperation operation, DateTimeOffset now)
    {
        if (revision < 1) throw new ArgumentException("Revision deve ser positiva.");
        InitializeAudit(now); ProjectionTarget = target; AggregateType = type; AggregateId = aggregateId;
        AggregateRevision = revision; Operation = operation; Status = MemoryProjectionStatus.Pending;
    }
    public MemoryProjectionTarget ProjectionTarget { get; private set; }
    public MemoryAggregateType AggregateType { get; private set; }
    public Guid AggregateId { get; private set; }
    public int AggregateRevision { get; private set; }
    public MemoryProjectionOperation Operation { get; private set; }
    public MemoryProjectionStatus Status { get; private set; }
    public int Attempt { get; private set; }
    public DateTimeOffset? NextAttemptAt { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset? ProcessingStartedAt { get; private set; }
    public DateTimeOffset? LeaseExpiresAt { get; private set; }
    public Guid? LeaseId { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public void Claim(Guid leaseId, DateTimeOffset now, TimeSpan duration)
    {
        if (Status == MemoryProjectionStatus.Completed || (Status == MemoryProjectionStatus.Processing && LeaseExpiresAt > now))
            throw new InvalidOperationException("Projection job is not claimable.");
        Status = MemoryProjectionStatus.Processing;
        Attempt++;
        ProcessingStartedAt = now;
        LeaseExpiresAt = now.Add(duration);
        LeaseId = leaseId;
        NextAttemptAt = null;
        LastError = null;
        CompletedAt = null;
        Touch(now);
    }

    public bool Complete(Guid leaseId, DateTimeOffset now)
    {
        if (Status != MemoryProjectionStatus.Processing || LeaseId != leaseId) return false;
        Status = MemoryProjectionStatus.Completed;
        CompletedAt = now;
        LeaseId = null;
        LeaseExpiresAt = null;
        Touch(now);
        return true;
    }

    public bool Fail(Guid leaseId, string errorCode, DateTimeOffset now, TimeSpan? retryDelay)
    {
        if (Status != MemoryProjectionStatus.Processing || LeaseId != leaseId) return false;
        Status = retryDelay is null ? MemoryProjectionStatus.Failed : MemoryProjectionStatus.Pending;
        LastError = errorCode;
        NextAttemptAt = retryDelay is null ? null : now.Add(retryDelay.Value);
        LeaseId = null;
        LeaseExpiresAt = null;
        Touch(now);
        return true;
    }

    public void Requeue(DateTimeOffset now)
    {
        if (Status == MemoryProjectionStatus.Processing && LeaseExpiresAt > now) return;
        Status = MemoryProjectionStatus.Pending;
        Attempt = 0;
        NextAttemptAt = null;
        LeaseId = null;
        LeaseExpiresAt = null;
        CompletedAt = null;
        LastError = null;
        Touch(now);
    }
}

public sealed class MemoryExtractionJob : AuditableEntity
{
    private MemoryExtractionJob() { }
    public MemoryExtractionJob(Guid conversationId, Guid userMessageId, DateTimeOffset now)
    {
        InitializeAudit(now);
        ConversationId = conversationId;
        UserMessageId = userMessageId;
        Status = MemoryExtractionStatus.Pending;
    }

    public Guid? ConversationId { get; private set; }
    public Guid? UserMessageId { get; private set; }
    public MemoryExtractionStatus Status { get; private set; }
    public int Attempt { get; private set; }
    public DateTimeOffset? NextAttemptAt { get; private set; }
    public DateTimeOffset? ProcessingStartedAt { get; private set; }
    public DateTimeOffset? LeaseExpiresAt { get; private set; }
    public Guid? LeaseId { get; private set; }
    public string? LastError { get; private set; }
    public int CandidatesCount { get; private set; }
    public int CreatedCount { get; private set; }
    public int ReinforcedCount { get; private set; }
    public int CorrectedCount { get; private set; }
    public int TransitionedCount { get; private set; }
    public int GraphMutationsCount { get; private set; }
    public int SkippedCount { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public bool Complete(Guid leaseId, DateTimeOffset now, int candidates, int created, int reinforced,
        int corrected, int transitioned, int graphMutations, int skipped)
    {
        if (Status != MemoryExtractionStatus.Processing || LeaseId != leaseId) return false;
        Status = MemoryExtractionStatus.Completed;
        CandidatesCount = candidates; CreatedCount = created; ReinforcedCount = reinforced;
        CorrectedCount = corrected; TransitionedCount = transitioned;
        GraphMutationsCount = graphMutations; SkippedCount = skipped;
        CompletedAt = now; LeaseId = null; LeaseExpiresAt = null; NextAttemptAt = null; LastError = null;
        Touch(now); return true;
    }

    public bool Fail(Guid leaseId, string code, DateTimeOffset now, TimeSpan? retryDelay)
    {
        if (Status != MemoryExtractionStatus.Processing || LeaseId != leaseId) return false;
        Status = retryDelay is null ? MemoryExtractionStatus.Failed : MemoryExtractionStatus.Pending;
        LastError = code;
        NextAttemptAt = retryDelay is null ? null : now.Add(retryDelay.Value);
        LeaseId = null; LeaseExpiresAt = null;
        Touch(now); return true;
    }
}
