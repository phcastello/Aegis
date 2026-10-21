using Aegis.Domain.Entities;

namespace Aegis.Application.Reminders;

public interface IReminderStore
{
    Task<bool> HasNotificationNodeAsync(CancellationToken ct);
    Task AddAsync(Reminder reminder, CancellationToken ct);
    Task<IReadOnlyList<Reminder>> ListAsync(DateTimeOffset? from, DateTimeOffset? to, int limit, CancellationToken ct);
    Task<T> LockedAsync<T>(Guid id, Func<Reminder, Task<T>> action, CancellationToken ct);
    Task RememberAsync(Guid conversationId, IReadOnlyList<Reminder> reminders, string tool, DateTimeOffset now, CancellationToken ct);
    Task<string?> GetContextAsync(Guid conversationId, DateTimeOffset now, CancellationToken ct);
    Task<bool> WasObservedAsync(Guid conversationId, Guid reminderId, DateTimeOffset now, CancellationToken ct);
}

// Validation only: no new browser subscriptions, sends or interaction tokens are issued.
public interface ILegacyReminderInteractionTokens
{
    bool Validate(string token, Guid reminderId, string action);
}
