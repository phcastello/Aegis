using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aegis.Application.Common;
using Aegis.Domain.Entities;

namespace Aegis.Application.Calendar;

public sealed class CalendarToolContextService(IAegisDbContext dbContext)
{
    public const string Scope = "calendar";
    public const string EntryType = "calendar_event";
    private const string CalendarEntryType = "calendar_reference";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task RememberAsync(Guid conversationId, IEnumerable<CalendarEventData> events, string sourceTool,
        CancellationToken cancellationToken = default)
    {
        foreach (var item in events.DistinctBy(item => (item.CalendarId, item.EventId)))
            await RememberEntryAsync(conversationId, EntryType, Key(item.CalendarId, item.EventId), item, sourceTool, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RememberCalendarsAsync(Guid conversationId, IEnumerable<CalendarData> calendars, string sourceTool,
        CancellationToken cancellationToken = default)
    {
        foreach (var item in calendars.DistinctBy(item => item.CalendarId))
            await RememberEntryAsync(conversationId, CalendarEntryType, Key(item.CalendarId), item, sourceTool, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> WasObservedAsync(Guid conversationId, string calendarId, string eventId, CancellationToken cancellationToken = default) =>
        (await RecentEventsAsync(conversationId, cancellationToken)).Any(item => item.CalendarId == calendarId && item.EventId == eventId);

    public async Task RequireCalendarAsync(Guid conversationId, string calendarId, CancellationToken cancellationToken = default)
    {
        if (calendarId == "primary") return;
        var calendars = await dbContext.GetActiveToolContextEntriesAsync(conversationId, Scope, CalendarEntryType, Key(calendarId), cancellationToken);
        if (calendars.Count > 0 || (await RecentEventsAsync(conversationId, cancellationToken)).Any(item => item.CalendarId == calendarId)) return;
        throw new CalendarException("invalid_calendar_tool_arguments", "calendarId não observado ou expirado. Consulte calendar_list_calendars e escolha o destino solicitado pelo usuário.");
    }

    public async Task<CalendarEventData> ResolveEventAsync(Guid conversationId, string eventId, string? calendarId,
        CancellationToken cancellationToken = default)
    {
        var recent = await RecentEventsAsync(conversationId, cancellationToken);
        var matches = recent.Where(item => item.EventId == eventId && (calendarId is null || item.CalendarId == calendarId)).ToList();
        if (matches.Count == 1) return matches[0];
        var valid = recent.Take(10).Select(item => new { item.CalendarId, item.EventId, item.CalendarName, item.Summary, item.Type });
        throw new CalendarException("invalid_calendar_tool_arguments", (matches.Count > 1
            ? "eventId existe em mais de um calendário. Use também o calendarId observado. "
            : "Referência não observada ou expirada. Resolva o evento por calendar_list_events. ") +
            "Referências recentes: " + JsonSerializer.Serialize(valid, JsonOptions));
    }

    private async Task<List<CalendarEventData>> RecentEventsAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var entries = await dbContext.GetActiveToolContextEntriesAsync(conversationId, Scope, EntryType, null, cancellationToken);
        var events = new List<CalendarEventData>();
        foreach (var entry in entries)
        {
            // Previous primary-only context has no calendarId and must be resolved again.
            try
            {
                var item = JsonSerializer.Deserialize<CalendarEventData>(entry.DataJson, JsonOptions);
                if (item is not null && !string.IsNullOrWhiteSpace(item.CalendarId) && !string.IsNullOrWhiteSpace(item.EventId)) events.Add(item);
            }
            catch (JsonException) { }
        }
        return events.DistinctBy(item => (item.CalendarId, item.EventId)).ToList();
    }

    private async Task RememberEntryAsync(Guid conversationId, string entryType, string key, object item, string sourceTool, CancellationToken cancellationToken)
    {
        await dbContext.ReplaceActiveToolContextEntriesAsync(conversationId, Scope, entryType, key, cancellationToken);
        dbContext.AddToolContextEntry(new ToolContextEntry(conversationId, Scope, entryType, key,
            JsonSerializer.Serialize(item, JsonOptions), sourceTool, DateTimeOffset.UtcNow.AddMinutes(30)));
    }

    // JSON tuple prevents ambiguous concatenation; hash fits the existing 200-character key.
    private static string Key(params string[] ids) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ids))));
}
