namespace Aegis.Domain.Entities;

public sealed class ReminderRecipient : AuditableEntity
{
    private ReminderRecipient() { }
    public ReminderRecipient(Guid reminderId, int generation, Guid nodeId, DateTimeOffset now)
    { InitializeAudit(now); ReminderId = reminderId; Generation = generation; NodeId = nodeId; NextAttemptAt = now; }
    public Guid ReminderId { get; private set; }
    public int Generation { get; private set; }
    public Guid? NodeId { get; private set; }
    public int Attempts { get; private set; }
    public Guid? CommandId { get; private set; }
    public DateTimeOffset? CommandExpiresAt { get; private set; }
    public DateTimeOffset NextAttemptAt { get; private set; }
    public string Status { get; private set; } = "pending";
    public bool Ambiguous { get; private set; }
    public DateTimeOffset? ProviderAcceptedAt { get; private set; }
    public DateTimeOffset? PostedAt { get; private set; }
    public DateTimeOffset? WithdrawnAt { get; private set; }
    public DateTimeOffset? WithdrawalQueuedAt { get; private set; }
    public int WithdrawalAttempts { get; private set; }
    public Guid? WithdrawalCommandId { get; private set; }
    public DateTimeOffset? WithdrawalCommandExpiresAt { get; private set; }
    public DateTimeOffset? WithdrawalNextAttemptAt { get; private set; }
    public string? Transport { get; private set; }
    public static ReminderRecipient LocalPosted(Guid reminderId, int generation, Guid nodeId, DateTimeOffset postedAt)
    {
        var recipient = new ReminderRecipient(reminderId, generation, nodeId, postedAt);
        recipient.Status = "accepted"; recipient.PostedAt = postedAt; recipient.Transport = "android_local"; return recipient;
    }
    public static ReminderRecipient Recover(ReminderDeliveryAttempt attempt, int generation, DateTimeOffset now)
    {
        var recipient = new ReminderRecipient(attempt.ReminderId, generation, attempt.NodeId!.Value, now);
        recipient.Attempts = attempt.Attempt; recipient.CommandId = attempt.CommandId; recipient.CommandExpiresAt = attempt.CommandExpiresAt;
        recipient.Ambiguous = attempt.CompletedAt is null || attempt.OutcomeAmbiguous; recipient.NextAttemptAt = attempt.RetryAt ?? now;
        if (attempt.AcceptedAt is not null) { recipient.Status = "accepted"; recipient.ProviderAcceptedAt = attempt.AcceptedAt; }
        return recipient;
    }
    public void Stop(DateTimeOffset now) { if (Status == "pending") Status = "failed"; Touch(now); }
    public bool NeedsRetry(DateTimeOffset now) => Status == "pending" && Attempts < 5 && NextAttemptAt <= now;
    public void Begin(DateTimeOffset now, Guid? replayCommandId = null, DateTimeOffset? replayExpiresAt = null, DateTimeOffset? notAfter = null)
    {
        if ((replayCommandId is null) != (replayExpiresAt is null) || replayCommandId == Guid.Empty) throw new ArgumentException("Identidade de replay inválida.");
        // Pending from a crashed process is ambiguous and reuses its original command.
        if (replayCommandId is not null)
        {
            // The geographic observer may already have posted locally without
            // a receipt. Keep its event identity and original lifetime even
            // after a definite route failure; never replace an attempted ID.
            CommandId ??= replayCommandId; CommandExpiresAt ??= replayExpiresAt;
        }
        else if (CommandId is null || !Ambiguous)
        {
            CommandId = Guid.NewGuid(); CommandExpiresAt = now.AddSeconds(300);
            if (notAfter < CommandExpiresAt) CommandExpiresAt = notAfter;
        }
        Attempts++; Ambiguous = true; Touch(now);
    }
    public void Result(string status, string? transport, bool ambiguous, DateTimeOffset now)
    {
        Transport = transport; Ambiguous = ambiguous;
        if (status is "success" or "accepted" or "duplicate")
        {
            Status = "accepted"; ProviderAcceptedAt = now; Ambiguous = false;
            if (status == "success" && transport == "live_websocket") PostedAt = now;
        }
        else if (Attempts >= 5 || ambiguous && CommandExpiresAt <= now || status == "expired") Status = "failed";
        else NextAttemptAt = now.AddSeconds(new[] {10, 30, 120, 300}[Math.Min(Attempts - 1, 3)]);
        Touch(now);
    }
    public void QueueWithdrawal(DateTimeOffset now) { WithdrawalQueuedAt = now; Touch(now); }
    public bool CanBeginWithdrawal(DateTimeOffset now) => WithdrawnAt is null && WithdrawalAttempts < 4 &&
        (WithdrawalCommandExpiresAt is null || WithdrawalCommandExpiresAt > now) && (WithdrawalNextAttemptAt is null || WithdrawalNextAttemptAt <= now);
    public void BeginWithdrawal(DateTimeOffset now)
    {
        if (!CanBeginWithdrawal(now)) throw new InvalidOperationException("Withdrawal budget is exhausted or not due.");
        WithdrawalCommandId ??= Guid.NewGuid(); WithdrawalCommandExpiresAt ??= now.AddSeconds(300);
        WithdrawalAttempts++;
        WithdrawalNextAttemptAt = WithdrawalAttempts < 4
            ? WithdrawalCommandExpiresAt.Value.AddSeconds(-300 + new[] { 0, 30, 90, 210 }[WithdrawalAttempts])
            : null;
        Touch(now);
    }
    public void Withdraw(DateTimeOffset now) { if (WithdrawnAt is not null) return; WithdrawnAt = now; if (Status == "pending") Status = "withdrawn"; Touch(now); }
}

public sealed class ReminderActionReceipt : AuditableEntity
{
    private ReminderActionReceipt() { }
    public ReminderActionReceipt(Guid actionId, Guid reminderId, int revision, int generation, string action, bool applied, string responseJson, DateTimeOffset now, int snoozeMinutes = 10)
    { InitializeAudit(now); Id = actionId; ReminderId = reminderId; ExpectedRevision = revision; Generation = generation; Action = action; SnoozeMinutes = snoozeMinutes; Applied = applied; ResponseJson = responseJson; }
    public Guid ReminderId { get; private set; }
    public int ExpectedRevision { get; private set; }
    public int Generation { get; private set; }
    public string Action { get; private set; } = "";
    public int SnoozeMinutes { get; private set; }
    public bool Applied { get; private set; }
    public string ResponseJson { get; private set; } = "";
}
