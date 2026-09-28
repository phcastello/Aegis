using System.Text.Json;
using Aegis.Application.Calendar;
using Aegis.Application.Calendar.Tools;
using Aegis.Domain;
using Aegis.Domain.Entities;
using Xunit;

namespace Aegis.Application.Tests;

public sealed partial class CalendarTests
{
    [Theory]
    [InlineData("daily", 1, "2026-10-05", "RRULE:FREQ=DAILY")]
    [InlineData("daily", 2, "2026-10-05", "RRULE:FREQ=DAILY;INTERVAL=2")]
    [InlineData("weekly", 1, "2026-10-05", "RRULE:FREQ=WEEKLY;BYDAY=MO")]
    [InlineData("weekly", 2, "2026-10-09", "RRULE:FREQ=WEEKLY;INTERVAL=2;BYDAY=FR")]
    [InlineData("monthly", 1, "2026-10-15", "RRULE:FREQ=MONTHLY")]
    [InlineData("yearly", 1, "2026-09-28", "RRULE:FREQ=YEARLY")]
    public async Task CreatesOneRealSeriesForBasicFrequencies(string frequency, int interval, string date, string rule)
    {
        using var f = new Fixture();
        var changes = new CalendarEventChanges("Atividade", date + "T18:00:00-03:00", date + "T19:00:00-03:00", false,
            Recurrence: new(frequency, interval));
        var prepared = await f.Calendar.PrepareAsync(CalendarActionTypes.Create, "primary", "series1", changes);
        Assert.Equal(rule, prepared.Fields["recurrence"]![0]!.GetValue<string>());
        Assert.Contains("sem término", prepared.HumanSummary);
        if (frequency == "monthly") Assert.Contains("no dia 15", prepared.HumanSummary);
        if (frequency == "yearly") Assert.Contains("em 28/09", prepared.HumanSummary);
        Assert.Equal(0, f.Google.Mutations);
        await f.Calendar.ExecuteAsync(CalendarActionTypes.Create, "primary", "series1", prepared);
        Assert.Equal(rule, f.Google.Events["series1"]["recurrence"]![0]!.GetValue<string>());
        Assert.True(await f.Calendar.VerifyAsync(CalendarActionTypes.Create, "primary", "series1", prepared));
        Assert.Single(f.Google.Events);
    }

    [Fact]
    public async Task WeeklyDaysAreSortedAndCountIsPreserved()
    {
        using var f = new Fixture();
        var prepared = await f.Calendar.PrepareAsync(CalendarActionTypes.Create, "primary", "series1",
            new("Academia", "2026-10-05T18:00:00-03:00", "2026-10-05T19:00:00-03:00", false,
                Recurrence: new("weekly", DaysOfWeek: ["friday", "monday", "wednesday"], Count: 20)));
        Assert.Equal("RRULE:FREQ=WEEKLY;BYDAY=MO,WE,FR;COUNT=20", prepared.Fields["recurrence"]![0]!.GetValue<string>());
        Assert.Contains("segunda, quarta, sexta", prepared.HumanSummary);
        Assert.Contains("20 ocorrências", prepared.HumanSummary);
        await f.Calendar.ExecuteAsync(CalendarActionTypes.Create, "primary", "series1", prepared);
        // Google's harmless RRULE property and weekday ordering changes do not break post-state verification.
        f.Google.Events["series1"]["recurrence"]![0] = "RRULE:COUNT=20;BYDAY=FR,MO,WE;FREQ=WEEKLY;INTERVAL=1";
        Assert.True(await f.Calendar.VerifyAsync(CalendarActionTypes.Create, "primary", "series1", prepared));
    }

    [Fact]
    public async Task AcademyCreatesOneGoogleMasterWithThreeWeekdaysAndNoEnd()
    {
        using var f = new Fixture();
        var prepared = await f.Calendar.PrepareAsync(CalendarActionTypes.Create, "primary", "academy1",
            new("Academia", "2026-10-05T18:00:00-03:00", "2026-10-05T19:00:00-03:00", false,
                Recurrence: new("weekly", DaysOfWeek: ["monday", "wednesday", "friday"])));
        Assert.Contains("sem término", prepared.HumanSummary);
        Assert.Equal(0, f.Google.Mutations);
        await f.Calendar.ExecuteAsync(CalendarActionTypes.Create, "primary", "academy1", prepared);
        Assert.Equal(1, f.Google.Mutations);
        Assert.Single(f.Google.Events);
        Assert.Equal("RRULE:FREQ=WEEKLY;BYDAY=MO,WE,FR", f.Google.Events["academy1"]["recurrence"]![0]!.ToString());
        Assert.True(await f.Calendar.VerifyAsync(CalendarActionTypes.Create, "primary", "academy1", prepared));
    }

