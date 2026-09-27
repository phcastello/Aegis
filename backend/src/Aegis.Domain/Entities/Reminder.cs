using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text;

namespace Aegis.Domain.Entities;

public enum ReminderStatus { Scheduled, Processing, Triggered, Cancelled, Failed }

public sealed class Reminder : AuditableEntity
{
    private Reminder() { }
    public Reminder(string text, DateTimeOffset dueAt, string timeZoneId, Guid? sourceConversationId, DateTimeOffset now)
    {
        ValidateText(text);
        if (dueAt <= now) throw new ArgumentException("O horário do lembrete precisa estar no futuro.");
        InitializeAudit(now);
        Text = text.Trim();
        DueAtUtc = dueAt.ToUniversalTime();
        TimeZoneId = timeZoneId;
        SourceConversationId = sourceConversationId;
        NextAttemptAt = DueAtUtc;
    }
    public string Text { get; private set; } = "";
    public DateTimeOffset DueAtUtc { get; private set; }
    public string TimeZoneId { get; private set; } = "";
    public ReminderStatus Status { get; private set; }
    public Guid? SourceConversationId { get; private set; }
    public DateTimeOffset? TriggeredAt { get; private set; }
    public DateTimeOffset? AcknowledgedAt { get; private set; }
    public DateTimeOffset? OpenedAt { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public DateTimeOffset NextAttemptAt { get; private set; }
    public DateTimeOffset? ProcessingStartedAt { get; private set; }
    public DateTimeOffset? LeaseExpiresAt { get; private set; }
    public Guid? LeaseId { get; private set; }

    public void Update(string? text, DateTimeOffset? dueAt, DateTimeOffset now, string? timeZoneId = null)
    {
        RequireFuture(now);
        if (text is null && dueAt is null) throw new ArgumentException("Informe texto ou horário para alterar.");
        if (text is not null) ValidateText(text);
        if (dueAt is not null && dueAt <= now) throw new ArgumentException("O horário precisa estar no futuro.");
        if (timeZoneId is not null) TimeZoneId = timeZoneId;
        if (text is not null) Text = text.Trim();
        if (dueAt is not null) NextAttemptAt = DueAtUtc = dueAt.Value.ToUniversalTime();
        Touch(now);
    }
    public void Cancel(DateTimeOffset now)
    {
        if (Status == ReminderStatus.Cancelled) return;
        RequireFuture(now);
        Status = ReminderStatus.Cancelled;
        CancelledAt = now;
        Touch(now);
    }
    private void RequireFuture(DateTimeOffset now)
    {
        if (Status != ReminderStatus.Scheduled || TriggeredAt is not null || DueAtUtc <= now)
            throw new ArgumentException("Esse lembrete já começou a disparar ou não está mais agendado no futuro.");
    }
    private static void ValidateText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Length > 600 ||
            Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(text.Trim(), new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })) > 1900)
            throw new ArgumentException("O texto deve conter entre 1 e 600 caracteres.");
    }
    public bool CanClaim(DateTimeOffset now) => AcknowledgedAt is null && DueAtUtc <= now &&
        (Status == ReminderStatus.Scheduled && NextAttemptAt <= now ||
         Status == ReminderStatus.Processing && LeaseExpiresAt <= now);
    public void Claim(Guid leaseId, DateTimeOffset now)
    {
        if (!CanClaim(now)) throw new InvalidOperationException("Reminder cannot be claimed.");
        Status = ReminderStatus.Processing;
        LeaseId = leaseId;
        ProcessingStartedAt = now;
        LeaseExpiresAt = now.AddMinutes(2);
        Touch(now);
    }
    public bool OwnsLease(Guid leaseId) => Status == ReminderStatus.Processing && LeaseId == leaseId;
    public bool BeginTrigger(DateTimeOffset now)
    {
        if (TriggeredAt is not null) return false;
        TriggeredAt = now;
        Touch(now);
        return true;
    }
    public void RenewLease(DateTimeOffset now) { LeaseExpiresAt = now.AddMinutes(2); Touch(now); }
    public void Finish(DateTimeOffset? retryAt, bool anyAccepted, DateTimeOffset now)
    {
        Status = AcknowledgedAt is not null ? ReminderStatus.Triggered : retryAt is not null ? ReminderStatus.Scheduled : anyAccepted ? ReminderStatus.Triggered : ReminderStatus.Failed;
        if (retryAt is not null && AcknowledgedAt is null) NextAttemptAt = retryAt.Value;
        LeaseId = null;
        LeaseExpiresAt = null;
        Touch(now);
    }
    public void Acknowledge(DateTimeOffset now)
    {
        if (TriggeredAt is null) throw new ArgumentException("O lembrete ainda não disparou.");
        if (AcknowledgedAt is null)
        {
            AcknowledgedAt = now;
            Touch(now);
            // Global recognition fulfills notification work without cancelling the reminder.
            // Existing delivery attempts retain their original outcomes/retry audit.
        }
        if (Status != ReminderStatus.Triggered || LeaseId is not null || LeaseExpiresAt is not null)
            Finish(null, true, now);
    }
    public void Open(DateTimeOffset now)
    {
        if (TriggeredAt is null) throw new ArgumentException("O lembrete ainda não disparou.");
        if (OpenedAt is null) { OpenedAt = now; Touch(now); }
    }
}
