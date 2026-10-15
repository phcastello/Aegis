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
        var pending = await (from recipient in db.Set<ReminderRecipient>() join reminder in db.Reminders on recipient.ReminderId equals reminder.Id
            where recipient.NodeId != null && recipient.WithdrawnAt == null && (reminder.AcknowledgedAt != null || reminder.Generation != recipient.Generation || reminder.Status == ReminderStatus.Cancelled)
            select recipient).OrderBy(r => r.UpdatedAt).Take(50).ToListAsync(ct);
        if (pending.Count == 0) return;
        var inventory = await nodes.ListForDeliveryAsync(ct);
        foreach (var recipient in pending)
        {
            var node = inventory.FirstOrDefault(n => n.Id == recipient.NodeId && n.Enabled && n.RevokedAt is null);
            if (node is null || !node.Capabilities.Any(c => c.Name == ReminderNotificationContract.WithdrawCapability && c.Version >= 1)) continue;
            if (recipient.WithdrawalQueuedAt > clock.GetUtcNow().AddMinutes(-5) && node.Availability != "online") continue;
            var result = await dispatcher.DispatchToNodeAsync(node.Id, new(Guid.NewGuid(), ReminderNotificationContract.WithdrawCapability, 1, clock.GetUtcNow().AddMinutes(5), new(null, null, recipient.ReminderId, Generation: recipient.Generation)), ct);
            if (result.Status is "success" or "duplicate" && result.Transport == "live_websocket") recipient.Withdraw(clock.GetUtcNow());
            else recipient.QueueWithdrawal(clock.GetUtcNow());
        }
        await db.SaveChangesAsync(ct);
    }
}
