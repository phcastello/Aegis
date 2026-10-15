using Aegis.Api.Nodes;
using Aegis.Application.Reminders;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Persistence;
using Aegis.Infrastructure.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace Aegis.Api.Controllers;

[ApiController, Route("api/nodes/reminders"), Authorize(Policy = "NodeManagement"), ServiceFilter(typeof(NodeApiFilter)), RequestSizeLimit(8192)]
public sealed class RemindersController(AegisDbContext db, ReminderStore store, ReminderActionService actions, TimeProvider clock) : ControllerBase
{
    public sealed record CreateRequest(string Text, string DueAt, string TimeZoneId = "America/Sao_Paulo", string DeliveryPolicy = "allEligible");
    public sealed record UpdateRequest(int ExpectedRevision, string? Text = null, string? DueAt = null, string? TimeZoneId = null);
    [HttpGet] public async Task<IActionResult> List(CancellationToken ct) => Ok(new {
        reminders = (await db.Reminders.AsNoTracking().OrderByDescending(r => r.DueAtUtc).Take(200).ToArrayAsync(ct)).OrderBy(r => r.DueAtUtc).Select(ReminderService.View),
        series = (await db.Set<ReminderSeries>().AsNoTracking().Where(s => s.Status != "cancelled").Take(100).ToArrayAsync(ct)).Select(ReminderSeriesService.View),
        deliveries = await db.Set<ReminderRecipient>().AsNoTracking().OrderByDescending(r => r.UpdatedAt).Take(500)
            .Select(r => new { r.ReminderId, r.Generation, r.NodeId, r.Status, r.ProviderAcceptedAt, r.PostedAt, r.WithdrawnAt }).ToArrayAsync(ct)
    });
    [HttpPost] public async Task<IActionResult> Create(CreateRequest request, CancellationToken ct)
    {
        try
        {
            if (!await store.HasNotificationNodeAsync(ct)) return Conflict(new { code = "notifications_unavailable", error = "Habilite um dispositivo para receber lembretes." });
            var zone = TimeZoneInfo.FindSystemTimeZoneById(request.TimeZoneId);
            var reminder = new Reminder(request.Text, ReminderService.ParseInstant(request.DueAt), zone.Id, null, clock.GetUtcNow()); reminder.SetDeliveryPolicy(request.DeliveryPolicy);
            await store.AddAsync(reminder, ct); return Ok(ReminderService.View(reminder));
        }
        catch (Exception e) when (e is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException) { return BadRequest(new { code = "invalid_reminder", error = "Confira texto, horário, fuso e política do lembrete." }); }
    }
    [HttpPatch("{id:guid}")] public async Task<IActionResult> Update(Guid id, UpdateRequest request, CancellationToken ct)
    {
        try { return Ok(ReminderService.View(await store.LockedAsync(id, r => {
            if (r.Revision != request.ExpectedRevision) throw new InvalidOperationException("revision_conflict");
            if (request.TimeZoneId is not null) TimeZoneInfo.FindSystemTimeZoneById(request.TimeZoneId);
            r.Update(request.Text, request.DueAt is null ? null : ReminderService.ParseInstant(request.DueAt), clock.GetUtcNow(), request.TimeZoneId); return Task.FromResult(r);
        }, ct))); }
        catch (InvalidOperationException) { return Conflict(new { code = "revision_conflict", error = "O lembrete mudou. Atualize a tela." }); }
        catch (ReminderException) { return NotFound(); }
        catch (Exception e) when (e is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException) { return BadRequest(new { code = "invalid_reminder", error = "Não foi possível alterar esta ocorrência." }); }
    }
    [HttpDelete("{id:guid}")] public async Task<IActionResult> Cancel(Guid id, [FromQuery] int expectedRevision, CancellationToken ct)
    {
        try { return Ok(ReminderService.View(await store.LockedAsync(id, r => { if (r.Revision != expectedRevision) throw new InvalidOperationException("revision_conflict"); r.Cancel(clock.GetUtcNow()); return Task.FromResult(r); }, ct))); }
        catch (ReminderException) { return NotFound(); }
        catch (InvalidOperationException) { return Conflict(new { code = "revision_conflict", error = "O lembrete mudou. Atualize a tela." }); }
        catch (ArgumentException) { return Conflict(new { code = "occurrence_already_started", error = "Esta ocorrência já disparou; use OK ou Adiar." }); }
    }
    [HttpPost("{id:guid}/actions")] public async Task<IActionResult> Action(Guid id, ReminderActionRequest request, CancellationToken ct)
    {
        try { return Ok(await actions.ApplyAsync(id, request, ct)); }
        catch (ReminderException e) { return BadRequest(new { code = e.Code, error = e.Message }); }
        catch (ArgumentException) { return BadRequest(new { code = "invalid_action" }); }
    }
}

