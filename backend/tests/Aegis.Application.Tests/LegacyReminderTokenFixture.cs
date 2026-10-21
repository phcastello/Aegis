using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Aegis.Application.Tests;

// Reproduce the v1 wire format; production only validates already-issued tokens.
internal static class LegacyReminderTokenFixture
{
    public static string Create(IDataProtectionProvider protection, TimeProvider clock, Guid reminderId, string action,
        Guid? subscriptionId = null) => protection.CreateProtector("Aegis.ReminderInteraction.v1").Protect(JsonSerializer.Serialize(new
        {
            ReminderId = reminderId,
            SubscriptionId = subscriptionId ?? Guid.NewGuid(),
            Action = action,
            ExpiresAt = clock.GetUtcNow().AddDays(30)
        }));
}
