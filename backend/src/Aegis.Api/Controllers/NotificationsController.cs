using System.Security.Cryptography;
using System.Text.Json;
using Aegis.Application.Reminders;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Persistence;
using Aegis.Infrastructure.Reminders;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aegis.Api.Controllers;

[ApiController]
[Route("api/notifications")]
[RequestSizeLimit(8192)]
public sealed class NotificationsController(AegisDbContext db, ReminderStore reminders, IReminderInteractionTokens interactions,
    IDataProtectionProvider protection, TimeProvider clock, IOptions<WebPushOptions> options) : ControllerBase
{
    private readonly IDataProtector subscriptionTokens = protection.CreateProtector("Aegis.PushSubscriptionManagement.v1");
    public sealed record SubscriptionRequest(Guid DeviceId, string Endpoint, string P256dh, string Auth, string? UserAgent);
    public sealed record TokenRequest(string Token);
    public sealed record StatusRequest(string Token, string? Endpoint = null);
    private sealed record SubscriptionClaims(Guid Id, Guid DeviceId);

    [HttpGet("configuration")]
    public IActionResult Configuration() => Ok(new { enabled = options.Value.IsConfigured, publicKey = options.Value.IsConfigured ? options.Value.PublicKey : null });

    [HttpPost("subscriptions")]
    public async Task<IActionResult> Register(SubscriptionRequest request, CancellationToken ct)
    {
        if (!options.Value.IsConfigured) return Conflict(new { error = "Notificações ainda não estão configuradas na Aegis." });
        if (!ValidSubscription(request)) return BadRequest(new { error = "Não foi possível registrar este dispositivo para notificações." });
        var now = clock.GetUtcNow();
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        // Serialize the small single-user device registry, including first registration.
        if (db.Database.IsNpgsql()) await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(5050001)", ct);
        var subscription = await db.PushSubscriptions.SingleOrDefaultAsync(s => s.Endpoint == request.Endpoint, ct);
        if (subscription is not null && subscription.DeviceId != request.DeviceId) return Conflict(new { error = "Este registro de notificações pertence a outro dispositivo." });
        if (subscription is null || subscription.DisabledAt is not null)
        {
            if (await db.PushSubscriptions.CountAsync(s => s.DisabledAt == null && s.DeviceId != request.DeviceId, ct) >= 20)
                return Conflict(new { error = "O limite de dispositivos para notificações foi atingido." });
        }
        if (subscription is null)
        {
            subscription = new PushSubscription(request.Endpoint, request.P256dh, request.Auth, request.DeviceId, request.UserAgent, now);
            db.PushSubscriptions.Add(subscription);
        }
        else subscription.Refresh(request.P256dh, request.Auth, request.UserAgent, now);
        // A new browser endpoint replaces only this device's previous subscription.
        var previous = await db.PushSubscriptions.Where(s => s.DeviceId == request.DeviceId && s.Id != subscription.Id && s.DisabledAt == null).ToListAsync(ct);
        foreach (var s in previous) s.Disable(now);
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return Ok(new { subscriptionId = subscription.Id, token = subscriptionTokens.Protect(JsonSerializer.Serialize(new SubscriptionClaims(subscription.Id, subscription.DeviceId))) });
    }

    [HttpPost("subscriptions/{id:guid}/status")]
    public async Task<IActionResult> Status(Guid id, StatusRequest request, CancellationToken ct)
    {
        if (!ValidateSubscriptionToken(id, request.Token)) return NotFound();
        var subscription = await db.PushSubscriptions.SingleOrDefaultAsync(s => s.Id == id, ct);
        return subscription is null ? NotFound() : Ok(new { active = subscription.DisabledAt is null && options.Value.IsConfigured &&
            (request.Endpoint is null || request.Endpoint == subscription.Endpoint) });
    }

    [HttpPost("subscriptions/{id:guid}/disable")]
    public async Task<IActionResult> Disable(Guid id, TokenRequest request, CancellationToken ct)
    {
        if (!ValidateSubscriptionToken(id, request.Token)) return NotFound();
        var subscription = await db.PushSubscriptions.SingleOrDefaultAsync(s => s.Id == id, ct);
        if (subscription is null) return NotFound();
        subscription.Disable(clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

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
                if (action == "acknowledge") r.Acknowledge(clock.GetUtcNow()); else r.Open(clock.GetUtcNow());
                return Task.FromResult(true);
            }, ct);
            return NoContent();
        }
        catch (ReminderException) { return NotFound(); }
        catch (ArgumentException) { return Conflict(new { error = "Este lembrete ainda não disparou." }); }
    }
    private bool ValidateSubscriptionToken(Guid id, string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096) return false;
        try
        {
            var claims = JsonSerializer.Deserialize<SubscriptionClaims>(subscriptionTokens.Unprotect(token));
            return claims is not null && claims.Id == id && claims.DeviceId != Guid.Empty;
        }
        catch (Exception e) when (e is CryptographicException or JsonException or FormatException) { return false; }
    }
    private static bool ValidSubscription(SubscriptionRequest r)
    {
        // Chromium's standard push service only. This prevents arbitrary server-side requests.
        if (r.DeviceId == Guid.Empty || r.Endpoint is null || r.Endpoint.Length > 2048 ||
            !Uri.TryCreate(r.Endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Host != "fcm.googleapis.com" || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) ||
            r.UserAgent?.Length > 500 || r.P256dh is null || r.Auth is null || r.P256dh.Length > 100 || r.Auth.Length > 40) return false;
        try
        {
            var key = WebEncoders.Base64UrlDecode(r.P256dh);
            if (key.Length != 65 || key[0] != 4 || WebEncoders.Base64UrlDecode(r.Auth).Length != 16) return false;
            using var ec = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = key[1..33], Y = key[33..65] } });
            return true;
        }
        catch (Exception e) when (e is FormatException or CryptographicException or ArgumentException) { return false; }
    }
}
