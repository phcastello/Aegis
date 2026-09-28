using System.Globalization;
using System.Text.Json.Nodes;
using Aegis.Application.Calendar;

namespace Aegis.Infrastructure.Calendar;

internal static class CalendarRecurrenceRules
{
    private static readonly string[] Weekdays = ["monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday"];
    private static readonly string[] RfcWeekdays = ["MO", "TU", "WE", "TH", "FR", "SA", "SU"];
    private static readonly string[] PortugueseWeekdays = ["segunda", "terça", "quarta", "quinta", "sexta", "sábado", "domingo"];

    internal static (CalendarRecurrenceData? Normalized, string? Rule) Build(CalendarRecurrenceData? value, JsonObject start, JsonObject end)
    {
        if (value is null) return (null, null);
        if (value.Frequency is not ("none" or "daily" or "weekly" or "monthly" or "yearly")) throw Invalid("Frequência de recorrência inválida.");
        if (value.Frequency == "none")
        {
            if (value.Interval is not null || value.DaysOfWeek is not null || value.Count is not null || value.Until is not null)
                throw Invalid("frequency=none não aceita outros parâmetros de recorrência.");
            return (null, null);
        }
        var interval = value.Interval ?? 1;
        if (interval <= 0) throw Invalid("interval deve ser positivo.");
        if (value.Count is <= 0) throw Invalid("count deve ser positivo.");
        if (value.Count is not null && value.Until is not null) throw Invalid("count e until são mutuamente exclusivos.");
        if (value.DaysOfWeek is not null && value.Frequency != "weekly") throw Invalid("daysOfWeek só é válido para recorrência weekly.");

        var allDay = start["date"] is not null;
        var zoneId = start["timeZone"]?.GetValue<string>();
        TimeZoneInfo? zone = null;
        DateOnly first;
        if (allDay) first = DateOnly.ParseExact(start["date"]!.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        else
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId!);
            var instant = DateTimeOffset.Parse(start["dateTime"]!.GetValue<string>(), CultureInfo.InvariantCulture);
            var ending = DateTimeOffset.Parse(end["dateTime"]!.GetValue<string>(), CultureInfo.InvariantCulture);
            if ((instant.Offset != TimeSpan.Zero && zone.GetUtcOffset(instant) != instant.Offset) ||
                (ending.Offset != TimeSpan.Zero && zone.GetUtcOffset(ending) != ending.Offset))
                throw Invalid("Use UTC ou offsets de início e fim correspondentes ao timezone da série.");
            first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
            if (value.Until is not null && TimeZoneInfo.ConvertTime(instant, zone).TimeOfDay > new TimeSpan(23, 59, 59))
                throw Invalid("until não pode incluir com precisão uma ocorrência iniciada após 23:59:59; ajuste os segundos.");
        }
        var firstWeekday = ((int)first.DayOfWeek + 6) % 7;
        IReadOnlyList<string>? days = null;
        if (value.Frequency == "weekly")
        {
            var supplied = value.DaysOfWeek ?? [Weekdays[firstWeekday]];
            if (supplied.Count == 0 || supplied.Count > 7 || supplied.Any(day => day is null || !Weekdays.Contains(day, StringComparer.Ordinal)) ||
                supplied.Distinct(StringComparer.Ordinal).Count() != supplied.Count)
                throw Invalid("daysOfWeek deve conter dias da semana válidos, sem repetição.");
            days = supplied.OrderBy(day => Array.IndexOf(Weekdays, day), Comparer<int>.Default).ToArray();
            if (!days.Contains(Weekdays[firstWeekday], StringComparer.Ordinal))
                throw Invalid("A primeira ocorrência deve cair em um dos daysOfWeek informados.");
        }

