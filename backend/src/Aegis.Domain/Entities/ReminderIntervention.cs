namespace Aegis.Domain.Entities;

// A single geographic activation incident, not a general automation job.
public sealed class ReminderIntervention
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RuleId { get; set; }
    public int ActivationEpoch { get; set; }
    public Guid? SharedIncidentId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public int RecoveryAttempts { get; set; }
    public string RecoveryResultsJson { get; set; } = "[]";
    public string State { get; set; } = "recovering";
    public string? Diagnosis { get; set; }
    public string? RequiredAction { get; set; }
    public int GenerationAttempts { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public Guid? MessageId { get; set; }
    public string? LastError { get; set; }
}

// Independently leased outbox; message creation never waits for native delivery.
public sealed class ProactiveNotification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MessageId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid? NodeId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public int Attempts { get; set; }
    public string State { get; set; } = "pending";
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public string? Transport { get; set; }
    public string? LastResult { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public DateTimeOffset? PostedAt { get; set; }
}

// Communication episode. Individual activation incidents retain their recovery history.
public sealed class SharedReminderIncident
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string GroupKey { get; set; } = "";
    public Guid ObserverNodeId { get; set; }
    public string Subsystem { get; set; } = "geofence";
    public string Cause { get; set; } = "";
    public string RequiredActionKey { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? HealthySince { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string State { get; set; } = "pending";
    public Guid? ConversationId { get; set; }
    public string Diagnosis { get; set; } = "";
    public string RequiredAction { get; set; } = "";
    public int AffectedRuleCount { get; set; }
    public string ProblemSignature { get; set; } = "";
    public int GenerationAttempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public Guid? MessageId { get; set; }
    public string? LastError { get; set; }
}