    [Fact]
    public async Task UntilIsInclusiveInEventTimezoneAndAllDayKeepsExclusiveGoogleEnd()
    {
        using var f = new Fixture();
        var timed = await f.Calendar.PrepareAsync(CalendarActionTypes.Create, "primary", "timed1",
            new("Terapia", "2026-10-08T17:00:00-03:00", "2026-10-08T18:00:00-03:00", false,
                Recurrence: new("weekly", Until: "2026-12-31")));
        Assert.Equal("RRULE:FREQ=WEEKLY;BYDAY=TH;UNTIL=20270101T025959Z", timed.Fields["recurrence"]![0]!.ToString());
        Assert.Contains("até 31/12/2026", timed.HumanSummary);
        await f.Calendar.ExecuteAsync(CalendarActionTypes.Create, "primary", "timed1", timed);
        Assert.True(await f.Calendar.VerifyAsync(CalendarActionTypes.Create, "primary", "timed1", timed));

        var allDay = await f.Calendar.PrepareAsync(CalendarActionTypes.Create, "primary", "birthday1",
            new("Aniversário", "2026-09-28", "2026-09-28", true,
                Recurrence: new("yearly", Until: "2030-09-28")));
        Assert.Equal("RRULE:FREQ=YEARLY;UNTIL=20300928", allDay.Fields["recurrence"]![0]!.ToString());
        Assert.Equal("2026-09-29", allDay.Fields["end"]!["date"]!.ToString());
        await f.Calendar.ExecuteAsync(CalendarActionTypes.Create, "primary", "birthday1", allDay);
        Assert.Equal("2026-09-29", f.Google.Events["birthday1"]["end"]!["date"]!.ToString());
        Assert.True(await f.Calendar.VerifyAsync(CalendarActionTypes.Create, "primary", "birthday1", allDay));
    }

    [Fact]
    public async Task UntilUsesEventZoneAcrossDaylightSavingChange()
    {
        using var f = new Fixture();
        var prepared = await f.Calendar.PrepareAsync(CalendarActionTypes.Create, "primary", "newyork1",
            new("Aula", "2026-10-30T10:00:00-04:00", "2026-10-30T11:00:00-04:00", false, "America/New_York",
                Recurrence: new("weekly", Until: "2026-11-10")));
        Assert.Equal("RRULE:FREQ=WEEKLY;BYDAY=FR;UNTIL=20261111T045959Z", prepared.Fields["recurrence"]![0]!.ToString());
    }

    [Fact]
    public async Task UtcInstantsCanUseExplicitRecurrenceTimezone()
    {
        using var f = new Fixture();
        var prepared = await f.Calendar.PrepareAsync(CalendarActionTypes.Create, "primary", "utcseries1",
            new("Academia", "2026-10-05T21:00:00Z", "2026-10-05T22:00:00Z", false, "America/Sao_Paulo",
                Recurrence: new("weekly")));
        Assert.Equal("RRULE:FREQ=WEEKLY;BYDAY=MO", prepared.Fields["recurrence"]![0]!.ToString());
    }

    [Theory]
    [InlineData("unknown", 1, null, null, null)]
    [InlineData("daily", 0, null, null, null)]
    [InlineData("daily", 1, 0, null, null)]
    [InlineData("daily", 1, 2, "2026-12-31", null)]
    [InlineData("daily", 1, null, "2026-10-04", null)]
    [InlineData("weekly", 1, null, null, "funday")]
    [InlineData("monthly", 1, null, null, "monday")]
    [InlineData("none", 2, null, null, null)]
    public async Task InvalidRecurrenceCannotCreatePendingActionOrMutateGoogle(string frequency, int interval, int? count, string? until, string? day)
    {
        using var f = new Fixture();
        var recurrence = new { frequency, interval, count, until, daysOfWeek = day is null ? null : new[] { day } };
        var result = await f.Create.ExecuteAsync(Args(new { summary = "Atividade", start = "2026-10-05T18:00:00-03:00",
            end = "2026-10-05T19:00:00-03:00", allDay = false, recurrence }), await f.Context());
        Assert.Equal("invalid_tool_arguments", result.ErrorCode);
        Assert.Empty(f.Db.PendingCalendarActions);
        Assert.Equal(0, f.Google.Mutations);
    }

