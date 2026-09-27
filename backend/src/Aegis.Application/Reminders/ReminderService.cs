using System.Globalization;
using System.Text.RegularExpressions;
using Aegis.Application.Observability;
using Aegis.Application.Runtime;
using Aegis.Domain.Entities;

namespace Aegis.Application.Reminders;

public sealed class ReminderService(IReminderStore store, IWebPushClient push, TimeProvider clock, AegisMetrics metrics)
{
    public Task<string?> GetContextAsync(Guid conversationId, CancellationToken ct) => store.GetContextAsync(conversationId, clock.GetUtcNow(), ct);
    public async Task<Reminder> CreateAsync(Guid conversationId, string text, string dueAt, CancellationToken ct, string? timeZoneId = null)
    {
        var due = ParseInstant(dueAt);
        var zone = ValidateTimeZone(timeZoneId ?? RuntimeContextProvider.ReferenceTimeZoneId);
        if (!push.IsConfigured)
            throw new ReminderException("notifications_not_configured", "O envio de notificações ainda não está configurado no servidor da Aegis. Conceder permissão no navegador não resolve essa configuração; o lembrete ainda não foi criado.");
        if (!await store.HasActiveSubscriptionAsync(ct))
            throw new ReminderException("notifications_unavailable", "Ative notificações na Aegis para eu conseguir avisar com a aplicação fechada. Depois, peça o lembrete novamente; ele ainda não foi criado.");
        var reminder = new Reminder(text, due, zone, conversationId, clock.GetUtcNow());
        await store.AddAsync(reminder, ct);
        metrics.RemindersCreated.Add(1);
        return reminder;
    }
    public async Task<IReadOnlyList<Reminder>> ListAsync(Guid conversationId, string? from, string? to, int limit, CancellationToken ct)
    {
        if (limit is < 1 or > 50) throw new ArgumentException("O limite deve estar entre 1 e 50.");
        var start = from is null ? (DateTimeOffset?)null : ParseInstant(from);
        var end = to is null ? (DateTimeOffset?)null : ParseInstant(to);
        if (start is not null && end is not null && start >= end) throw new ArgumentException("Intervalo inválido.");
        var reminders = await store.ListAsync(start, end, limit + 1, ct);
        await store.RememberAsync(conversationId, reminders.Take(limit).ToArray(), "reminder_list", clock.GetUtcNow(), ct);
        return reminders;
    }
    public async Task<Reminder> ChangeAsync(Guid conversationId, Guid id, string? text, string? dueAt, bool cancel, CancellationToken ct, string? timeZoneId = null)
    {
        if (!await store.WasObservedAsync(conversationId, id, clock.GetUtcNow(), ct))
            throw new ReminderException("invalid_tool_arguments", "Referência não observada ou expirada. Consulte reminder_list; se houver múltiplos alvos possíveis, pergunte qual deles.");
        if (timeZoneId is not null && dueAt is null) throw new ArgumentException("Para alterar o timezone, informe também dueAt.");
        var zone = timeZoneId is null ? null : ValidateTimeZone(timeZoneId);
        var due = dueAt is null ? (DateTimeOffset?)null : ParseInstant(dueAt);
        var changed = true;
        var result = await store.LockedAsync(id, reminder =>
        {
            if (cancel) { changed = reminder.Status != ReminderStatus.Cancelled; reminder.Cancel(clock.GetUtcNow()); }
            else reminder.Update(text, due, clock.GetUtcNow(), zone);
            return Task.FromResult(reminder);
        }, ct);
        if (changed) { if (cancel) metrics.RemindersCancelled.Add(1); else metrics.RemindersUpdated.Add(1); }
        await store.RememberAsync(conversationId, [result], cancel ? "reminder_cancel" : "reminder_update", clock.GetUtcNow(), ct);
        return result;
    }
    private static string ValidateTimeZone(string zone)
    {
        if (string.IsNullOrWhiteSpace(zone) || zone.Length > 100) throw new ArgumentException("Timezone inválido.");
        try { return TimeZoneInfo.FindSystemTimeZoneById(zone).Id; }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { throw new ArgumentException("Timezone desconhecido."); }
    }
    public static DateTimeOffset ParseInstant(string value)
    {
        if (!Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant) ||
            !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant))
            throw new ArgumentException("Use RFC3339 com offset explícito, por exemplo 2026-10-01T18:00:00-03:00.");
        return instant.ToUniversalTime();
    }
    public static object View(Reminder r) => new { reminderId = r.Id, text = r.Text, dueAt = r.DueAtUtc, timeZoneId = r.TimeZoneId, status = r.Status.ToString() };
}
public sealed class ReminderException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