        DateOnly? until = null;
        if (value.Until is not null)
        {
            if (!DateOnly.TryParseExact(value.Until, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                throw Invalid("until exige data YYYY-MM-DD.");
            if (parsed < first) throw Invalid("until deve incluir ou suceder a primeira ocorrência.");
            until = parsed;
        }
        var normalized = new CalendarRecurrenceData(value.Frequency, interval, days, value.Count, until?.ToString("yyyy-MM-dd"));
        var parts = new List<string> { "FREQ=" + value.Frequency.ToUpperInvariant() };
        if (interval != 1) parts.Add("INTERVAL=" + interval.ToString(CultureInfo.InvariantCulture));
        if (days is not null) parts.Add("BYDAY=" + string.Join(',', days.Select(day => RfcWeekdays[Array.IndexOf(Weekdays, day)])));
        if (value.Count is not null) parts.Add("COUNT=" + value.Count.Value.ToString(CultureInfo.InvariantCulture));
        if (until is not null)
        {
            if (allDay) parts.Add("UNTIL=" + until.Value.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            else
            {
                if (until == DateOnly.MaxValue) throw Invalid("until excede a data suportada para evento com horário.");
                // The local date is inclusive: any occurrence starting on that date must fit.
                var lastLocalSecond = until.Value.AddDays(1).ToDateTime(TimeOnly.MinValue).AddSeconds(-1);
                if (zone!.IsInvalidTime(lastLocalSecond) || zone.IsAmbiguousTime(lastLocalSecond))
                    throw Invalid("until termina em horário ambíguo neste timezone; escolha outra data.");
                var utc = TimeZoneInfo.ConvertTimeToUtc(lastLocalSecond, zone);
                parts.Add("UNTIL=" + utc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
            }
        }
        return (normalized, "RRULE:" + string.Join(';', parts));
    }

    internal static string Summary(CalendarRecurrenceData recurrence, JsonObject start)
    {
        var interval = recurrence.Interval ?? 1;
        var first = start["date"] is not null
            ? DateOnly.ParseExact(start["date"]!.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(
                DateTimeOffset.Parse(start["dateTime"]!.GetValue<string>(), CultureInfo.InvariantCulture),
                TimeZoneInfo.FindSystemTimeZoneById(start["timeZone"]!.GetValue<string>())).DateTime);
        var cadence = recurrence.Frequency switch
        {
            "daily" => interval == 1 ? "diariamente" : $"a cada {interval} dias",
            "weekly" when interval == 1 => "toda " + string.Join(", ", recurrence.DaysOfWeek!.Select(DayName)),
            "weekly" => $"a cada {interval} semanas na " + string.Join(", ", recurrence.DaysOfWeek!.Select(DayName)),
            "monthly" => (interval == 1 ? "mensalmente" : $"a cada {interval} meses") + $" no dia {first.Day}",
            "yearly" => (interval == 1 ? "anualmente" : $"a cada {interval} anos") + $" em {first:dd/MM}",
            _ => throw Invalid("Frequência de recorrência inválida.")
        };
        if (recurrence.Frequency == "monthly" && first.Day > 28) cadence += "; meses sem esse dia são pulados";
        if (recurrence.Frequency == "yearly" && first.Month == 2 && first.Day == 29) cadence += "; anos sem 29/02 são pulados";
        var ending = recurrence.Count is not null ? $"{recurrence.Count} ocorrências"
            : recurrence.Until is not null ? "até " + DateOnly.ParseExact(recurrence.Until, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("dd/MM/yyyy")
            : "sem término";
        return $"; recorrência: {cadence}; {ending}";
    }

    private static string DayName(string day) => PortugueseWeekdays[Array.IndexOf(Weekdays, day)];

    internal static bool Matches(JsonNode? actual, JsonNode? expected)
    {
        if (actual is not JsonArray { Count: 1 } actualRules || expected is not JsonArray { Count: 1 } expectedRules) return false;
        return Canonical(actualRules[0]?.GetValue<string>()) == Canonical(expectedRules[0]?.GetValue<string>());
    }

    private static string? Canonical(string? rule)
    {
        if (rule is null || !rule.StartsWith("RRULE:", StringComparison.Ordinal)) return null;
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var component in rule[6..].Split(';'))
        {
            var pair = component.Split('=', 2);
            if (pair.Length != 2 || !values.TryAdd(pair[0].ToUpperInvariant(), pair[1].ToUpperInvariant())) return null;
        }
        if (!values.ContainsKey("FREQ")) return null;
        if (values.GetValueOrDefault("INTERVAL") == "1") values.Remove("INTERVAL");
        if (values.TryGetValue("BYDAY", out var days))
            values["BYDAY"] = string.Join(',', days.Split(',').OrderBy(day => Array.IndexOf(RfcWeekdays, day)));
        return string.Join(';', values.Select(value => value.Key + "=" + value.Value));
    }

    private static CalendarException Invalid(string message) => new("invalid_calendar_tool_arguments", message);
}
