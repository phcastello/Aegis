using System.Text.Json;
using System.Text.Json.Nodes;
using Aegis.Application.Calendar;
using Aegis.Application.Calendar.Tools;
using Xunit;

namespace Aegis.Application.Tests;

public sealed partial class CalendarTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateWithoutNoteDoesNotGenerateDescriptionOrInternalMetadata(bool empty)
    {
        using var f = new Fixture();
        var args = new Dictionary<string, object?> { ["summary"] = "Dentista", ["start"] = Start, ["end"] = End, ["allDay"] = false };
        if (empty) args["description"] = "";
        Assert.True((await f.Create.ExecuteAsync(Args(args), await f.Context("Marca dentista; já conversamos sobre exames, mas não pedi anotação."))).Success);
        var action = Assert.Single(f.Db.PendingCalendarActions);
        var fields = Payload(action).Fields;
        Assert.True(string.IsNullOrEmpty(fields["description"]?.GetValue<string>()));
        Assert.Equal(empty, fields.ContainsKey("description"));
        Assert.Equal(0, f.Google.Mutations);
        Assert.True((await f.Confirm.ExecuteAsync(Empty, await f.Context("Pode"))).Success);
        Assert.True(string.IsNullOrEmpty(f.Google.Events[action.EventId]["description"]?.GetValue<string>()));
    }

    [Fact]
    public async Task ExplicitCreateNoteIsPersistedConfirmedAndReadable()
    {
        using var f = new Fixture();
        const string note = "Preciso levar os exames.";
        var result = await f.Create.ExecuteAsync(Args(new { summary = "Dentista", start = Start, end = End, allDay = false,
            description = note }), await f.Context());
        Assert.True(result.Success);
        var action = Assert.Single(f.Db.PendingCalendarActions);
        Assert.Equal(note, Payload(action).Fields["description"]!.GetValue<string>());
        Assert.Contains(note, action.HumanSummary);
        Assert.Equal(0, f.Google.Mutations);
        Assert.True((await f.Confirm.ExecuteAsync(Empty, await f.Context())).Success);
        var read = new CalendarGetEventTool(f.Calendar, f.ToolContext);
        var actual = await read.ExecuteAsync(Args(new { eventId = action.EventId }), await f.Context());
        Assert.True(actual.Success);
        var details = JsonDocument.Parse(actual.Content).RootElement.GetProperty("calendarEvent");
        Assert.Equal(note, details.GetProperty("description").GetString());
        Assert.False(details.TryGetProperty("descriptionTruncated", out _));
    }

    [Theory]
    [InlineData("Preciso chegar 15 minutos antes.")]
    [InlineData("Levar RG e comprovante.")]
    [InlineData("")]
    public async Task NoteOnlyUpdateReplacesOrClearsWhilePreservingEveryOtherField(string note)
    {
        using var f = new Fixture();
        var original = f.Google.AddEvent();
        original["description"] = "Anotação anterior.";
        original["reminders"] = JsonNode.Parse("""{"useDefault":false,"overrides":[{"method":"popup","minutes":15}]}""");
        original["extendedProperties"] = JsonNode.Parse("""{"private":{"source":"original"}}""");
        var expected = original.DeepClone().AsObject();
        expected["description"] = note;
        await f.Observe();
        Assert.True((await f.Update.ExecuteAsync(Args(new { eventId = "event1", description = note }), await f.Context())).Success);
        var action = Assert.Single(f.Db.PendingCalendarActions);
        Assert.Equal("description", Assert.Single(Payload(action).Fields).Key);
        Assert.Equal(note, Payload(action).Fields["description"]!.GetValue<string>());
        Assert.Contains(note.Length == 0 ? "anotação removida" : note, action.HumanSummary);
        Assert.Equal("Anotação anterior.", original["description"]!.GetValue<string>());
        Assert.Equal(0, f.Google.Mutations);
        Assert.True((await f.Confirm.ExecuteAsync(Empty, await f.Context())).Success);
        Assert.True(JsonNode.DeepEquals(expected, f.Google.Events["event1"]));
        var final = await f.Calendar.GetEventAsync("primary", "event1");
        Assert.Equal(note, final.Description);
    }

    [Fact]
    public async Task AppendUsesReadContentAndPersistedCompleteNoteWithoutDuplicatingOnLostResponse()
    {
        using var f = new Fixture();
        f.Google.AddEvent()["description"] = "Preciso levar os exames.";
        await f.Observe();
        var read = new CalendarGetEventTool(f.Calendar, f.ToolContext);
        var result = await read.ExecuteAsync(Args(new { eventId = "event1" }), await f.Context());
        Assert.True(result.Success);
        var existing = JsonDocument.Parse(result.Content).RootElement.GetProperty("calendarEvent").GetProperty("description").GetString();
        // The model supplies the complete desired note after reading, not an append command at execution.
        var combined = existing + " Preciso levar documento também.";
        Assert.True((await f.Update.ExecuteAsync(Args(new { eventId = "event1", description = combined }), await f.Context())).Success);
        var pending = Assert.Single(f.Db.PendingCalendarActions);
        Assert.Equal(combined, Payload(pending).Fields["description"]!.GetValue<string>());
        f.Google.ThrowAfterMutation = true;
        Assert.True((await f.Confirm.ExecuteAsync(Empty, await f.Context())).Success);
        Assert.False((await f.Confirm.ExecuteAsync(Empty, await f.Context())).Success);
        Assert.Equal(1, f.Google.Mutations);
        Assert.Equal(combined, f.Google.Events["event1"]["description"]!.GetValue<string>());
    }

    [Fact]
    public async Task MovingEventPreservesFullExistingNoteEvenIfReadWouldBeTruncated()
    {
        using var f = new Fixture();
        var note = new string('a', 10000) + " <b>Conteúdo original</b>";
        f.Google.AddEvent()["description"] = note;
        await f.Observe();
        Assert.True((await f.Update.ExecuteAsync(Args(new { eventId = "event1", start = "2026-10-10T16:00:00-03:00",
            end = "2026-10-10T17:00:00-03:00" }), await f.Context())).Success);
        var action = Assert.Single(f.Db.PendingCalendarActions);
        Assert.False(Payload(action).Fields.ContainsKey("description"));
        Assert.DoesNotContain("anotação", action.HumanSummary);
        Assert.True((await f.Confirm.ExecuteAsync(Empty, await f.Context())).Success);
        Assert.Equal(note, f.Google.Events["event1"]["description"]!.GetValue<string>());
    }

    [Fact]
    public async Task PreparedNoteCannotOverwriteConcurrentNoteChange()
    {
        using var f = new Fixture();
        var item = f.Google.AddEvent();
        item["description"] = "Levar exames.";
        await f.Observe();
        Assert.True((await f.Update.ExecuteAsync(Args(new { eventId = "event1", description = "Levar exames. Levar documento." }), await f.Context())).Success);
        item["description"] = "Anotação atualizada no Google.";
        item["etag"] = "\"v2\"";
        var result = await f.Confirm.ExecuteAsync(Empty, await f.Context());
        Assert.False(result.Success);
        Assert.Contains("calendar_event_changed", result.Content);
        Assert.Equal(0, f.Google.Mutations);
        Assert.Equal("Anotação atualizada no Google.", f.Google.Events["event1"]["description"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(8000, false)]
    [InlineData(8001, true)]
    public async Task GetIndicatesTruncationWithoutChangingStoredNote(int length, bool truncated)
    {
        using var f = new Fixture();
        var original = new string('a', length);
        f.Google.AddEvent()["description"] = original;
        var read = await f.Calendar.GetEventAsync("primary", "event1");
        Assert.Equal(truncated, read.DescriptionTruncated);
        Assert.Equal(truncated ? 8001 : length, read.Description!.Length);
        Assert.Null((await f.Calendar.ListEventsAsync(null, null, null)).Events.Single().Description);
        Assert.Equal(original, f.Google.Events["event1"]["description"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("amend")]
    public async Task ExplicitNullDescriptionIsRejectedRatherThanTreatedAsPreserveOrClear(string operation)
    {
        using var f = new Fixture();
        var args = operation == "create"
            ? Args(new { summary = "Teste", start = Start, end = End, allDay = false, description = (string?)null })
            : operation == "update" ? Args(new { eventId = "event1", description = (string?)null }) : Args(new { description = (string?)null });
        var result = operation switch
        {
            "create" => await f.Create.ExecuteAsync(args, await f.Context()),
            "update" => await f.Update.ExecuteAsync(args, await f.Context()),
            _ => await new CalendarAmendPendingActionTool(f.Db, f.Calendar, f.ToolContext).ExecuteAsync(args, await f.Context())
        };
        Assert.False(result.Success);
        Assert.Contains("invalid_calendar_tool_arguments", result.Content);
        Assert.Empty(f.Db.PendingCalendarActions);
        Assert.Equal(0, f.Google.CalendarRequests);
    }

    [Fact]
    public async Task OversizedNoteIsRejectedWithoutChangingExistingDescription()
    {
        using var f = new Fixture();
        f.Google.AddEvent()["description"] = "Original.";
        await f.Observe();
        var result = await f.Update.ExecuteAsync(Args(new { eventId = "event1", description = new string('a', 8001) }), await f.Context());
        Assert.False(result.Success);
        Assert.Contains("invalid_calendar_tool_arguments", result.Content);
        Assert.Empty(f.Db.PendingCalendarActions);
        Assert.Equal(0, f.Google.Mutations);
        Assert.Equal("Original.", f.Google.Events["event1"]["description"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingNoteCanBeReplacedOrClearedWithoutExternalEffects(bool clear)
    {
        using var f = new Fixture();
        Assert.True((await f.Create.ExecuteAsync(Args(new { summary = "Dentista", start = Start, end = End, allDay = false,
            description = "Levar exames." }), await f.Context())).Success);
        var previous = Assert.Single(f.Db.PendingCalendarActions);
        var amend = new CalendarAmendPendingActionTool(f.Db, f.Calendar, f.ToolContext);
        var expected = clear ? "" : "Levar exames e documento.";
        Assert.True((await amend.ExecuteAsync(Args(new { description = expected }), await f.Context())).Success);
        var active = (await f.Db.GetLatestOpenPendingCalendarActionAsync(f.ConversationId))!;
        Assert.NotEqual(previous.Id, active.Id);
        Assert.Equal(active.Id, previous.SupersededById);
        Assert.Equal("Levar exames.", Payload(previous).Fields["description"]!.GetValue<string>());
        Assert.Equal(expected, Payload(active).Fields["description"]!.GetValue<string>());
        Assert.Equal(DateTimeOffset.Parse(Start), DateTimeOffset.Parse(Payload(active).Fields["start"]!["dateTime"]!.GetValue<string>()));
        Assert.Equal(0, f.Google.Mutations);
        Assert.True((await f.Confirm.ExecuteAsync(Empty, await f.Context())).Success);
        Assert.Equal(expected, f.Google.Events[active.EventId]["description"]!.GetValue<string>());
        Assert.Single(f.Google.Events);
    }
}