[ApiController, Route("api/nodes/reminder-series"), Authorize(Policy = "NodeManagement"), ServiceFilter(typeof(NodeApiFilter)), RequestSizeLimit(8192)]
public sealed class ReminderSeriesController(ReminderSeriesService series) : ControllerBase
{
    public sealed record CreateRequest(string Text, string TimeZoneId, ReminderRecurrenceRule Rule, string DeliveryPolicy = "allEligible");
    public sealed record ChangeRequest(int ExpectedRevision, string? Text = null, ReminderRecurrenceRule? Rule = null, string? DeliveryPolicy = null);
    public sealed record RevisionRequest(int ExpectedRevision);
    [HttpGet] public async Task<IActionResult> List(CancellationToken ct) => Ok(new { series = (await series.ListAsync(null, ct)).Select(ReminderSeriesService.View) });
    [HttpPost] public Task<IActionResult> Create(CreateRequest r, CancellationToken ct) => Safely(async () => ReminderSeriesService.View(await series.CreateAsync(null, r.Text, r.TimeZoneId, r.Rule, r.DeliveryPolicy, ct)));
    [HttpPatch("{id:guid}")] public Task<IActionResult> Update(Guid id, ChangeRequest r, CancellationToken ct) => Change(id, r.ExpectedRevision, "update", r.Text, r.Rule, r.DeliveryPolicy, ct);
    [HttpPost("{id:guid}/pause")] public Task<IActionResult> Pause(Guid id, RevisionRequest r, CancellationToken ct) => Change(id, r.ExpectedRevision, "pause", null, null, null, ct);
    [HttpPost("{id:guid}/resume")] public Task<IActionResult> Resume(Guid id, RevisionRequest r, CancellationToken ct) => Change(id, r.ExpectedRevision, "resume", null, null, null, ct);
    [HttpPost("{id:guid}/skip")] public Task<IActionResult> Skip(Guid id, RevisionRequest r, CancellationToken ct) => Change(id, r.ExpectedRevision, "skip", null, null, null, ct);
    [HttpDelete("{id:guid}")] public async Task<IActionResult> Cancel(Guid id, [FromQuery] int expectedRevision, CancellationToken ct)
    {
        // Mutations always require the revision displayed by the client.
        return await Change(id, expectedRevision, "cancel", null, null, null, ct);
    }
    private Task<IActionResult> Change(Guid id, int revision, string operation, string? text, ReminderRecurrenceRule? rule, string? policy, CancellationToken ct) => Safely(async () => ReminderSeriesService.View(await series.ChangeAsync(null, id, revision, operation, text, rule, policy, ct)));
    private async Task<IActionResult> Safely(Func<Task<object>> operation)
    {
        try { return Ok(await operation()); }
        catch (ReminderException e) { return BadRequest(new { code = e.Code, error = e.Message }); }
        catch (InvalidOperationException) { return Conflict(new { code = "revision_conflict", error = "A série mudou. Atualize a tela." }); }
        catch (Exception e) when (e is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException) { return BadRequest(new { code = "invalid_series", error = "Confira regra, fuso e encerramento da repetição." }); }
    }
}

// Scoped cryptographic proof is required; no anonymous acknowledgement is accepted.
[ApiController, Route("api/reminder-actions"), RequestSizeLimit(8192)]
public sealed class ScopedReminderActionsController(IReminderActionTokens tokens, ReminderActionService actions, AegisDbContext db) : ControllerBase
{
    public sealed record InteractionRequest(string Token, Guid ActionId, int ExpectedRevision, int Generation, string Action, int SnoozeMinutes = 10);
    [HttpPost("{id:guid}")] public async Task<IActionResult> Action(Guid id, InteractionRequest request, CancellationToken ct)
    {
        if (!tokens.TryValidateRecipient(request.Token, id, request.ExpectedRevision, request.Generation, out var recipientNodeId) ||
            !await db.Nodes.AsNoTracking().AnyAsync(n => n.Id == recipientNodeId && n.Enabled && n.RevokedAt == null, ct)) return NotFound();
        try { return Ok(await actions.ApplyAsync(id, new(request.ActionId, request.ExpectedRevision, request.Generation, request.Action, request.SnoozeMinutes), ct)); }
        catch (ReminderException) { return NotFound(); }
        catch (ArgumentException) { return BadRequest(new { code = "invalid_action" }); }
    }
}
