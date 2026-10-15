using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aegis.Domain.Entities;

public sealed record ReminderRecurrenceRule
{
    public string Frequency { get; init; } = "daily";
    public int Interval { get; init; } = 1;
    public string LocalTime { get; init; } = "08:00";
    public string StartDate { get; init; } = "";
    public int[]? Weekdays { get; init; }
    public int? MonthDay { get; init; }
    public bool LastDay { get; init; }
    public string? UntilDate { get; init; }
    public int? Count { get; init; }
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public void Validate()
    {
        if (Frequency is not ("daily" or "weekly" or "monthly") || Interval is < 1 or > 365 ||
            !TimeOnly.TryParseExact(LocalTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
            !DateOnly.TryParseExact(StartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
            UntilDate is not null && (!DateOnly.TryParseExact(UntilDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end) || end < start) ||
            Count is < 1 or > 10000 || Count is not null && UntilDate is not null)
            throw new ArgumentException("Regra de repetição inválida; use data-base, hora local e um único encerramento.");
        if (Frequency == "weekly" && (Weekdays is not { Length: > 0 and <= 7 } || Weekdays.Any(d => d is < 1 or > 7) || Weekdays.Distinct().Count() != Weekdays.Length) ||
            Frequency != "weekly" && Weekdays is { Length: > 0 } ||
            Frequency == "monthly" && (LastDay == MonthDay.HasValue || MonthDay is < 1 or > 31) ||
            Frequency != "monthly" && (MonthDay.HasValue || LastDay))
            throw new ArgumentException("Informe dias da semana para regra semanal ou dia/último dia para mensal.");
    }
}

public sealed record RecurrenceInstant(DateTimeOffset DueAt, string LocalKey, int Ordinal);

public static class ReminderCalendar
{
    // Calendar dates are evaluated in the series zone; UTC durations never define recurrence.
    public static RecurrenceInstant? Next(ReminderRecurrenceRule rule, string timeZoneId, DateTimeOffset afterExclusive)
    {
        rule.Validate(); var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var start = DateOnly.ParseExact(rule.StartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var until = rule.UntilDate is null ? DateOnly.MaxValue : DateOnly.ParseExact(rule.UntilDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var time = TimeOnly.ParseExact(rule.LocalTime, "HH:mm", CultureInfo.InvariantCulture);
        var startMonday = start.AddDays(-(((int)start.DayOfWeek + 6) % 7));
        var localAfter = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(afterExclusive, zone).DateTime);
        var scanStart = DateOnly.FromDayNumber(Math.Max(start.DayNumber, Math.Max(0, localAfter.DayNumber - 2)));
        // Jump directly to the relevant local date; two prior days cover whole-day timezone gaps.
        // Search is bounded to 1,000 future calendar years, never scanning from an old series base.
        for (var day = scanStart; day <= until && day.DayNumber - scanStart.DayNumber <= 366000; day = day.AddDays(1))
        {
            var days = day.DayNumber - start.DayNumber;
            var months = (day.Year - start.Year) * 12 + day.Month - start.Month;
            var matches = rule.Frequency switch {
                "daily" => days % rule.Interval == 0,
                "weekly" => (day.DayNumber - startMonday.DayNumber) / 7 % rule.Interval == 0 && rule.Weekdays!.Contains(((int)day.DayOfWeek + 6) % 7 + 1),
                _ => months % rule.Interval == 0 && day.Day == (rule.LastDay ? DateTime.DaysInMonth(day.Year, day.Month) : rule.MonthDay)
            };
            if (!matches) { if (day == DateOnly.MaxValue) break; continue; }
            var ordinal = Ordinal(rule, start, day);
            if (ordinal > rule.Count) return null;
            var local = day.ToDateTime(time, DateTimeKind.Unspecified);
            var due = ResolveLocal(local, zone);
            if (due > afterExclusive) return new(due, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T" + rule.LocalTime, ordinal);
            if (day == DateOnly.MaxValue) break;
        }
        return null;
    }
    private static int Ordinal(ReminderRecurrenceRule rule, DateOnly start, DateOnly day)
    {
        if (rule.Frequency == "daily") return (day.DayNumber - start.DayNumber) / rule.Interval + 1;
        if (rule.Frequency == "weekly")
        {
            var startIso = ((int)start.DayOfWeek + 6) % 7 + 1; var iso = ((int)day.DayOfWeek + 6) % 7 + 1;
            var week = (day.DayNumber - start.AddDays(1 - startIso).DayNumber) / 7;
            var priorActiveWeeks = (week + rule.Interval - 1) / rule.Interval;
            return priorActiveWeeks * rule.Weekdays!.Length + rule.Weekdays.Count(d => d <= iso) - rule.Weekdays.Count(d => d < startIso);
        }
        var lastMonth = (day.Year - start.Year) * 12 + day.Month - start.Month;
        if (rule.LastDay || rule.MonthDay <= 28)
            return lastMonth / rule.Interval + 1 - (!rule.LastDay && rule.MonthDay < start.Day ? 1 : 0);
        var count = 0;
        // Month-day 29..31 requires counting skipped months; no occurrence rows are generated.
        for (var month = 0; month <= lastMonth; month += rule.Interval)
        {
            var value = start.Year * 12 + start.Month - 1 + month; var year = value / 12; var number = value % 12 + 1;
            if (DateTime.DaysInMonth(year, number) >= rule.MonthDay && (month != 0 || rule.MonthDay >= start.Day)) count++;
        }
        return count;
    }
    public static DateTimeOffset ResolveLocal(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        // Gaps may span a day (international date-line changes). Find the first valid minute.
        var limit = local.AddDays(2);
        while (zone.IsInvalidTime(local) && local < limit) local = local.AddMinutes(1);
        if (zone.IsInvalidTime(local)) throw new ArgumentException("Horário local inválido sem resolução bounded.");
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}

public sealed class ReminderSeries : AuditableEntity
{
    private ReminderSeries() { }
    public ReminderSeries(string text, string timeZoneId, ReminderRecurrenceRule rule, Guid? conversationId, DateTimeOffset now, string deliveryPolicy = "allEligible")
    {
        Reminder.ValidateText(text); rule.Validate(); ValidateIanaZone(timeZoneId); TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); Reminder.ValidatePolicy(deliveryPolicy);
        InitializeAudit(now); Text = text.Trim(); TimeZoneId = timeZoneId; RuleJson = JsonSerializer.Serialize(rule, ReminderRecurrenceRule.JsonOptions);
        SourceConversationId = conversationId; DeliveryPolicy = deliveryPolicy; ResumeAfter = now;
    }
    public string Text { get; private set; } = "";
    public string TimeZoneId { get; private set; } = "";
    public string RuleJson { get; private set; } = "";
    public string Status { get; private set; } = "active";
    public int Revision { get; private set; } = 1;
    public string DeliveryPolicy { get; private set; } = "allEligible";
    public Guid? SourceConversationId { get; private set; }
    public DateTimeOffset ResumeAfter { get; private set; }
    public DateTimeOffset? LastMaterializedAt { get; private set; }
    public int GeneratedCount { get; private set; }
    private static void ValidateIanaZone(string id)
    {
        if (id != "UTC" && !TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out _)) throw new ArgumentException("Use um timezone IANA.");
    }
    public ReminderRecurrenceRule Rule() => JsonSerializer.Deserialize<ReminderRecurrenceRule>(RuleJson, ReminderRecurrenceRule.JsonOptions)!;
    public RecurrenceInstant? Next(DateTimeOffset now) => Status != "active" ? null : ReminderCalendar.Next(Rule(), TimeZoneId,
        new[] { ResumeAfter, LastMaterializedAt ?? DateTimeOffset.MinValue, now.AddMinutes(-10) }.Max());
    public void Materialized(RecurrenceInstant item, DateTimeOffset now) { LastMaterializedAt = item.DueAt; GeneratedCount = item.Ordinal; Touch(now); }
    public void Complete(DateTimeOffset now) { Status = "completed"; Touch(now); }
    public void Change(int expectedRevision, string? text, ReminderRecurrenceRule? rule, string? deliveryPolicy, DateTimeOffset now)
    {
        CheckRevision(expectedRevision); if (Status == "cancelled") throw new ArgumentException("Série cancelada.");
        if (text is null && rule is null && deliveryPolicy is null) throw new ArgumentException("Informe uma alteração da série.");
        if (text is not null) { Reminder.ValidateText(text); Text = text.Trim(); }
        if (rule is not null) { rule.Validate(); RuleJson = JsonSerializer.Serialize(rule, ReminderRecurrenceRule.JsonOptions); LastMaterializedAt = null; }
        if (deliveryPolicy is not null) { Reminder.ValidatePolicy(deliveryPolicy); DeliveryPolicy = deliveryPolicy; }
        ResumeAfter = now; LastMaterializedAt = now; Revision++; Touch(now);
    }
    public void SetState(int expectedRevision, string state, DateTimeOffset now)
    {
        CheckRevision(expectedRevision); if (state is not ("active" or "paused" or "cancelled") || Status == "cancelled") throw new ArgumentException("Estado de série inválido.");
        if (state == "active") { ResumeAfter = now; LastMaterializedAt = now; } Status = state; Revision++; Touch(now);
    }
    public void MarkSkipped(DateTimeOffset now) { Revision++; Touch(now); }
    public void CheckRevision(int revision) { if (revision != Revision) throw new InvalidOperationException("series_revision_conflict"); }
}
