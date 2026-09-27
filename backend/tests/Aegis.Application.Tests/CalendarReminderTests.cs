using System.Text.Json;
using System.Text.Json.Nodes;
using Aegis.Application.Calendar;
using Aegis.Application.Calendar.Tools;
using Aegis.Application.Runtime;
using Aegis.Domain;
using Aegis.Infrastructure;
using Aegis.Infrastructure.Calendar;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aegis.Application.Tests;

public sealed partial class CalendarTests
{
    private static int[] ReminderMinutes(JsonObject fields) => fields["reminders"]!["overrides"]!.AsArray()
        .Select(item => item!["minutes"]!.GetValue<int>()).ToArray();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreatePersistsTypeSpecificPopupPolicyBeforeLaterConfirmation(bool allDay)
    {
        using var f = new Fixture();
        // Real calendar defaults must not replace the Aegis create policy.
        f.Google.Calendars["primary"]["defaultReminders"] = JsonNode.Parse("""[{"method":"email","minutes":30}]""");
        var context = await f.Context();
        var prepared = await f.Create.ExecuteAsync(Args(new { summary = "Teste", start = allDay ? "2026-10-10" : Start,
            end = allDay ? "2026-10-10" : End, allDay }), context);
        Assert.True(prepared.Success);
        var action = Assert.Single(f.Db.PendingCalendarActions);
        var payload = Payload(action);
        var expected = allDay ? new[] { 3480, 600, 240 } : [4320, 1440, 240, 60, 0];
        Assert.Equal("aegis_default", payload.ReminderMode);
        Assert.Equal(expected, ReminderMinutes(payload.Fields));
        Assert.False(payload.Fields["reminders"]!["useDefault"]!.GetValue<bool>());
        Assert.All(payload.Fields["reminders"]!["overrides"]!.AsArray(), item => Assert.Equal("popup", item!["method"]!.ToString()));
        Assert.Equal(expected.Length, JsonDocument.Parse(prepared.Content).RootElement.GetProperty("reminders").GetProperty("overrides").GetArrayLength());
        Assert.Equal(0, f.Google.Mutations);
        Assert.False((await f.Confirm.ExecuteAsync(Empty, context)).Success);
        Assert.True((await f.Confirm.ExecuteAsync(Empty, await f.Context("Manda bala"))).Success);
        Assert.Equal(expected, ReminderMinutes(f.Google.Events[action.EventId]));
        var read = await f.Calendar.GetEventAsync("primary", action.EventId);
        Assert.Equal(expected, read.Reminders!.Overrides!.Select(item => item.Minutes));
    }

