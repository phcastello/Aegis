using Aegis.Api.Nodes;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aegis.Api.Controllers;

// Native clients send only their bounded local presentation inventory on reconnect/lifecycle events.
// This is not a list poll and returns no reminder text or credential material.
[ApiController, Route("api/nodes/reminders/reconcile"), Authorize(Policy = "NodeManagement"), ServiceFilter(typeof(NodeApiFilter)), RequestSizeLimit(16384)]
public sealed class RemindersReconcileController(AegisDbContext db) : ControllerBase
{
    public sealed record Presentation(Guid ReminderId, int Revision, int Generation);
    public sealed record ReconcileRequest(IReadOnlyList<Presentation> Items);
    [HttpPost]
    public async Task<IActionResult> Reconcile(ReconcileRequest request, CancellationToken ct)
    {
        if (request.Items is null || request.Items.Count > 100 || request.Items.Any(p => p is null || p.ReminderId == Guid.Empty || p.Revision < 1 || p.Generation < 1) ||
            request.Items.Select(p => p.ReminderId).Distinct().Count() != request.Items.Count)
            return BadRequest(new { code = "invalid_presentation_inventory", error = "Envie até 100 apresentações locais distintas com revisão e geração válidas." });
        var ids = request.Items.Select(p => p.ReminderId).ToArray();
        var states = await db.Reminders.AsNoTracking().Where(r => ids.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        var withdraw = request.Items.Where(local => {
            // Unsynchronized local geographic events must be flushed before being included by the client.
            if (!states.TryGetValue(local.ReminderId, out var current)) return true;
            // A response from an older backend snapshot must not erase a newer native presentation.
            if (current.Generation < local.Generation) return false;
            if (current.Generation > local.Generation || current.AcknowledgedAt is not null || current.Status == ReminderStatus.Cancelled) return true;
            // A future occurrence edited before delivery supersedes an old packet for that same generation.
            return current.Revision > local.Revision && current.Status == ReminderStatus.Scheduled && current.TriggeredAt is null;
        }).Select(p => new { p.ReminderId, p.Generation }).ToArray();
        // Failed/unknown delivery alone is not recognition and does not retract a known native post.
        return Ok(new { withdraw });
    }
}
