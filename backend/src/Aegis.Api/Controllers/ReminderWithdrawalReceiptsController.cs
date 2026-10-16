using System.Security.Claims;
using System.Text.Json.Serialization;
using Aegis.Api.Nodes;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Persistence;
using Aegis.Infrastructure.Reminders;
using Aegis.Application.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aegis.Api.Controllers;

// A receipt proves a native withdrawal only for this authenticated recipient.
// Provider acceptance remains a separate timestamp and never substitutes for it.
[ApiController, Route("api/nodes/reminders/withdrawals/receipts"), Authorize(Policy = "NodeManagement"), ServiceFilter(typeof(NodeApiFilter)), RequestSizeLimit(16384)]
public sealed class ReminderWithdrawalReceiptsController(AegisDbContext db, ReminderStore store, TimeProvider clock) : ControllerBase
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record Item(Guid ReminderId, int Generation);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record ReceiptRequest(IReadOnlyList<Item> Items);
    [HttpPost]
    public async Task<IActionResult> Receive(ReceiptRequest request, CancellationToken ct)
    {
        if (request.Items is null || request.Items.Count > 100 || request.Items.Any(i => i is null || i.ReminderId == Guid.Empty || i.Generation < 1) ||
            request.Items.Select(i => (i.ReminderId, i.Generation)).Distinct().Count() != request.Items.Count)
            return BadRequest(new { code = "invalid_withdrawal_receipts", error = "Envie até 100 retiradas distintas com identificador e geração válidos." });
        var nodeId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!); var received = new List<Item>();
        foreach (var item in request.Items)
        {
            try
            {
                var accepted = await store.LockedAsync(item.ReminderId, async reminder => {
                    if (!ReminderWithdrawalReconciler.RequiresWithdrawal(reminder, item.Generation)) return false;
                    var recipient = await db.Set<ReminderRecipient>().SingleOrDefaultAsync(r => r.ReminderId == item.ReminderId && r.Generation == item.Generation && r.NodeId == nodeId, ct);
                    if (recipient is null) return false;
                    recipient.Withdraw(clock.GetUtcNow()); return true;
                }, ct);
                if (accepted) received.Add(item);
            }
            catch (ReminderException) { /* Unknown IDs produce the same empty result as unrelated copies. */ }
        }
        return Ok(new { received });
    }
}