    [Fact]
    public void AllDayDefaultOffsetsResolveToUsefulReferenceLocalTimes()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(RuntimeContextProvider.ReferenceTimeZoneId);
        var midnight = new DateTime(2026, 10, 10, 0, 0, 0);
        var start = new DateTimeOffset(midnight, zone.GetUtcOffset(midnight));
        var actual = new GoogleCalendarOptions().AllDayDefaultReminders.Select(minutes =>
            TimeZoneInfo.ConvertTime(start.AddMinutes(-minutes), zone).ToString("yyyy-MM-dd HH:mm")).ToArray();
        Assert.Equal(["2026-10-07 14:00", "2026-10-09 14:00", "2026-10-09 20:00"], actual);
    }

    [Theory]
    [InlineData("custom", false)]
    [InlineData("none", false)]
    [InlineData("calendar_default", true)]
    public async Task ExplicitCreatePreferenceOverridesAegisPolicy(string mode, bool useDefault)
    {
        using var f = new Fixture();
        f.Google.Calendars["primary"]["defaultReminders"] = JsonNode.Parse("""[{"method":"email","minutes":30}]""");
        var args = new Dictionary<string, object?> { ["summary"] = "Dentista", ["start"] = Start, ["end"] = End,
            ["allDay"] = false, ["reminderMode"] = mode };
        if (mode == "custom") args["reminders"] = new[] { new CalendarReminderData("popup", 60) };
        Assert.True((await f.Create.ExecuteAsync(Args(args), await f.Context())).Success);
        var action = Assert.Single(f.Db.PendingCalendarActions);
        var payload = Payload(action);
        Assert.Equal(mode, payload.ReminderMode);
        Assert.Equal(useDefault, payload.Fields["reminders"]!["useDefault"]!.GetValue<bool>());
        if (useDefault) Assert.Null(payload.Fields["reminders"]!["overrides"]);
        else Assert.Equal(mode == "custom" ? new[] { 60 } : [], ReminderMinutes(payload.Fields));
        Assert.Contains(mode == "custom" ? "1h antes" : mode == "none" ? "sem lembretes" : "padrão do calendário", action.HumanSummary);
        Assert.True((await f.Confirm.ExecuteAsync(Empty, await f.Context("Pode"))).Success);
        Assert.True(await f.Calendar.VerifyAsync(action.ActionType, action.CalendarId, action.EventId, payload));
    }

    [Theory]
    [InlineData("custom")]
    [InlineData("calendar_default")]
    [InlineData("none")]
    public async Task NormalUpdatePreservesAllReminderModesAndUnknownResourceFields(string existingMode)
    {
        using var f = new Fixture();
        var item = f.Google.AddEvent();
        item["reminders"] = existingMode switch
        {
            "custom" => JsonNode.Parse("""{"useDefault":false,"overrides":[{"method":"email","minutes":90},{"method":"popup","minutes":15}]}"""),
            "calendar_default" => JsonNode.Parse("""{"useDefault":true}"""),
            _ => JsonNode.Parse("""{"useDefault":false,"overrides":[]}""")
        };
        item["extendedProperties"] = JsonNode.Parse("""{"private":{"source":"preserve"}}""");
        var before = item["reminders"]!.DeepClone();
        await f.Observe();
        Assert.True((await f.Update.ExecuteAsync(Args(new { eventId = "event1", summary = "Consulta",
            start = "2026-10-11T16:00:00-03:00", end = "2026-10-11T17:00:00-03:00", description = "Mudou", location = "Centro" }), await f.Context())).Success);
        var pending = Assert.Single(f.Db.PendingCalendarActions);
        Assert.Equal("keep", Payload(pending).ReminderMode);
        Assert.False(Payload(pending).Fields.ContainsKey("reminders"));
        Assert.True((await f.Confirm.ExecuteAsync(Empty, await f.Context())).Success);
        var final = f.Google.Events["event1"];
        Assert.True(JsonNode.DeepEquals(before, final["reminders"]));
        Assert.Equal("preserve", final["extendedProperties"]!["private"]!["source"]!.ToString());
        Assert.Equal("Centro", final["location"]!.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SwitchingExistingEventTypeDoesNotReinterpretCustomReminders(bool toAllDay)
    {
        using var f = new Fixture();
        var item = f.Google.AddEvent();
        if (!toAllDay)
        {
            item["start"] = new JsonObject { ["date"] = "2026-10-10" };
            item["end"] = new JsonObject { ["date"] = "2026-10-11" };
        }
        item["reminders"] = JsonNode.Parse("""{"useDefault":false,"overrides":[{"method":"popup","minutes":3480},{"method":"email","minutes":5}]}""");
        var before = item["reminders"]!.DeepClone();
        await f.Observe();
        var result = await f.Update.ExecuteAsync(Args(new { eventId = "event1", allDay = toAllDay,
            start = toAllDay ? "2026-10-10" : Start, end = toAllDay ? "2026-10-10" : End }), await f.Context());
        Assert.True(result.Success);
        Assert.True((await f.Confirm.ExecuteAsync(Empty, await f.Context())).Success);
        Assert.True(JsonNode.DeepEquals(before, f.Google.Events["event1"]["reminders"]));
    }

    [Theory]
    [InlineData("custom", false)]
    [InlineData("aegis_default", false)]
    [InlineData("aegis_default", true)]
    [InlineData("calendar_default", false)]
    [InlineData("none", false)]
    public async Task ReminderOnlyUpdateWorksInOriginCalendarAndRequiresConfirmation(string mode, bool allDay)
    {
        using var f = new Fixture();
        f.Google.AddCalendar("diario", "Diario");
        var item = f.Google.AddEvent("diario-event", "diario");
        if (allDay)
        {
            item["start"] = new JsonObject { ["date"] = "2026-10-10" };
            item["end"] = new JsonObject { ["date"] = "2026-10-11" };
        }
        item["reminders"] = JsonNode.Parse("""{"useDefault":false,"overrides":[{"method":"popup","minutes":12}]}""");
        await new CalendarListEventsTool(f.Calendar, f.ToolContext).ExecuteAsync(Empty, await f.Context());
        var args = new Dictionary<string, object?> { ["eventId"] = "diario-event", ["reminderMode"] = mode };
        if (mode == "custom") args["reminders"] = new[] { new CalendarReminderData("popup", 4320), new CalendarReminderData("popup", 0) };
        var context = await f.Context();
        Assert.True((await f.Update.ExecuteAsync(Args(args), context)).Success);
        var action = Assert.Single(f.Db.PendingCalendarActions);
        Assert.Equal("diario", action.CalendarId);
        Assert.Single(Payload(action).Fields);
        Assert.Equal(0, f.Google.Mutations);
        Assert.False((await f.Confirm.ExecuteAsync(Empty, context)).Success);
        Assert.True((await f.Confirm.ExecuteAsync(Empty, await f.Context())).Success);
        var reminders = f.Google.SecondaryEvents["diario"]["diario-event"]["reminders"]!;
        if (mode == "calendar_default") Assert.True(reminders["useDefault"]!.GetValue<bool>());
        else Assert.Equal(mode == "none" ? [] : mode == "custom" ? new[] { 4320, 0 } : allDay ? [3480, 600, 240] : [4320, 1440, 240, 60, 0],
            ReminderMinutes(f.Google.SecondaryEvents["diario"]["diario-event"]));
        Assert.Equal("America/Sao_Paulo", (await f.Calendar.GetEventAsync("diario", "diario-event")).TimeZone);
    }

    [Fact]
    public async Task ReadsDistinguishDefaultsCustomAndNoneAndCalendarListReportsRealDefaults()
    {
        using var f = new Fixture();
        f.Google.Calendars["primary"]["defaultReminders"] = JsonNode.Parse("""[{"method":"email","minutes":75},{"method":"popup","minutes":10}]""");
        f.Google.AddCalendar("diario", "Diario")["defaultReminders"] = new JsonArray();
        f.Google.AddCalendar("unknown", "Sem dados");
        f.Google.AddEvent("defaults")["reminders"] = JsonNode.Parse("""{"useDefault":true}""");
        f.Google.AddEvent("custom")["reminders"] = JsonNode.Parse("""{"useDefault":false,"overrides":[{"method":"popup","minutes":60}]}""");
        f.Google.AddEvent("none")["reminders"] = JsonNode.Parse("""{"useDefault":false}""");
        var context = await f.Context();
        var list = await new CalendarListEventsTool(f.Calendar, f.ToolContext).ExecuteAsync(Empty, context);
        Assert.True(list.Success);
        var values = JsonDocument.Parse(list.Content).RootElement.GetProperty("events").EnumerateArray().ToDictionary(e => e.GetProperty("eventId").GetString()!);
        Assert.True(values["defaults"].GetProperty("reminders").GetProperty("useDefault").GetBoolean());
        Assert.False(values["defaults"].GetProperty("reminders").TryGetProperty("overrides", out _));
        Assert.Equal(60, values["custom"].GetProperty("reminders").GetProperty("overrides")[0].GetProperty("minutes").GetInt32());
        Assert.Equal(0, values["none"].GetProperty("reminders").GetProperty("overrides").GetArrayLength());
        var details = await new CalendarGetEventTool(f.Calendar, f.ToolContext).ExecuteAsync(Args(new { eventId = "custom" }), context);
        Assert.Equal(60, JsonDocument.Parse(details.Content).RootElement.GetProperty("calendarEvent").GetProperty("reminders").GetProperty("overrides")[0].GetProperty("minutes").GetInt32());
        var calendars = await new CalendarListCalendarsTool(f.Calendar, f.ToolContext).ExecuteAsync(Empty, context);
        var entries = JsonDocument.Parse(calendars.Content).RootElement.GetProperty("calendars").EnumerateArray().ToDictionary(e => e.GetProperty("calendarId").GetString()!);
        Assert.True(entries["primary"].GetProperty("primary").GetBoolean());
        Assert.Equal(new[] { 75, 10 }, entries["primary"].GetProperty("defaultReminders").EnumerateArray().Select(r => r.GetProperty("minutes").GetInt32()));
        Assert.Equal(0, entries["diario"].GetProperty("defaultReminders").GetArrayLength());
        Assert.False(entries["unknown"].TryGetProperty("defaultReminders", out _));
    }

    [Theory]
    [InlineData("custom", "[{\"method\":\"popup\",\"minutes\":-1}]")]
    [InlineData("custom", "[{\"method\":\"popup\",\"minutes\":40321}]")]
    [InlineData("custom", "[{\"method\":\"popup\",\"minutes\":60},{\"method\":\"popup\",\"minutes\":60}]")]
    [InlineData("custom", "[{\"method\":\"popup\",\"minutes\":0},{\"method\":\"popup\",\"minutes\":1},{\"method\":\"popup\",\"minutes\":2},{\"method\":\"popup\",\"minutes\":3},{\"method\":\"popup\",\"minutes\":4},{\"method\":\"popup\",\"minutes\":5}]")]
    [InlineData("custom", "[{\"method\":\"sms\",\"minutes\":60}]")]
    [InlineData("custom", "[{\"method\":\"popup\"}]")]
    [InlineData("custom", "[{\"minutes\":60}]")]
    [InlineData("custom", "[{\"method\":\"popup\",\"minutes\":1.5}]")]
    [InlineData("custom", "[{\"method\":\"popup\",\"minutes\":60,\"unknown\":1}]")]
    [InlineData("custom", "[null]")]
    [InlineData("custom", "null")]
    [InlineData("none", "[]")]
    [InlineData("keep", "null")]
    [InlineData("invalid", "null")]
    public async Task InvalidRemindersAreRecoverableBeforeCalendarRequest(string mode, string reminders)
    {
        using var f = new Fixture();
        var args = JsonSerializer.SerializeToElement(new { summary = "Teste", start = Start, end = End, allDay = false, reminderMode = mode,
            reminders = JsonSerializer.Deserialize<JsonElement>(reminders) });
        var result = await f.Create.ExecuteAsync(args, await f.Context());
        Assert.False(result.Success);
        Assert.Contains("invalid_calendar_tool_arguments", result.Content);
        Assert.Empty(f.Db.PendingCalendarActions);
        Assert.Equal(0, f.Google.CalendarRequests);
    }

    [Fact]
    public async Task BoundaryValuesAndDifferentMethodsAtSameTimeAreValid()
    {
        using var f = new Fixture();
        var prepared = await f.Calendar.PrepareAsync(CalendarActionTypes.Create, "primary", "valid", new("Teste", Start, End, false,
            ReminderMode: "custom", Reminders: [new("popup", 0), new("email", 0), new("popup", 40320)]));
        Assert.Equal([0, 0, 40320], ReminderMinutes(prepared.Fields));
    }

    [Fact]
    public async Task CustomReminderAmendmentPreservesChoicesAndCanSwitchType()
    {
        using var f = new Fixture();
        await f.PrepareCreate();
        var amend = new CalendarAmendPendingActionTool(f.Db, f.Calendar, f.ToolContext);
        Assert.True((await amend.ExecuteAsync(Args(new { reminderMode = "custom", reminders = new[] { new CalendarReminderData("popup", 60) } }), await f.Context())).Success);
        Assert.True((await amend.ExecuteAsync(Args(new { start = "2026-10-10", end = "2026-10-10", allDay = true }), await f.Context())).Success);
        var action = (await f.Db.GetLatestOpenPendingCalendarActionAsync(f.ConversationId))!;
        Assert.Equal("custom", Payload(action).ReminderMode);
        Assert.Equal([60], ReminderMinutes(Payload(action).Fields));
        Assert.True((await f.Confirm.ExecuteAsync(Empty, await f.Context())).Success);
        Assert.Equal([60], ReminderMinutes(f.Google.Events[action.EventId]));
        Assert.True((await f.Calendar.GetEventAsync("primary", action.EventId)).AllDay);
    }

    [Fact]
    public async Task PendingDefaultCreateSwitchesPolicyAndReminderOnlyUpdateSurvivesAmendment()
    {
        using var f = new Fixture();
        await f.PrepareCreate();
        var amend = new CalendarAmendPendingActionTool(f.Db, f.Calendar, f.ToolContext);
        Assert.True((await amend.ExecuteAsync(Args(new { start = "2026-10-10", end = "2026-10-10", allDay = true }), await f.Context())).Success);
        var allDay = (await f.Db.GetLatestOpenPendingCalendarActionAsync(f.ConversationId))!;
        Assert.Equal([3480, 600, 240], ReminderMinutes(Payload(allDay).Fields));
        f.Google.AddEvent();
        await f.Observe();
        Assert.True((await f.Update.ExecuteAsync(Args(new { eventId = "event1", reminderMode = "none" }), await f.Context())).Success);
        Assert.True((await amend.ExecuteAsync(Args(new { summary = "Outro nome" }), await f.Context())).Success);
        var action = (await f.Db.GetLatestOpenPendingCalendarActionAsync(f.ConversationId))!;
        Assert.Equal("none", Payload(action).ReminderMode);
        Assert.True((await f.Confirm.ExecuteAsync(Empty, await f.Context())).Success);
        Assert.Empty(ReminderMinutes(f.Google.Events["event1"]));
    }

    [Fact]
    public async Task VerificationComparesReminderSemanticsAndDetectsMissingOrDifferentOverrides()
    {
        using var f = new Fixture();
        var action = await f.PrepareCreate();
        f.Google.AdjustRemindersAfterMutation = item => item["reminders"]!["overrides"] = new JsonArray(
            item["reminders"]!["overrides"]!.AsArray().Reverse().Select(value => value!.DeepClone()).ToArray());
        Assert.True((await f.Confirm.ExecuteAsync(Empty, await f.Context())).Success);
        var payload = Payload(action);
        f.Google.Events[action.EventId]["reminders"]!["overrides"]!.AsArray().RemoveAt(0);
        Assert.False(await f.Calendar.VerifyAsync(action.ActionType, action.CalendarId, action.EventId, payload));
        f.Google.Events[action.EventId].Remove("reminders");
        Assert.False(await f.Calendar.VerifyAsync(action.ActionType, action.CalendarId, action.EventId, payload));
    }

    [Fact]
    public async Task NoneVerificationAcceptsGoogleOmittingEmptyOverridesAndRetryDoesNotDuplicate()
    {
        using var f = new Fixture();
        Assert.True((await f.Create.ExecuteAsync(Args(new { summary = "Teste", start = Start, end = End, allDay = false, reminderMode = "none" }), await f.Context())).Success);
        f.Google.AdjustRemindersAfterMutation = item => item["reminders"]!.AsObject().Remove("overrides");
        f.Google.ThrowAfterMutation = true;
        var action = Assert.Single(f.Db.PendingCalendarActions);
        Assert.True((await f.Confirm.ExecuteAsync(Empty, await f.Context())).Success);
        Assert.Single(f.Google.Events);
        Assert.True(await f.Calendar.VerifyAsync(action.ActionType, action.CalendarId, action.EventId, Payload(action)));
    }

    [Fact]
    public async Task ConfiguredDefaultsAreFrozenInPendingPayload()
    {
        var options = new GoogleCalendarOptions { TimedEventDefaultReminders = [120, 30], AllDayDefaultReminders = [600] };
        using var f = new Fixture(calendarOptions: options);
        var action = await f.PrepareCreate();
        Assert.Equal([120, 30], ReminderMinutes(Payload(action).Fields));
        options.TimedEventDefaultReminders = [5];
        Assert.True((await f.Confirm.ExecuteAsync(Empty, await f.Context())).Success);
        Assert.Equal([120, 30], ReminderMinutes(f.Google.Events[action.EventId]));
    }

    [Fact]
    public void TypedOptionsRespectSectionAndEnvironmentWithoutAppendingBuiltInDefaults()
    {
        var values = new Dictionary<string, string?> { ["ConnectionStrings:AegisDatabase"] = "Host=unused;Database=unused",
            ["GoogleCalendar:TimedEventDefaultReminders:0"] = "120", ["GoogleCalendar:TimedEventDefaultReminders:1"] = "30",
            ["GoogleCalendar:AllDayDefaultReminders:0"] = "600", ["AEGIS_CALENDAR_TIMED_REMINDERS"] = "90, 15" };
        using var services = new ServiceCollection().AddInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(values).Build()).BuildServiceProvider();
        var options = services.GetRequiredService<IOptions<GoogleCalendarOptions>>().Value;
        Assert.Equal([90, 15], options.TimedEventDefaultReminders);
        Assert.Equal([600], options.AllDayDefaultReminders);
    }

    [Theory]
    [InlineData("60,60")]
    [InlineData("-1")]
    [InlineData("40321")]
    [InlineData("0,1,2,3,4,5")]
    [InlineData("60,no")]
    public void InvalidConfiguredDefaultsFailValidation(string value)
    {
        var values = new Dictionary<string, string?> { ["ConnectionStrings:AegisDatabase"] = "Host=unused;Database=unused", ["AEGIS_CALENDAR_TIMED_REMINDERS"] = value };
        using var services = new ServiceCollection().AddInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(values).Build()).BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<GoogleCalendarOptions>>().Value);
    }
}
