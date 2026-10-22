using System.Text.Json;

namespace Aegis.Domain.Entities;

public sealed class GeographicReminderRule : AuditableEntity
{
    private GeographicReminderRule() { }
    public GeographicReminderRule(string text, IReadOnlyList<Guid> placeIds, Guid observerNodeId, string transition, bool repeat,
        DateTimeOffset? startsAt, DateTimeOffset? endsAt, string timeZoneId, DateTimeOffset now, Guid? conversationId = null)
    {
        InitializeAudit(now); SyncStartedAt = now; SourceConversationId = conversationId;
        Change(text, placeIds, observerNodeId, transition, repeat, startsAt, endsAt, timeZoneId, now, false);
    }
    public string Text { get; private set; } = "";
    public string PlaceIdsJson { get; private set; } = "[]";
    public IReadOnlyList<Guid> PlaceIds => JsonSerializer.Deserialize<Guid[]>(PlaceIdsJson)!;
    public Guid ObserverNodeId { get; private set; }
    public Guid? SourceConversationId { get; private set; }
    public int Revision { get; private set; } = 1;
    public string Transition { get; private set; } = "enter";
    public bool Repeat { get; private set; }
    public DateTimeOffset? StartsAt { get; private set; }
    public DateTimeOffset? EndsAt { get; private set; }
    public string TimeZoneId { get; private set; } = "America/Sao_Paulo";
    public string DeliveryPolicy { get; private set; } = "allEligible";
    public string Status { get; private set; } = "active";
    public DateTimeOffset SyncStartedAt { get; private set; }
    public DateTimeOffset? FailureSince { get; private set; }
    public int ActivationEpoch { get; private set; } = 1;
    public string SyncStatus { get; private set; } = "pending_sync";
    public DateTimeOffset LeaseUntil { get; private set; }
    public DateTimeOffset? SyncedAt { get; private set; }
    public int? SyncedRevision { get; private set; }
    public string? ErrorCode { get; private set; }
    public long LastEventSequence { get; private set; }
    public DateTimeOffset? LastEventAt { get; private set; }
    public void Change(string text, IReadOnlyList<Guid> placeIds, Guid observerNodeId, string transition, bool repeat,
        DateTimeOffset? startsAt, DateTimeOffset? endsAt, string timeZoneId, DateTimeOffset now, bool increment = true)
    {
        Reminder.ValidateText(text);
        if (placeIds is null || placeIds.Count is < 1 or > 20 || placeIds.Any(id => id == Guid.Empty) || placeIds.Distinct().Count() != placeIds.Count ||
            observerNodeId == Guid.Empty || transition is not ("enter" or "exit") || startsAt >= endsAt || endsAt <= now)
            throw new ArgumentException("Lugar, observador, transição ou validade inválida.");
        if (timeZoneId != "UTC" && !TimeZoneInfo.TryConvertIanaIdToWindowsId(timeZoneId, out _)) throw new ArgumentException("Use um timezone IANA.");
        _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        Text = text.Trim(); PlaceIdsJson = JsonSerializer.Serialize(placeIds.Order()); ObserverNodeId = observerNodeId;
        Transition = transition; Repeat = repeat; StartsAt = startsAt?.ToUniversalTime(); EndsAt = endsAt?.ToUniversalTime(); TimeZoneId = timeZoneId;
        if (increment) { Revision++; BeginSync(now); }
        LeaseUntil = now.AddDays(7); if (EndsAt is { } end && end < LeaseUntil) LeaseUntil = end;
        Touch(now);
    }
    public void InvalidateMonitoring(DateTimeOffset now) { BeginSync(now); Touch(now); }
    public void InvalidateGeometry(DateTimeOffset now) { Revision++; BeginSync(now); Touch(now); }
    public void SetStatus(string action, DateTimeOffset now)
    {
        if (Status is "completed" or "cancelled") throw new ArgumentException("Esta regra já foi encerrada.");
        Status = action switch { "pause" => "paused", "resume" => "active", "cancel" => "cancelled", _ => throw new ArgumentException("Ação inválida.") };
        Revision++; BeginSync(now); Touch(now);
    }
    private void BeginSync(DateTimeOffset now) { ActivationEpoch++; SyncStartedAt = now; FailureSince = null; SyncStatus = "pending_sync"; ErrorCode = null; }
    public void Renew(DateTimeOffset now)
    {
        if (Status != "active") return;
        LeaseUntil = now.AddDays(7); if (EndsAt is { } end && end < LeaseUntil) LeaseUntil = end;
    }
    public void Confirm(int revision, string status, string? errorCode, DateTimeOffset now)
    {
        if (revision != Revision) return;
        if (status is not ("installed" or "waiting_initial" or "active" or "paused" or "completed" or "recoverable_error")) throw new ArgumentException("Estado de sincronização inválido.");
        if (errorCode is not null && (errorCode.Length > 64 || errorCode.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))) throw new ArgumentException("Código de erro inválido.");
        if (status == "recoverable_error" && FailureSince is null) {
            if (SyncStatus is not ("pending_sync" or "recoverable_error")) ActivationEpoch++;
            FailureSince = now;
        } else if (status != "recoverable_error") FailureSince = null;
        SyncStatus = Status == "cancelled" ? "removed" : status; SyncedAt = now; SyncedRevision = revision; ErrorCode = errorCode;
    }
    public bool AcceptEvent(int revision, string transition, long sequence, DateTimeOffset occurredAt, DateTimeOffset now)
    {
        if (revision != Revision || Status != "active" || transition != Transition || sequence <= LastEventSequence ||
            occurredAt < CreatedAt || occurredAt > now.AddMinutes(2) || occurredAt > LeaseUntil ||
            StartsAt is { } start && occurredAt < start || EndsAt is { } end && occurredAt >= end ||
            LastEventAt is { } previous && occurredAt <= previous) return false;
        LastEventSequence = sequence; LastEventAt = occurredAt;
        if (!Repeat) { Status = "completed"; SyncStatus = "completed"; }
        Touch(now); return true;
    }
}

public sealed class GeographicReminderEvent
{
    private GeographicReminderEvent() { }
    public GeographicReminderEvent(Guid id, Guid occurrenceId, Guid ruleId, Guid observerNodeId, int revision, long sequence,
        DateTimeOffset occurredAt, DateTimeOffset? postedAt, string result, DateTimeOffset now)
    { Id = id; OccurrenceId = occurrenceId; RuleId = ruleId; ObserverNodeId = observerNodeId; RuleRevision = revision; Sequence = sequence; OccurredAt = occurredAt; PostedAt = postedAt; Result = result; ReceivedAt = now; }
    public Guid Id { get; private set; }
    public Guid OccurrenceId { get; private set; }
    public Guid RuleId { get; private set; }
    public Guid? ObserverNodeId { get; private set; }
    public int RuleRevision { get; private set; }
    public long Sequence { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset? PostedAt { get; private set; }
    public DateTimeOffset ReceivedAt { get; private set; }
    public string Result { get; private set; } = "";
}
