using Aegis.Domain.Entities;

namespace Aegis.Application.Reminders;

public interface IReminderStore
{
    Task<bool> HasActiveSubscriptionAsync(CancellationToken ct);
    Task AddAsync(Reminder reminder, CancellationToken ct);
    Task<IReadOnlyList<Reminder>> ListAsync(DateTimeOffset? from, DateTimeOffset? to, int limit, CancellationToken ct);
    Task<T> LockedAsync<T>(Guid id, Func<Reminder, Task<T>> action, CancellationToken ct);
    Task RememberAsync(Guid conversationId, IReadOnlyList<Reminder> reminders, string tool, DateTimeOffset now, CancellationToken ct);
    Task<string?> GetContextAsync(Guid conversationId, DateTimeOffset now, CancellationToken ct);
    Task<bool> WasObservedAsync(Guid conversationId, Guid reminderId, DateTimeOffset now, CancellationToken ct);
}

public interface IWebPushClient
{
    bool IsConfigured { get; }
    Task<PushResult> SendAsync(PushSubscription subscription, string payload, CancellationToken ct);
}
public sealed record PushResult(int? HttpStatus, string? FailureReason = null)
{
    public bool Accepted => FailureReason is null;
    public bool PermanentSubscriptionFailure => HttpStatus is 404 or 410;
    public bool Transient => HttpStatus is null or 408 or 429 || HttpStatus >= 500;
}
public interface IReminderInteractionTokens
{
    string Create(Guid reminderId, Guid subscriptionId, string action);
    bool Validate(string token, Guid reminderId, string action);
}