    [Fact]
    public async Task ContradictoryNoneAndFirstDayMismatchAreRejected()
    {
        using var f = new Fixture();
        var none = await f.Create.ExecuteAsync(Args(new { summary = "X", start = Start, end = End, allDay = false,
            recurrence = new { frequency = "none", count = 2 } }), await f.Context());
        Assert.False(none.Success);
        var mismatch = await f.Create.ExecuteAsync(Args(new { summary = "X", start = "2026-10-05T18:00:00-03:00",
            end = "2026-10-05T19:00:00-03:00", allDay = false,
            recurrence = new { frequency = "weekly", daysOfWeek = new[] { "tuesday" } } }), await f.Context());
        Assert.False(mismatch.Success);
        var offset = await f.Create.ExecuteAsync(Args(new { summary = "X", start = "2026-10-05T18:00:00-04:00",
            end = "2026-10-05T19:00:00-04:00", allDay = false, timeZone = "America/Sao_Paulo",
            recurrence = new { frequency = "weekly" } }), await f.Context());
        Assert.False(offset.Success);
        Assert.Empty(f.Db.PendingCalendarActions);
        Assert.Equal(0, f.Google.Mutations);
    }

    [Fact]
    public async Task RecurringCreateRequiresLaterTurnAndAmendmentPreservesOrReplacesRule()
    {
        using var f = new Fixture();
        var firstContext = await f.Context();
        var create = await f.Create.ExecuteAsync(Args(new { summary = "Academia", start = "2026-10-05T18:00:00-03:00",
            end = "2026-10-05T19:00:00-03:00", allDay = false,
            recurrence = new { frequency = "weekly", daysOfWeek = new[] { "monday" } } }), firstContext);
        Assert.True(create.Success);
        Assert.Equal(0, f.Google.Mutations);
        Assert.False((await f.Confirm.ExecuteAsync(Empty, firstContext)).Success);
        var rename = await new CalendarAmendPendingActionTool(f.Db, f.Calendar, f.ToolContext).ExecuteAsync(
            Args(new { summary = "Treino" }), await f.Context("Troca o nome"));
        Assert.True(rename.Success);
        Assert.Equal("RRULE:FREQ=WEEKLY;BYDAY=MO", LatestRecurrence(f));
        var addDay = await new CalendarAmendPendingActionTool(f.Db, f.Calendar, f.ToolContext).ExecuteAsync(
            Args(new { recurrence = new { frequency = "weekly", daysOfWeek = new[] { "wednesday", "monday" } } }),
            await f.Context("Quarta também"));
        Assert.True(addDay.Success);
        Assert.Equal("RRULE:FREQ=WEEKLY;BYDAY=MO,WE", LatestRecurrence(f));
        Assert.True((await f.Confirm.ExecuteAsync(Empty, await f.Context("Sim"))).Success);
        Assert.Equal("RRULE:FREQ=WEEKLY;BYDAY=MO,WE", f.Google.Events.Single().Value["recurrence"]![0]!.ToString());
        Assert.Single(f.Google.Events);
    }

    [Fact]
    public async Task PendingCreateCanAcquireOrRemoveRecurrenceExplicitly()
    {
        using var f = new Fixture();
        await f.PrepareCreate();
        var amend = new CalendarAmendPendingActionTool(f.Db, f.Calendar, f.ToolContext);
        Assert.True((await amend.ExecuteAsync(Args(new { recurrence = new { frequency = "daily" } }), await f.Context("Todos os dias"))).Success);
        Assert.Equal("RRULE:FREQ=DAILY", LatestRecurrence(f));
        Assert.True((await amend.ExecuteAsync(Args(new { recurrence = new { frequency = "none" } }), await f.Context("Só uma vez"))).Success);
        Assert.Null(LatestRecurrence(f));
        Assert.True((await f.Confirm.ExecuteAsync(Empty, await f.Context("Sim"))).Success);
        Assert.Null(f.Google.Events.Single().Value["recurrence"]);
    }

