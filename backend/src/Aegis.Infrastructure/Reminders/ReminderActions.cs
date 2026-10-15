using System.Security.Cryptography;
using System.Text.Json;
using Aegis.Application.Nodes;
using Aegis.Application.Reminders;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
namespace Aegis.Infrastructure.Reminders;

public sealed class ReminderActionTokens(IDataProtectionProvider provider, TimeProvider clock) : IReminderActionTokens
{
    private readonly IDataProtector protector = provider.CreateProtector("Aegis.ReminderAction.v2");
    private sealed record Claims(Guid ReminderId, int Revision, int Generation, Guid RecipientNodeId, DateTimeOffset ExpiresAt);
    public string Create(Reminder reminder, DateTimeOffset expiresAt, Guid recipientNodeId)
    {
        if (recipientNodeId == Guid.Empty || expiresAt <= clock.GetUtcNow() || expiresAt > clock.GetUtcNow().AddDays(7)) throw new ArgumentException("Escopo de interação inválido.");
        return protector.Protect(JsonSerializer.Serialize(new Claims(reminder.Id, reminder.Revision, reminder.Generation, recipientNodeId, expiresAt)));
    }
    public bool Validate(string token, Guid reminderId, int revision, int generation) => TryValidateRecipient(token, reminderId, revision, generation, out _);
    public bool TryValidateRecipient(string token, Guid reminderId, int revision, int generation, out Guid recipientNodeId)
    {
        recipientNodeId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096) return false;
        try
        {
            var c = JsonSerializer.Deserialize<Claims>(protector.Unprotect(token));
            if (c is null || c.ReminderId != reminderId || c.Revision != revision || c.Generation != generation || c.RecipientNodeId == Guid.Empty || c.ExpiresAt <= clock.GetUtcNow()) return false;
            recipientNodeId = c.RecipientNodeId; return true;
        }
        catch (Exception e) when (e is CryptographicException or JsonException or FormatException) { return false; }
    }
}
public sealed class ReminderActionStore(AegisDbContext db, ReminderStore reminders, TimeProvider clock) : IReminderActionStore
{
    public async Task<ReminderActionResult> ApplyAsync(Guid reminderId, ReminderActionRequest request, CancellationToken ct) => await reminders.LockedAsync(reminderId, async reminder =>
    {
        var old = await db.Set<ReminderActionReceipt>().AsNoTracking().SingleOrDefaultAsync(a => a.Id == request.ActionId, ct);
        if (old is not null)
        {
            if (old.ReminderId != reminderId || old.ExpectedRevision != request.ExpectedRevision || old.Generation != request.Generation || old.Action != request.Action || old.SnoozeMinutes != request.SnoozeMinutes)
                throw new ReminderException("action_id_conflict", "Identificador de ação já utilizado com outros parâmetros.");
            return new ReminderActionResult(old.Applied, true, !old.Applied, JsonSerializer.Deserialize<JsonElement>(old.ResponseJson));
        }
        var valid = reminder.Revision == request.ExpectedRevision && reminder.Generation == request.Generation && reminder.TriggeredAt is not null && reminder.AcknowledgedAt is null && reminder.Status != ReminderStatus.Cancelled;
        if (valid)
        {
            if (reminder.ObserverNodeId is { } observer && reminder.ObserverPostedAt is { } posted &&
                !await db.Set<ReminderRecipient>().AnyAsync(p => p.ReminderId == reminderId && p.Generation == reminder.Generation && p.NodeId == observer, ct))
                db.Set<ReminderRecipient>().Add(ReminderRecipient.LocalPosted(reminderId, reminder.Generation, observer, posted));
            if (request.Action == "acknowledge") reminder.Acknowledge(clock.GetUtcNow()); else reminder.Snooze(clock.GetUtcNow(), request.SnoozeMinutes);
            // Copies are withdrawn by the native reconciliation worker; offline Nodes remain pending.
        }
        var view = ReminderService.View(reminder);
        db.Set<ReminderActionReceipt>().Add(new(request.ActionId, reminderId, request.ExpectedRevision, request.Generation, request.Action, valid, JsonSerializer.Serialize(view, ReminderRecurrenceRule.JsonOptions), clock.GetUtcNow(), request.SnoozeMinutes));
        return new ReminderActionResult(valid, false, !valid, view);
    }, ct);
}

