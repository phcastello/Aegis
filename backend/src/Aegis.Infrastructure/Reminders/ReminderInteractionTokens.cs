using System.Security.Cryptography;
using System.Text.Json;
using Aegis.Application.Reminders;
using Microsoft.AspNetCore.DataProtection;

namespace Aegis.Infrastructure.Reminders;

public sealed class ReminderInteractionTokens(IDataProtectionProvider protection, TimeProvider clock) : IReminderInteractionTokens
{
    private readonly IDataProtector protector = protection.CreateProtector("Aegis.ReminderInteraction.v1");
    private sealed record Claims(Guid ReminderId, Guid SubscriptionId, string Action, DateTimeOffset ExpiresAt);
    public string Create(Guid reminderId, Guid subscriptionId, string action) => protector.Protect(JsonSerializer.Serialize(
        new Claims(reminderId, subscriptionId, action, clock.GetUtcNow().AddDays(30))));
    public bool Validate(string token, Guid reminderId, string action)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096) return false;
        try
        {
            var claims = JsonSerializer.Deserialize<Claims>(protector.Unprotect(token));
            return claims is not null && claims.ReminderId == reminderId && claims.SubscriptionId != Guid.Empty && claims.Action == action && claims.ExpiresAt > clock.GetUtcNow();
        }
        catch (Exception e) when (e is CryptographicException or JsonException or FormatException) { return false; }
    }
}
