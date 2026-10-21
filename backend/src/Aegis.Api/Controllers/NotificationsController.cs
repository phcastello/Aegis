using Aegis.Application.Reminders;
using Aegis.Infrastructure.Reminders;
using Microsoft.AspNetCore.Mvc;

namespace Aegis.Api.Controllers;

// Compatibility for installed browser clients and previously issued action tokens.
// No subscriptions, delivery, or new action tokens are created by this controller.
[ApiController]
[Route("api/notifications")]
[RequestSizeLimit(8192)]
public sealed class NotificationsController(ReminderStore reminders, ILegacyReminderInteractionTokens interactions,
    TimeProvider clock) : ControllerBase
{
    private const string RetirementMessage = "O Web Push foi encerrado. Para receber lembretes, use um aplicativo Android ou Windows pareado. O chat web continua podendo criar lembretes para esses dispositivos.";
    public sealed record SubscriptionRequest(Guid DeviceId, string Endpoint, string P256dh, string Auth, string? UserAgent);
    public sealed record TokenRequest(string Token);
    public sealed record StatusRequest(string Token, string? Endpoint = null);

    [HttpGet("configuration")]
    public IActionResult Configuration() => Ok(new { enabled = false, publicKey = (string?)null, retired = true, message = RetirementMessage });

    [HttpPost("subscriptions")]
    public IActionResult Register(SubscriptionRequest request) => StatusCode(StatusCodes.Status410Gone,
        new { code = "web_push_retired", error = RetirementMessage, retryable = false });

    [HttpPost("subscriptions/{id:guid}/status")]
    public IActionResult Status(Guid id, StatusRequest request) => Ok(new { active = false, retired = true });

    // Old clients can finish their local deactivation without changing stored history.
    [HttpPost("subscriptions/{id:guid}/disable")]
    public IActionResult Disable(Guid id, TokenRequest request) => NoContent();

    [HttpPost("reminders/{id:guid}/acknowledge")]
    public Task<IActionResult> Acknowledge(Guid id, TokenRequest request, CancellationToken ct) => Interact(id, request.Token, "acknowledge", ct);

    [HttpPost("reminders/{id:guid}/open")]
    public Task<IActionResult> Open(Guid id, TokenRequest request, CancellationToken ct) => Interact(id, request.Token, "open", ct);

    private async Task<IActionResult> Interact(Guid id, string token, string action, CancellationToken ct)
    {
        if (!interactions.Validate(token, id, action)) return NotFound();
        try
        {
            await reminders.LockedAsync(id, r =>
            {
                if (r.Generation != 1 || r.SeriesId is not null)
                    throw new ReminderException("legacy_interaction_expired", "Use as ações da ocorrência atual.");
                if (action == "acknowledge") r.Acknowledge(clock.GetUtcNow());
                else r.Open(clock.GetUtcNow());
                return Task.FromResult(true);
            }, ct);
            return NoContent();
        }
        catch (ReminderException) { return NotFound(); }
        catch (ArgumentException) { return Conflict(new { error = "Este lembrete ainda não disparou." }); }
    }
}