public sealed class ReminderWithdrawalReconciler(AegisDbContext db, INodeRegistry nodes, INodeNotificationDispatcher dispatcher, TimeProvider clock)
{
    public async Task ReconcileAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var candidates = from recipient in db.Set<ReminderRecipient>() join reminder in db.Reminders on recipient.ReminderId equals reminder.Id
            where recipient.NodeId != null && recipient.WithdrawnAt == null && recipient.WithdrawalAttempts < 4 &&
                (recipient.WithdrawalCommandExpiresAt == null || recipient.WithdrawalCommandExpiresAt > now) &&
                (recipient.WithdrawalNextAttemptAt == null || recipient.WithdrawalNextAttemptAt <= now) &&
                (reminder.AcknowledgedAt != null || reminder.Generation > recipient.Generation || reminder.Status == ReminderStatus.Cancelled || reminder.Status == ReminderStatus.Paused)
                && db.Nodes.Any(n => n.Id == recipient.NodeId && n.Enabled && n.RevokedAt == null)
                && db.NodeCapabilities.Any(c => c.NodeId == recipient.NodeId && c.Name == ReminderNotificationContract.WithdrawCapability && c.Version >= 1)
            select recipient;
        // Legacy/disabled recipients remain reconcilable if their implementation
        // or administrative state changes, without consuming the bounded batch.
        if (!await candidates.AnyAsync(ct)) return;
        var inventory = await nodes.ListForDeliveryAsync(ct);
        var eligibleIds = inventory.Where(n => n.Enabled && n.RevokedAt is null && n.Capabilities.Any(c => c.Name == ReminderNotificationContract.WithdrawCapability && c.Version >= 1)).Select(n => n.Id).ToArray();
        var pending = await candidates.Where(r => eligibleIds.Contains(r.NodeId!.Value))
            .OrderBy(r => r.UpdatedAt).ThenBy(r => r.Id).Select(r => r.Id).Take(50).ToArrayAsync(ct);
        foreach (var id in pending)
        {
            Guid nodeId; NodeNotificationCommand command;
            await using (var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null)
            {
                var recipient = await LoadLocked(id, ct); if (recipient?.NodeId is not { } target || !recipient.CanBeginWithdrawal(clock.GetUtcNow())) continue;
                var reminder = await db.Reminders.AsNoTracking().SingleOrDefaultAsync(r => r.Id == recipient.ReminderId, ct);
                if (reminder is null || !RequiresWithdrawal(reminder, recipient.Generation)) continue;
                recipient.BeginWithdrawal(clock.GetUtcNow());
                command = new(recipient.WithdrawalCommandId!.Value, ReminderNotificationContract.WithdrawCapability, 1, recipient.WithdrawalCommandExpiresAt!.Value,
                    new(null, null, recipient.ReminderId, Generation: recipient.Generation)); nodeId = target;
                // Persist identity, lifetime and cumulative budget before native I/O.
                await db.SaveChangesAsync(ct); if (tx is not null) await tx.CommitAsync(ct);
            }
            var result = await dispatcher.DispatchToNodeAsync(nodeId, command, ct);
            await using (var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null)
            {
                var recipient = await LoadLocked(id, ct);
                if (recipient is not null && recipient.WithdrawnAt is null && recipient.WithdrawalCommandId == command.CommandId)
                {
                    if (result.Status is "success" or "duplicate" && result.Transport == "live_websocket") recipient.Withdraw(clock.GetUtcNow());
                    else if (result.Status == "accepted") recipient.QueueWithdrawal(clock.GetUtcNow());
                    await db.SaveChangesAsync(ct);
                }
                if (tx is not null) await tx.CommitAsync(ct);
            }
        }
    }
    public static bool RequiresWithdrawal(Reminder reminder, int generation) => reminder.AcknowledgedAt is not null || reminder.Generation > generation ||
        reminder.Status is ReminderStatus.Cancelled or ReminderStatus.Paused;
    private async Task<ReminderRecipient?> LoadLocked(Guid id, CancellationToken ct)
    {
        var recipient = db.Database.IsNpgsql()
            ? (await db.Set<ReminderRecipient>().FromSqlInterpolated($"SELECT * FROM reminder_recipients WHERE \"Id\" = {id} FOR UPDATE SKIP LOCKED").AsNoTracking().ToListAsync(ct)).SingleOrDefault()
            : await db.Set<ReminderRecipient>().AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
        if (recipient is null) return null;
        var previous = db.ChangeTracker.Entries<ReminderRecipient>().FirstOrDefault(e => e.Entity.Id == id); if (previous is not null) previous.State = EntityState.Detached;
        db.Attach(recipient); return recipient;
    }
}
