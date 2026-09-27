using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Aegis.Application.Calendar;

public interface ICalendarService
{
    Task<CalendarConnectionStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<CalendarListData> ListCalendarsAsync(CancellationToken cancellationToken = default);
    Task<CalendarEventList> ListEventsAsync(string? timeMin, string? timeMax, string? query, int limit = 20, CancellationToken cancellationToken = default);
    Task<CalendarEventData> GetEventAsync(string calendarId, string eventId, CancellationToken cancellationToken = default);
    Task<CalendarActionPayload> PrepareAsync(string actionType, string calendarId, string eventId, CalendarEventChanges changes, CancellationToken cancellationToken = default);
    Task ExecuteAsync(string actionType, string calendarId, string eventId, CalendarActionPayload payload, CancellationToken cancellationToken = default);
    Task<bool> VerifyAsync(string actionType, string calendarId, string eventId, CalendarActionPayload payload, CancellationToken cancellationToken = default);
}

public sealed record CalendarConnectionStatus(bool IsConnected, string? EmailAddress, bool CalendarAuthorized, string? AuthorizationState, bool CalendarEventsAuthorized, bool CalendarListAuthorized);
public sealed record CalendarReminderData([property: JsonRequired] string Method, [property: JsonRequired] int Minutes);
public sealed record CalendarRemindersData(bool UseDefault, IReadOnlyList<CalendarReminderData>? Overrides = null);
public sealed record CalendarData(string CalendarId, string Name, bool Primary, string AccessRole, string? TimeZone = null, string CalendarType = "calendar",
    IReadOnlyList<CalendarReminderData>? DefaultReminders = null);
public sealed record CalendarListData(IReadOnlyList<CalendarData> Calendars);
public sealed record CalendarEventData(string EventId, string CalendarId, string CalendarName, string? Summary, string? Start, string? End, bool AllDay,
    string? TimeZone, string? Location, string? Description, string? Status, string? HtmlLink, string Type = "event", CalendarRemindersData? Reminders = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool DescriptionTruncated = false);
public sealed record CalendarEventList(IReadOnlyList<CalendarEventData> Events, IReadOnlyList<CalendarEventData> Holidays,
    bool HasMore, bool HasMoreEvents, bool HasMoreHolidays);
public sealed record CalendarHolidayWarning(string Name, string Date, string CalendarId, string CalendarName, string? EndDate = null);

// For all-day events Start and End are inclusive dates at the Application boundary.
// Infrastructure converts End to Google's exclusive end.date.
// Description omitted/null preserves the current note; a string replaces it, including "" to clear.
public sealed record CalendarEventChanges(string? Summary = null, string? Start = null, string? End = null,
    bool? AllDay = null, string? TimeZone = null, string? Description = null, string? Location = null,
    string? ReminderMode = null, IReadOnlyList<CalendarReminderData>? Reminders = null);

// Normalized changes and contextual warnings are stored; no model input is needed at confirmation.
public sealed record CalendarActionPayload(string AccountEmail, string? ExpectedETag, JsonObject Fields, string HumanSummary,
    IReadOnlyList<CalendarHolidayWarning>? HolidayWarnings = null, bool HolidayWarningsHasMore = false, string? ReminderMode = null);

public class CalendarException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class CalendarMutationException(string code, string message, bool requestWasSent) : CalendarException(code, message)
{
    public bool RequestWasSent { get; } = requestWasSent;
}

public sealed class CalendarMutationCancelledException(bool requestWasSent, OperationCanceledException inner, CancellationToken token)
    : OperationCanceledException("Calendar request cancelled; external effects may exist.", inner, token)
{
    public bool RequestWasSent { get; } = requestWasSent;
}