    [Theory]
    [InlineData("aegis_default")]
    [InlineData("calendar_default")]
    [InlineData("custom")]
    [InlineData("none")]
    public async Task RecurringMasterKeepsSelectedEventReminders(string mode)
    {
        using var f = new Fixture();
        var changes = new CalendarEventChanges("Stand-up", Start, End, false, ReminderMode: mode,
            Reminders: mode == "custom" ? [new("popup", 30)] : null, Recurrence: new("daily"));
        var prepared = await f.Calendar.PrepareAsync(CalendarActionTypes.Create, "primary", "standup1", changes);
        await f.Calendar.ExecuteAsync(CalendarActionTypes.Create, "primary", "standup1", prepared);
        var reminders = f.Google.Events["standup1"]["reminders"]!;
        Assert.Equal(mode == "calendar_default", reminders["useDefault"]!.GetValue<bool>());
        if (mode != "calendar_default")
            Assert.Equal(mode == "none" ? 0 : mode == "custom" ? 1 : 5, reminders["overrides"]!.AsArray().Count);
        Assert.True(await f.Calendar.VerifyAsync(CalendarActionTypes.Create, "primary", "standup1", prepared));
    }

    [Fact]
    public async Task RecurringCreateUsesObservedSecondaryCalendar()
    {
        using var f = new Fixture();
        f.Google.AddCalendar("diario", "Diario");
        await new CalendarListCalendarsTool(f.Calendar, f.ToolContext).ExecuteAsync(Empty, await f.Context());
        var result = await f.Create.ExecuteAsync(Args(new { calendarId = "diario", summary = "Aula",
            start = "2026-10-06T13:20:00-03:00", end = "2026-10-06T15:00:00-03:00", allDay = false,
            recurrence = new { frequency = "weekly", daysOfWeek = new[] { "tuesday", "thursday" } } }), await f.Context());
        Assert.True(result.Success);
        Assert.Equal(0, f.Google.Mutations);
        Assert.True((await f.Confirm.ExecuteAsync(Empty, await f.Context("Sim"))).Success);
        Assert.Empty(f.Google.Events);
        Assert.Equal("RRULE:FREQ=WEEKLY;BYDAY=TU,TH", f.Google.EventsFor("diario").Single().Value["recurrence"]![0]!.ToString());
        Assert.Equal("diario", Assert.Single(f.Google.MutationCalendars));
    }

    [Fact]
    public async Task ExistingEventPendingUpdateCannotAcquireRecurrence()
    {
        using var f = new Fixture();
        f.Google.AddEvent();
        await f.Observe();
        Assert.True((await f.Update.ExecuteAsync(Args(new { eventId = "event1", summary = "Novo título" }), await f.Context())).Success);
        var amend = await new CalendarAmendPendingActionTool(f.Db, f.Calendar, f.ToolContext).ExecuteAsync(
            Args(new { recurrence = new { frequency = "weekly" } }), await f.Context());
        Assert.False(amend.Success);
        Assert.Equal("invalid_tool_arguments", amend.ErrorCode);
        Assert.Single(f.Db.PendingCalendarActions.AsEnumerable(), action => action.IsOpen());
        Assert.Equal(0, f.Google.Mutations);
    }

    [Fact]
    public async Task AmbiguousRecurringInsertIsRecoveredWithoutAnotherSeries()
    {
        using var f = new Fixture();
        Assert.True((await f.Create.ExecuteAsync(Args(new { summary = "Academia", start = "2026-10-05T18:00:00-03:00",
            end = "2026-10-05T19:00:00-03:00", allDay = false,
            recurrence = new { frequency = "weekly", daysOfWeek = new[] { "monday", "wednesday" } } }), await f.Context())).Success);
        f.Google.ThrowAfterMutation = true;
        f.Google.FailGetsAfterMutation = true;
        Assert.Equal("calendar_action_outcome_unknown", (await f.Confirm.ExecuteAsync(Empty, await f.Context("Sim"))).ErrorCode);
        f.Google.ThrowAfterMutation = false;
        f.Google.FailGetsAfterMutation = false;
        Assert.True((await f.Confirm.ExecuteAsync(Empty, await f.Context("Sim"))).Success);
        Assert.Single(f.Google.Events);
        Assert.Equal(2, f.Google.Mutations);
        Assert.Equal("RRULE:FREQ=WEEKLY;BYDAY=MO,WE", f.Google.Events.Single().Value["recurrence"]![0]!.ToString());
    }

    private static string? LatestRecurrence(Fixture fixture)
    {
        var latest = fixture.Db.PendingCalendarActions.AsEnumerable().Single(action => action.IsOpen());
        var payload = JsonSerializer.Deserialize<CalendarActionPayload>(latest.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        return payload.Fields["recurrence"]?[0]?.GetValue<string>();
    }
}
