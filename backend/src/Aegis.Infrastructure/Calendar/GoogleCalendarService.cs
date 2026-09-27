using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aegis.Application.Calendar;
using Aegis.Application.Email;
using Aegis.Application.Google;
using Aegis.Application.Runtime;
using Aegis.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aegis.Infrastructure.Calendar;

public sealed class GoogleCalendarService(HttpClient httpClient, IGoogleAccessTokenProvider tokenProvider,
    IEmailConnectionService connectionService, ILogger<GoogleCalendarService> logger, IOptions<GoogleCalendarOptions> options) : ICalendarService
{
    private const string BaseUrl = "https://www.googleapis.com/calendar/v3";
    private const int MaxCalendars = 1000;
    private const int MaxPages = 20;

    public async Task<CalendarConnectionStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var status = await connectionService.GetStatusAsync(cancellationToken);
        var events = status.IsConnected && GoogleScopes.Contains(status.Scopes, GoogleScopes.Calendar);
        var calendars = status.IsConnected && GoogleScopes.Contains(status.Scopes, GoogleScopes.CalendarList);
        return new(status.IsConnected, status.EmailAddress, events && calendars,
            !status.IsConnected ? "calendar_not_connected" : !events ? "calendar_scope_missing" : !calendars ? "calendar_list_scope_missing" : null,
            events, calendars);
    }

    private async Task<string> AccessTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            await tokenProvider.GetAccessTokenAsync(GoogleScopes.Calendar, cancellationToken);
            return await tokenProvider.GetAccessTokenAsync(GoogleScopes.CalendarList, cancellationToken);
        }
        catch (EmailNotConnectedException) { throw new CalendarException("calendar_not_connected", "Conecte a conta Google usando calendar_create_connect_link."); }
        catch (GoogleScopeMissingException e)
        {
            throw new CalendarException(e.Scope == GoogleScopes.CalendarList ? "calendar_list_scope_missing" : "calendar_scope_missing",
                "Autorize os scopes Calendar Events e Calendar List na mesma conta usando calendar_create_connect_link. A autorização Gmail foi preservada.");
        }
    }

    public async Task<CalendarListData> ListCalendarsAsync(CancellationToken cancellationToken = default) =>
        new(await DiscoverCalendarsAsync(await AccessTokenAsync(cancellationToken), cancellationToken));

    private async Task<List<CalendarData>> DiscoverCalendarsAsync(string token, CancellationToken cancellationToken)
    {
        var calendars = new List<CalendarData>();
        string? pageToken = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var parameters = new Dictionary<string, string> { ["maxResults"] = "250", ["showHidden"] = "true", ["showDeleted"] = "false" };
            if (pageToken is not null) parameters["pageToken"] = pageToken;
            var result = await SendAsync(HttpMethod.Get, Url(BaseUrl + "/users/me/calendarList", parameters), token, null, null,
                "calendarList.list", cancellationToken);
            calendars.AddRange((result?["items"] as JsonArray ?? []).OfType<JsonObject>()
                .Where(item => item["deleted"]?.GetValue<bool>() != true).Select(MapCalendar));
            if (calendars.Count > MaxCalendars) break;
            pageToken = result?["nextPageToken"]?.GetValue<string>();
            if (string.IsNullOrEmpty(pageToken)) return calendars.DistinctBy(item => item.CalendarId).ToList();
        }
        throw new CalendarException("calendar_result_limit_exceeded", "A lista de calendários excedeu o limite defensivo de consulta; não foi possível ler todas as agendas.");
    }

    public async Task<CalendarEventList> ListEventsAsync(string? timeMin, string? timeMax, string? query, int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (timeMin is not null) ParseTimestamp(timeMin);
        if (timeMax is not null) ParseTimestamp(timeMax);
        if (timeMin is not null && timeMax is not null && ParseTimestamp(timeMax) <= ParseTimestamp(timeMin))
            throw Invalid("timeMax deve ser posterior a timeMin.");
        limit = Math.Clamp(limit, 1, 50);
        var token = await AccessTokenAsync(cancellationToken);
        var calendars = await DiscoverCalendarsAsync(token, cancellationToken);
        var events = new List<CalendarEventData>();
        var holidays = new List<CalendarEventData>();
        var moreEvents = false;
        var moreHolidays = false;
        foreach (var calendar in calendars.Where(CanReadEvents))
        {
            var result = await ListCalendarEventsAsync(calendar, token, timeMin, timeMax, query, limit, cancellationToken);
            if (calendar.CalendarType == "holiday")
            {
                holidays.AddRange(result.Items);
                moreHolidays |= result.HasMore;
            }
            else
            {
                events.AddRange(result.Items);
                moreEvents |= result.HasMore;
            }
        }
        // Holidays share the defensive output budget but never displace the next appointment.
        var orderedEvents = Ordered(events);
        var selectedEvents = orderedEvents.Take(limit).ToList();
        var orderedHolidays = Ordered(holidays);
        var selectedHolidays = orderedHolidays.Take(limit - selectedEvents.Count).ToList();
        moreEvents |= orderedEvents.Count > selectedEvents.Count;
        moreHolidays |= orderedHolidays.Count > selectedHolidays.Count;
        return new(selectedEvents, selectedHolidays, moreEvents || moreHolidays, moreEvents, moreHolidays);
    }

    private static List<CalendarEventData> Ordered(IEnumerable<CalendarEventData> items) => items
        .DistinctBy(item => (item.CalendarId, item.EventId)).OrderBy(StartInstant)
        .ThenBy(item => item.CalendarId, StringComparer.Ordinal).ThenBy(item => item.EventId, StringComparer.Ordinal).ToList();

    private async Task<(List<CalendarEventData> Items, bool HasMore)> ListCalendarEventsAsync(CalendarData calendar, string token,
        string? timeMin, string? timeMax, string? query, int limit, CancellationToken cancellationToken)
    {
        var items = new List<CalendarEventData>();
        string? pageToken = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var parameters = new Dictionary<string, string> { ["singleEvents"] = "true", ["orderBy"] = "startTime",
                ["maxResults"] = (limit - items.Count).ToString(CultureInfo.InvariantCulture), ["showDeleted"] = "false",
                ["timeZone"] = RuntimeContextProvider.ReferenceTimeZoneId };
            if (timeMin is not null) parameters["timeMin"] = ParseTimestamp(timeMin).ToString("O");
            if (timeMax is not null) parameters["timeMax"] = ParseTimestamp(timeMax).ToString("O");
            if (!string.IsNullOrWhiteSpace(query)) parameters["q"] = query.Trim();
            if (pageToken is not null) parameters["pageToken"] = pageToken;
            var result = await SendAsync(HttpMethod.Get, Url(EventsUrl(calendar.CalendarId), parameters), token, null, null,
                "events.list", cancellationToken);
            items.AddRange((result?["items"] as JsonArray ?? []).OfType<JsonObject>().Where(IsActive)
                .Select(item => Map(item, calendar, false)).Take(limit - items.Count));
            pageToken = result?["nextPageToken"]?.GetValue<string>();
            if (string.IsNullOrEmpty(pageToken) || items.Count == limit) return (items, !string.IsNullOrEmpty(pageToken));
        }
        throw new CalendarException("calendar_result_limit_exceeded", "A paginação de eventos excedeu o limite defensivo; a consulta não está completa.");
    }

    private static DateTimeOffset StartInstant(CalendarEventData item)
    {
        if (!item.AllDay) return ParseTimestamp(item.Start ?? throw Invalid("Evento sem início."));
        var date = ParseDate(item.Start ?? throw Invalid("Evento sem data.")).ToDateTime(TimeOnly.MinValue);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(item.TimeZone ?? RuntimeContextProvider.ReferenceTimeZoneId);
        return new DateTimeOffset(date, zone.GetUtcOffset(date));
    }

    public async Task<CalendarEventData> GetEventAsync(string calendarId, string eventId, CancellationToken cancellationToken = default)
    {
        var token = await AccessTokenAsync(cancellationToken);
        var calendar = await GetCalendarAsync(calendarId, token, cancellationToken);
        return Map(await GetResourceAsync(calendarId, eventId, token, cancellationToken), calendar, true);
    }

    public async Task<CalendarActionPayload> PrepareAsync(string actionType, string calendarId, string eventId, CalendarEventChanges changes,
        CancellationToken cancellationToken = default)
    {
        ValidateReminderArguments(changes, actionType == CalendarActionTypes.Create);
        var token = await AccessTokenAsync(cancellationToken);
        var calendar = await GetCalendarAsync(calendarId, token, cancellationToken);
        RequireWrite(calendar);
        var status = await GetStatusAsync(cancellationToken);
        var current = actionType == CalendarActionTypes.Create ? null : await GetResourceAsync(calendarId, eventId, token, cancellationToken);
        RequireWrite(calendar, current);
        if (current is not null && current["recurrence"] is JsonArray { Count: > 0 })
            throw Invalid("Selecione uma ocorrência específica da série pela listagem. Esta versão não altera séries recorrentes completas.");
        var fields = actionType == CalendarActionTypes.Delete ? new JsonObject() : BuildFields(changes, current);
        var expected = current?.DeepClone() as JsonObject ?? new JsonObject();
        ApplyFields(expected, fields);
        var eventData = Map(expected, calendar, true);
        var verb = actionType switch { CalendarActionTypes.Create => "Criar", CalendarActionTypes.Update => "Alterar", _ => "Excluir" };
        var summary = $"{verb} ‘{eventData.Summary ?? "Sem título"}’ em ‘{calendar.Name}’: {eventData.Start} até {eventData.End}" +
            (eventData.AllDay ? " (dia inteiro)" : $" ({eventData.TimeZone ?? RuntimeContextProvider.ReferenceTimeZoneId})");
        if (changes.Location is not null) summary += $"; local: {changes.Location}";
        if (changes.Description is not null) summary += string.IsNullOrEmpty(changes.Description)
            ? "; anotação removida" : $"; anotação: {changes.Description}";
        var reminderSummary = fields["reminders"] is JsonObject reminders
            ? ReminderSummary(reminders, changes.ReminderMode ?? (current is null ? "aegis_default" : "keep"), eventData.AllDay) : "";
        var summaryBudget = 500 - reminderSummary.Length;
        if (summary.Length > summaryBudget) summary = summary[..(summaryBudget - 3)] + "...";
        summary += reminderSummary;
        var checksDates = actionType == CalendarActionTypes.Create || actionType == CalendarActionTypes.Update &&
            (changes.Start is not null || changes.End is not null || changes.AllDay is not null || changes.TimeZone is not null);
        var holidayCheck = checksDates
            ? await FindHolidayWarningsAsync(eventData, token, cancellationToken)
            : (Warnings: new List<CalendarHolidayWarning>(), HasMore: false);
        return new(status.EmailAddress ?? throw new CalendarException("calendar_not_connected", "Conta Google não confirmada."),
            current?["etag"]?.GetValue<string>(), fields, summary, holidayCheck.Warnings, holidayCheck.HasMore,
            actionType == CalendarActionTypes.Delete ? null : changes.ReminderMode ?? (current is null ? "aegis_default" : "keep"));
    }

    public async Task ExecuteAsync(string actionType, string calendarId, string eventId, CalendarActionPayload payload,
        CancellationToken cancellationToken = default)
    {
        var sent = false;
        try
        {
            var token = await AccessTokenAsync(cancellationToken);
            await RequireSameAccountAsync(payload, cancellationToken);
            var calendar = await GetCalendarAsync(calendarId, token, cancellationToken);
            RequireWrite(calendar);
            JsonObject resource;
            if (actionType == CalendarActionTypes.Create)
            {
                resource = (JsonObject)payload.Fields.DeepClone();
                resource["id"] = eventId;
            }
            else
            {
                JsonObject current;
                try { current = await GetResourceAsync(calendarId, eventId, token, cancellationToken); }
                catch (CalendarException e) when (e.Code == "calendar_event_not_found" && actionType == CalendarActionTypes.Delete) { return; }
                RequireWrite(calendar, current);
                if (actionType == CalendarActionTypes.Update && Matches(current, payload.Fields)) return;
                if (payload.ExpectedETag is not null && current["etag"]?.GetValue<string>() != payload.ExpectedETag)
                    throw new CalendarException("calendar_event_changed", "O evento mudou desde o preparo. Consulte novamente e prepare outra ação.");
                resource = (JsonObject)current.DeepClone();
                ApplyFields(resource, payload.Fields);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var method = actionType switch { CalendarActionTypes.Create => HttpMethod.Post, CalendarActionTypes.Update => HttpMethod.Put, _ => HttpMethod.Delete };
            sent = true;
            await SendAsync(method, (actionType == CalendarActionTypes.Create ? EventsUrl(calendarId) : EventUrl(calendarId, eventId)) +
                (actionType == CalendarActionTypes.Update ? "?conferenceDataVersion=1&supportsAttachments=true&sendUpdates=none" : "?sendUpdates=none"), token,
                actionType == CalendarActionTypes.Delete ? null : resource, payload.ExpectedETag,
                actionType == CalendarActionTypes.Create ? "events.insert" : actionType == CalendarActionTypes.Update ? "events.update" : "events.delete", cancellationToken);
        }
        catch (OperationCanceledException e) when (cancellationToken.IsCancellationRequested)
        { throw new CalendarMutationCancelledException(sent, e, cancellationToken); }
        catch (Exception e)
        {
            throw new CalendarMutationException((e as CalendarException)?.Code ?? "calendar_temporarily_unavailable",
                (e as CalendarException)?.Message ?? "Falha temporária na operação Calendar.", sent);
        }
    }

    public async Task<bool> VerifyAsync(string actionType, string calendarId, string eventId, CalendarActionPayload payload,
        CancellationToken cancellationToken = default)
    {
        var token = await AccessTokenAsync(cancellationToken);
        await RequireSameAccountAsync(payload, cancellationToken);
        try
        {
            var resource = await GetResourceAsync(calendarId, eventId, token, cancellationToken);
            return actionType != CalendarActionTypes.Delete && Matches(resource, payload.Fields);
        }
        catch (CalendarException e) when (e.Code == "calendar_event_not_found") { return actionType == CalendarActionTypes.Delete; }
    }

    private async Task RequireSameAccountAsync(CalendarActionPayload payload, CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(cancellationToken);
        if (!string.Equals(status.EmailAddress, payload.AccountEmail, StringComparison.OrdinalIgnoreCase))
            throw new CalendarException("calendar_account_changed", "A conta Google mudou desde o preparo. Prepare a ação novamente.");
    }

    private async Task<CalendarData> GetCalendarAsync(string calendarId, string token, CancellationToken cancellationToken) =>
        MapCalendar(await SendAsync(HttpMethod.Get, BaseUrl + "/users/me/calendarList/" + Uri.EscapeDataString(calendarId), token,
            null, null, "calendarList.get", cancellationToken) ?? throw new CalendarException("calendar_not_found", "Calendário não encontrado."));

    private static bool CanReadEvents(CalendarData calendar) => calendar.AccessRole is "owner" or "writer" or "reader" or "writerWithoutPrivateAccess";
    private static void RequireWrite(CalendarData calendar, JsonObject? current = null)
    {
        if (calendar.AccessRole is "owner" or "writer" || calendar.AccessRole == "writerWithoutPrivateAccess" && current?["visibility"]?.ToString() != "private") return;
        throw new CalendarException("calendar_write_access_denied", "Você não possui permissão de escrita neste calendário/evento. Escolha um calendário com acesso de escrita.");
    }

    private async Task<JsonObject> GetResourceAsync(string calendarId, string eventId, string token, CancellationToken cancellationToken)
    {
        var item = await SendAsync(HttpMethod.Get, EventUrl(calendarId, eventId), token, null, null, "events.get", cancellationToken);
        if (item is null || !IsActive(item)) throw new CalendarException("calendar_event_not_found", "Evento não encontrado no calendário informado.");
        return item;
    }

    private static string EventsUrl(string calendarId) => BaseUrl + "/calendars/" + Uri.EscapeDataString(calendarId) + "/events";
    private static string EventUrl(string calendarId, string eventId) => EventsUrl(calendarId) + "/" + Uri.EscapeDataString(eventId);
    private static string Url(string path, Dictionary<string, string> parameters) => path + "?" + string.Join('&', parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
    private static bool IsActive(JsonObject item) => item["status"]?.GetValue<string>() != "cancelled";
    private static CalendarData MapCalendar(JsonObject item)
    {
        var id = item["id"]?.GetValue<string>() ?? throw Invalid("Calendário sem ID.");
        const string holidaySuffix = "#holiday@group.v.calendar.google.com";
        var type = id.Length > holidaySuffix.Length && id.EndsWith(holidaySuffix, StringComparison.OrdinalIgnoreCase) ? "holiday" : "calendar";
        return new(id, Compact(item["summaryOverride"]?.GetValue<string>() ?? item["summary"]?.GetValue<string>() ?? "Sem nome", 256),
            item["primary"]?.GetValue<bool>() ?? false, item["accessRole"]?.GetValue<string>() ?? "none", item["timeZone"]?.GetValue<string>(), type,
            item["defaultReminders"] is JsonArray defaults ? ReadOverrides(defaults) : null);
    }

    private async Task<(List<CalendarHolidayWarning> Warnings, bool HasMore)> FindHolidayWarningsAsync(CalendarEventData target, string token,
        CancellationToken cancellationToken)
    {
        var (firstDate, lastDate) = EventDates(target);
        if (lastDate == DateOnly.MaxValue) throw Invalid("A data final excede o intervalo de consulta de feriados.");
        var calendars = await DiscoverCalendarsAsync(token, cancellationToken);
        var warnings = new List<CalendarHolidayWarning>();
        var hasMore = false;
        const int warningLimit = 50;
        foreach (var calendar in calendars.Where(c => c.CalendarType == "holiday" && CanReadEvents(c)))
        {
            // Holiday dates are calendar-local dates, rather than occupied time in the user's agenda.
            var zone = TimeZoneInfo.FindSystemTimeZoneById(calendar.TimeZone ?? RuntimeContextProvider.ReferenceTimeZoneId);
            string Midnight(DateOnly date)
            {
                var local = date.ToDateTime(TimeOnly.MinValue);
                return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToString("O");
            }
            var result = await ListCalendarEventsAsync(calendar, token, Midnight(firstDate), Midnight(lastDate.AddDays(1)), null,
                warningLimit, cancellationToken);
            hasMore |= result.HasMore;
            foreach (var item in result.Items)
            {
                var (holidayStart, holidayEnd) = EventDates(item);
                var overlapStart = holidayStart > firstDate ? holidayStart : firstDate;
                var overlapEnd = holidayEnd < lastDate ? holidayEnd : lastDate;
                if (overlapStart > overlapEnd) continue;
                warnings.Add(new(item.Summary ?? "Sem título", overlapStart.ToString("yyyy-MM-dd"), item.CalendarId, item.CalendarName,
                    overlapEnd > overlapStart ? overlapEnd.ToString("yyyy-MM-dd") : null));
            }
        }
        var ordered = warnings.Distinct().OrderBy(item => item.Date, StringComparer.Ordinal)
            .ThenBy(item => item.CalendarId, StringComparer.Ordinal).ThenBy(item => item.Name, StringComparer.Ordinal).ToList();
        return (ordered.Take(warningLimit).ToList(), hasMore || ordered.Count > warningLimit);
    }

    private static (DateOnly First, DateOnly Last) EventDates(CalendarEventData item)
    {
        if (item.AllDay) return (ParseDate(item.Start ?? throw Invalid("Evento sem data.")), ParseDate(item.End ?? throw Invalid("Evento sem fim.")));
        var zone = TimeZoneInfo.FindSystemTimeZoneById(item.TimeZone ?? RuntimeContextProvider.ReferenceTimeZoneId);
        var start = TimeZoneInfo.ConvertTime(ParseTimestamp(item.Start ?? throw Invalid("Evento sem início.")), zone);
        var end = TimeZoneInfo.ConvertTime(ParseTimestamp(item.End ?? throw Invalid("Evento sem fim.")).AddTicks(-1), zone);
        // A timed event ending exactly at midnight does not occupy the following day.
        return (DateOnly.FromDateTime(start.DateTime), DateOnly.FromDateTime(end.DateTime));
    }

    private static string Compact(string value, int max) => value.Length > max ? value[..max] + "…" : value;

    private async Task<JsonObject?> SendAsync(HttpMethod method, string url, string token, JsonObject? body, string? etag,
        string operation, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new("Bearer", token);
        if (etag is not null && method != HttpMethod.Post) request.Headers.TryAddWithoutValidation("If-Match", etag);
        if (body is not null) request.Content = JsonContent.Create(body);
        HttpResponseMessage response;
        try { response = await httpClient.SendAsync(request, cancellationToken); }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            logger.LogWarning("Google Calendar request interrupted: service={Service} operation={Operation} failure={Failure}",
                "calendar-json.googleapis.com", operation, e.GetType().Name);
            throw;
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var reasons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                    CollectReasons(error.RootElement, reasons);
                }
                catch (JsonException) { }
                logger.LogWarning("Google Calendar API failure: status={Status} reasons={Reasons} service={Service} operation={Operation}",
                    (int)response.StatusCode, string.Join(',', reasons.OrderBy(value => value, StringComparer.Ordinal)), "calendar-json.googleapis.com", operation);
                throw MapError(response.StatusCode, reasons, operation);
            }
            if (method == HttpMethod.Delete || response.StatusCode == HttpStatusCode.NoContent) return null;
            try { return await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken); }
            catch (JsonException)
            {
                logger.LogWarning("Google Calendar invalid response: status={Status} service={Service} operation={Operation}", (int)response.StatusCode, "calendar-json.googleapis.com", operation);
                throw new CalendarException("calendar_temporarily_unavailable", "Google retornou uma resposta inválida. Tente novamente.");
            }
        }
    }

    private static void CollectReasons(JsonElement node, HashSet<string> reasons)
    {
        if (node.ValueKind == JsonValueKind.Object)
            foreach (var property in node.EnumerateObject())
            {
                if (property.Name is "reason" or "status" && property.Value.ValueKind == JsonValueKind.String)
                {
                    var value = property.Value.GetString()!;
                    if (value.Length <= 100 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')) reasons.Add(value);
                }
                else if (property.Name != "metadata") CollectReasons(property.Value, reasons);
            }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray()) CollectReasons(item, reasons);
    }

    private static CalendarException MapError(HttpStatusCode status, HashSet<string> reasons, string operation)
    {
        if (reasons.Overlaps(["SERVICE_DISABLED", "accessNotConfigured"]))
            return new("calendar_api_disabled", "A Google Calendar API está desativada no projeto Google Cloud da conexão. Ative essa API no mesmo projeto e tente novamente.");
        if (reasons.Overlaps(["insufficientPermissions", "ACCESS_TOKEN_SCOPE_INSUFFICIENT"]))
            return new("calendar_scope_missing", "Google recusou um scope Calendar. Autorize novamente a mesma conta com calendar_create_connect_link.");
        if ((int)status >= 500 || status == HttpStatusCode.TooManyRequests || reasons.Overlaps(["rateLimitExceeded", "userRateLimitExceeded", "quotaExceeded", "backendError"]))
            return new("calendar_temporarily_unavailable", "Google Calendar está temporariamente indisponível ou limitou as requisições. Tente novamente mais tarde.");
        return status switch
        {
            HttpStatusCode.NotFound or HttpStatusCode.Gone => operation.StartsWith("calendarList", StringComparison.Ordinal) || operation is "events.list" or "events.insert"
                ? new("calendar_not_found", "Calendário não encontrado ou não está mais acessível.")
                : new("calendar_event_not_found", "Evento não encontrado no calendário informado."),
            HttpStatusCode.Unauthorized => new("calendar_authentication_failed", "A autorização Google foi recusada. Autorize novamente a mesma conta."),
            HttpStatusCode.Forbidden => new("calendar_access_denied", "Google recusou o acesso a esse calendário ou evento. Verifique as permissões."),
            HttpStatusCode.PreconditionFailed => new("calendar_event_changed", "O evento foi alterado por outra pessoa. Consulte novamente e prepare outra ação."),
            HttpStatusCode.Conflict => new("calendar_event_conflict", "O ID já existe. Verifique o evento antes de tentar novamente."),
            HttpStatusCode.BadRequest => new("invalid_calendar_tool_arguments", "Google recusou os parâmetros do evento."),
            _ => new("calendar_temporarily_unavailable", "Google Calendar está temporariamente indisponível.")
        };
    }

    private JsonObject BuildFields(CalendarEventChanges changes, JsonObject? current)
    {
        var creating = current is null;
        if (creating && (string.IsNullOrWhiteSpace(changes.Summary) || changes.Start is null || changes.End is null || changes.AllDay is null))
            throw Invalid("Criação exige título, início e fim concretos e allDay explícito. Preencha escolhas explicitamente delegadas ou esclareça informações essenciais ausentes.");
        if (changes.Summary is not null && (string.IsNullOrWhiteSpace(changes.Summary) || changes.Summary.Length > 1024)) throw Invalid("Título inválido.");
        if (changes.Description?.Length > 8000 || changes.Location?.Length > 1024) throw Invalid("Descrição ou local excede o limite.");
        var fields = new JsonObject();
        if (changes.Summary is not null) fields["summary"] = changes.Summary;
        if (changes.Description is not null) fields["description"] = changes.Description;
        if (changes.Location is not null) fields["location"] = changes.Location;
        var currentAllDay = current?["start"]?["date"] is not null;
        var allDay = changes.AllDay ?? currentAllDay;
        var changesTiming = creating || changes.Start is not null || changes.End is not null || changes.TimeZone is not null || changes.AllDay is not null;
        if (changesTiming)
        {
            if (allDay != currentAllDay && !creating && (changes.Start is null || changes.End is null))
                throw Invalid("Trocar entre horário e dia inteiro exige início e fim.");
            if (allDay)
            {
                if (changes.TimeZone is not null) throw Invalid("Dia inteiro usa datas, sem timezone.");
                var start = changes.Start is null ? ParseDate(current!["start"]!["date"]!.GetValue<string>()) : ParseDate(changes.Start);
                var end = changes.End is null ? ParseDate(current!["end"]!["date"]!.GetValue<string>()).AddDays(-1) : ParseDate(changes.End);
                if (end < start || end == DateOnly.MaxValue) throw Invalid("O fim inclusivo deve ser igual ou posterior ao início.");
                fields["start"] = new JsonObject { ["date"] = start.ToString("yyyy-MM-dd") };
                fields["end"] = new JsonObject { ["date"] = end.AddDays(1).ToString("yyyy-MM-dd") };
            }
            else
            {
                var start = changes.Start ?? current?["start"]?["dateTime"]?.GetValue<string>();
                var end = changes.End ?? current?["end"]?["dateTime"]?.GetValue<string>();
                if (start is null || end is null || ParseTimestamp(end) <= ParseTimestamp(start)) throw Invalid("Informe início e fim válidos; o fim deve ser posterior ao início.");
                var zone = changes.TimeZone ?? current?["start"]?["timeZone"]?.GetValue<string>() ?? RuntimeContextProvider.ReferenceTimeZoneId;
                ValidateTimeZone(zone);
                fields["start"] = new JsonObject { ["dateTime"] = ParseTimestamp(start).ToString("O"), ["timeZone"] = zone };
                fields["end"] = new JsonObject { ["dateTime"] = ParseTimestamp(end).ToString("O"), ["timeZone"] = zone };
            }
        }
        var reminders = BuildReminders(changes, creating, allDay);
        if (reminders is not null) fields["reminders"] = reminders;
        if (fields.Count == 0) throw Invalid("Informe pelo menos um campo para alterar.");
        return fields;
    }

    private static void ValidateReminderArguments(CalendarEventChanges changes, bool creating)
    {
        var mode = changes.ReminderMode ?? (creating ? "aegis_default" : "keep");
        if (mode is not ("keep" or "aegis_default" or "calendar_default" or "custom" or "none") || creating && mode == "keep")
            throw Invalid("reminderMode inválido. Criação usa aegis_default quando omitido; keep aplica-se a eventos existentes.");
        if (mode != "custom" && changes.Reminders is not null)
            throw Invalid("reminders só pode ser informado com reminderMode=custom.");
        if (mode == "custom")
        {
            if (changes.Reminders is null) throw Invalid("reminderMode=custom exige a lista completa reminders (até cinco itens).");
            if (changes.Reminders.Count > 5 || changes.Reminders.Any(item => item is null || item.Method is not ("popup" or "email") || item.Minutes is < 0 or > 40320))
                throw Invalid("Use no máximo cinco reminders, com method popup/email e minutes inteiro entre 0 e 40320.");
            if (changes.Reminders.DistinctBy(item => (item.Method, item.Minutes)).Count() != changes.Reminders.Count)
                throw Invalid("Não repita o mesmo method + minutes em reminders.");
        }
    }

    private JsonObject? BuildReminders(CalendarEventChanges changes, bool creating, bool allDay)
    {
        var mode = changes.ReminderMode ?? (creating ? "aegis_default" : "keep");
        if (mode == "keep") return null;
        if (mode == "calendar_default") return new JsonObject { ["useDefault"] = true };
        IReadOnlyList<CalendarReminderData> reminders = mode switch
        {
            "none" => [],
            "custom" => changes.Reminders!,
            _ => (allDay ? options.Value.AllDayDefaultReminders : options.Value.TimedEventDefaultReminders)
                .Select(minutes => new CalendarReminderData("popup", minutes)).ToList()
        };
        return new JsonObject { ["useDefault"] = false, ["overrides"] = JsonSerializer.SerializeToNode(reminders, new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
    }

    private static IReadOnlyList<CalendarReminderData> ReadOverrides(JsonArray? values) => (values ?? []).OfType<JsonObject>()
        .Select(item => new CalendarReminderData(item["method"]?.GetValue<string>() ?? "", item["minutes"]?.GetValue<int>() ?? 0)).ToList();

    private static CalendarRemindersData? ReadReminders(JsonNode? value)
    {
        if (value is not JsonObject reminders) return null;
        var useDefault = reminders["useDefault"]?.GetValue<bool>() ?? false;
        return new(useDefault, useDefault ? null : ReadOverrides(reminders["overrides"] as JsonArray));
    }

    private static string ReminderSummary(JsonObject reminders, string mode, bool allDay)
    {
        if (mode == "aegis_default") return $"; lembretes: padrão Aegis ({(allDay ? "dia inteiro" : "com horário")})";
        if (reminders["useDefault"]?.GetValue<bool>() == true) return "; lembretes: padrão do calendário";
        var values = ReadOverrides(reminders["overrides"] as JsonArray);
        if (values.Count == 0) return "; sem lembretes";
        string When(int minutes)
        {
            if (minutes == 0) return "na hora";
            var parts = new List<string>();
            if (minutes / 1440 > 0) parts.Add($"{minutes / 1440}d");
            if (minutes % 1440 / 60 > 0) parts.Add($"{minutes % 1440 / 60}h");
            if (minutes % 60 > 0) parts.Add($"{minutes % 60}min");
            return string.Join(" ", parts) + " antes";
        }
        return "; lembretes: " + string.Join(", ", values.Select(item => When(item.Minutes) + (item.Method == "email" ? " por email" : "")));
    }

    private static void ValidateTimeZone(string zone)
    {
        try
        {
            if (!zone.Contains('/') && zone != "UTC") throw Invalid("Use timezone IANA, por exemplo America/Sao_Paulo.");
            TimeZoneInfo.FindSystemTimeZoneById(zone);
        }
        catch (TimeZoneNotFoundException) { throw Invalid("Timezone desconhecido."); }
        catch (InvalidTimeZoneException) { throw Invalid("Timezone inválido."); }
    }

    private static DateOnly ParseDate(string value) => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
        ? date : throw Invalid("Dia inteiro exige datas YYYY-MM-DD.");

    private static DateTimeOffset ParseTimestamp(string value)
    {
        // Explicit offset is mandatory: never interpret an offset-free timestamp using the server's local timezone.
        var offset = value.Length >= 6 && (value[^6] is '+' or '-') && value[^3] == ':';
        if (!value.Contains('T') || (!value.EndsWith('Z') && !offset) ||
            !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw Invalid("Use RFC3339 com offset explícito, por exemplo 2026-10-10T14:00:00-03:00.");
        return date;
    }

    private static CalendarException Invalid(string message) => new("invalid_calendar_tool_arguments", message);

    private static void ApplyFields(JsonObject target, JsonObject fields)
    {
        foreach (var field in fields) target[field.Key] = field.Value?.DeepClone();
    }

    private static bool Matches(JsonObject item, JsonObject fields) => fields.All(field =>
    {
        if (field.Key == "reminders")
        {
            var expected = ReadReminders(field.Value);
            var actual = ReadReminders(item[field.Key]);
            if (expected is null || actual is null || expected.UseDefault != actual.UseDefault) return false;
            return expected.UseDefault || (expected.Overrides ?? []).OrderBy(value => value.Method, StringComparer.Ordinal).ThenBy(value => value.Minutes)
                .SequenceEqual((actual.Overrides ?? []).OrderBy(value => value.Method, StringComparer.Ordinal).ThenBy(value => value.Minutes));
        }
        if (field.Key is "start" or "end")
        {
            var expected = field.Value!;
            var actual = item[field.Key];
            if (expected["date"] is not null) return expected["date"]?.ToString() == actual?["date"]?.ToString();
            return DateTimeOffset.TryParse(expected["dateTime"]?.ToString(), out var a) &&
                DateTimeOffset.TryParse(actual?["dateTime"]?.ToString(), out var b) && a == b &&
                expected["timeZone"]?.ToString() == actual?["timeZone"]?.ToString();
        }
        return (field.Value?.ToString() ?? "") == (item[field.Key]?.ToString() ?? "");
    });

    private static CalendarEventData Map(JsonObject item, CalendarData calendar, bool details)
    {
        var allDay = item["start"]?["date"] is not null;
        var end = allDay ? item["end"]?["date"]?.GetValue<string>() : item["end"]?["dateTime"]?.GetValue<string>();
        if (allDay && end is not null) end = ParseDate(end).AddDays(-1).ToString("yyyy-MM-dd");
        string? Text(string key, int max) { var value = item[key]?.GetValue<string>(); return value?.Length > max ? value[..max] + "…" : value; }
        return new(item["id"]?.GetValue<string>() ?? "", calendar.CalendarId, calendar.Name, Text("summary", 1024),
            allDay ? item["start"]?["date"]?.GetValue<string>() : item["start"]?["dateTime"]?.GetValue<string>(), end, allDay,
            item["start"]?["timeZone"]?.GetValue<string>() ?? calendar.TimeZone ?? RuntimeContextProvider.ReferenceTimeZoneId, Text("location", 1024), details ? Text("description", 8000) : null,
            item["status"]?.GetValue<string>(), item["htmlLink"]?.GetValue<string>(), calendar.CalendarType == "holiday" ? "holiday" : "event",
            ReadReminders(item["reminders"]), details && item["description"]?.GetValue<string>().Length > 8000);
    }
}
