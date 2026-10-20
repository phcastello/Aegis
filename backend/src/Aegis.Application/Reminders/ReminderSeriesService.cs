using Aegis.Domain.Entities;
namespace Aegis.Application.Reminders;

public sealed record ReminderSeriesPage(IReadOnlyList<ReminderSeries> Series, bool HasMore, int? NextOffset, IReadOnlyDictionary<Guid, DateTimeOffset> NextDueAt);

public interface IReminderSeriesStore
{
    Task AddAsync(ReminderSeries series, CancellationToken ct);
    Task<IReadOnlyList<ReminderSeries>> ListAsync(CancellationToken ct);
    Task<ReminderSeriesPage> PageAsync(int offset, CancellationToken ct);
    Task<ReminderSeries> ChangeAsync(Guid id, int revision, string operation, string? text, ReminderRecurrenceRule? rule, string? policy, CancellationToken ct);
    Task RememberAsync(Guid conversationId, IReadOnlyList<ReminderSeries> series, DateTimeOffset now, CancellationToken ct);
    Task<bool> WasObservedAsync(Guid conversationId, Guid seriesId, DateTimeOffset now, CancellationToken ct);
}
public sealed class ReminderSeriesService(IReminderSeriesStore store, IReminderStore reminders, TimeProvider clock)
{
    public async Task<ReminderSeries> CreateAsync(Guid? conversationId, string text, string timeZoneId, ReminderRecurrenceRule rule, string deliveryPolicy, CancellationToken ct)
    {
        if (!await reminders.HasNotificationNodeAsync(ct)) throw new ReminderException("notifications_unavailable", "Pareie e habilite um dispositivo para receber lembretes.");
        var series = new ReminderSeries(text, timeZoneId, rule, conversationId, clock.GetUtcNow(), deliveryPolicy);
        if (series.Next(clock.GetUtcNow()) is null) throw new ArgumentException("A regra não possui ocorrências futuras.");
        await store.AddAsync(series, ct);
        if (conversationId is { } cid) await store.RememberAsync(cid, [series], clock.GetUtcNow(), ct);
        return series;
    }
    public async Task<IReadOnlyList<ReminderSeries>> ListAsync(Guid? conversationId, CancellationToken ct) => (await PageAsync(conversationId, 0, ct)).Series;
    public async Task<ReminderSeriesPage> PageAsync(Guid? conversationId, int offset, CancellationToken ct)
    {
        var page = await store.PageAsync(offset, ct);
        if (conversationId is { } cid) await store.RememberAsync(cid, page.Series, clock.GetUtcNow(), ct);
        return page;
    }
    public async Task<ReminderSeries> ChangeAsync(Guid? conversationId, Guid id, int revision, string operation, string? text, ReminderRecurrenceRule? rule, string? policy, CancellationToken ct)
    {
        if (conversationId is { } cid && !await store.WasObservedAsync(cid, id, clock.GetUtcNow(), ct))
            throw new ReminderException("invalid_tool_arguments", "Referência de série não observada; consulte reminder_series_list e esclareça alvos ambíguos.");
        var item = await store.ChangeAsync(id, revision, operation, text, rule, policy, ct);
        if (conversationId is { } cid2) await store.RememberAsync(cid2, [item], clock.GetUtcNow(), ct);
        return item;
    }
    public static object View(ReminderSeries s) => View(s, null);
    public static object View(ReminderSeries s, DateTimeOffset? nextDueAt) => new { seriesId = s.Id, s.Text, s.TimeZoneId, rule = s.Rule(), s.Status, s.Revision, s.DeliveryPolicy, s.GeneratedCount, nextDueAt };
    public static object PageView(ReminderSeriesPage page) => new { series = page.Series.Select(s => View(s, page.NextDueAt.TryGetValue(s.Id, out var due) ? due : null)), page.HasMore, page.NextOffset };
}

public sealed record ReminderActionRequest(Guid ActionId, int ExpectedRevision, int Generation, string Action, int SnoozeMinutes = 10);
public sealed record ReminderActionResult(bool Applied, bool Replayed, bool Conflict, object Reminder);
public interface IReminderActionStore { Task<ReminderActionResult> ApplyAsync(Guid reminderId, ReminderActionRequest request, CancellationToken ct); }
public interface IReminderActionTokens
{
    string Create(Reminder reminder, DateTimeOffset expiresAt, Guid recipientNodeId);
    bool Validate(string token, Guid reminderId, int revision, int generation);
    bool TryValidateRecipient(string token, Guid reminderId, int revision, int generation, out Guid recipientNodeId);
}
public sealed class ReminderActionService(IReminderActionStore store)
{
    public Task<ReminderActionResult> ApplyAsync(Guid reminderId, ReminderActionRequest request, CancellationToken ct)
    {
        if (request.ActionId == Guid.Empty || request.ExpectedRevision < 1 || request.Generation < 1 || request.Action is not ("acknowledge" or "snooze") || request.SnoozeMinutes is < 1 or > 1440)
            throw new ArgumentException("Ação de lembrete inválida.");
        return store.ApplyAsync(reminderId, request, ct);
    }
}
