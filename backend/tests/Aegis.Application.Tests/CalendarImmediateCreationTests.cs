using System.Net;
using System.Runtime.CompilerServices;
using Aegis.Application.Models;
using Aegis.Application.Llm;
using Aegis.Application.Observability;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Aegis.Application.Calendar;
using Aegis.Application.Calendar.Tools;
using Aegis.Application.Google;
using Aegis.Application.Tools;
using Aegis.Domain;
using Aegis.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Aegis.Application.Tests;

public sealed partial class CalendarTests
{
    private static JsonElement DirectArgs(string title = "Prova de Grafos") => Args(new
        { summary = title, start = Start, end = End, allDay = false });

    [Fact]
    public async Task ExplicitCreateExecutesAndVerifiesInTheOriginalTurnAndAuditsAuthorization()
    {
        using var f = new Fixture();
        var context = await f.Context("Coloca a prova de Grafos na agenda dia 10 das 14 às 15.");
        var result = await f.Create.ExecuteAsync(DirectArgs(), context);
        Assert.True(result.Success);
        var body = JsonDocument.Parse(result.Content).RootElement;
        Assert.True(body.GetProperty("verified").GetBoolean());
        Assert.Equal("completed", body.GetProperty("status").GetString());
        Assert.Single(f.Google.Events);
        var action = Assert.Single(f.Db.PendingCalendarActions);
        Assert.NotNull(action.ExecutedAt);
        Assert.Null(action.ConfirmedAt); // No later human confirmation was manufactured.
        Assert.Equal(context.UserMessageId, Payload(action).CreationRequest!.UserMessageId);
        var audit = Assert.Single(f.Db.CalendarActionAudits);
        Assert.True(audit.Success);
        Assert.Equal(context.UserMessageId, audit.UserConfirmationMessageId);
        Assert.True(await f.ToolContext.WasObservedAsync(f.ConversationId, "primary", action.EventId));
        Assert.Null(await f.Db.GetLatestOpenPendingCalendarActionAsync(f.ConversationId));
        Assert.Equal("no_pending_calendar_action", (await f.Confirm.ExecuteAsync(Empty, context)).ErrorCode);
    }

    [Fact]
    public async Task DirectAllDayCreationPreservesInclusiveDatesAndReminderPolicy()
    {
        using var f = new Fixture();
        var result = await f.Create.ExecuteAsync(Args(new { summary = "Feriado pessoal", start = "2026-10-10", end = "2026-10-10", allDay = true }), await f.Context("Cria evento de dia inteiro dia 10."));
        Assert.True(result.Success);
        var google = Assert.Single(f.Google.Events).Value;
        Assert.Equal("2026-10-11", google["end"]!["date"]!.ToString());
        Assert.Equal("popup", google["reminders"]!["overrides"]![0]!["method"]!.ToString());
        Assert.NotNull(Assert.Single(f.Db.PendingCalendarActions).ExecutedAt);
    }

    [Theory]
    [InlineData("daily", 1, null, null)]
    [InlineData("weekly", 1, null, null)]
    [InlineData("weekly", 2, null, null)]
    [InlineData("monthly", 1, null, null)]
    [InlineData("yearly", 1, null, null)]
    [InlineData("weekly", 1, 12, null)]
    [InlineData("weekly", 1, null, "2026-12-31")]
    public async Task DirectRecurrenceCreatesOneVerifiedSeriesWithoutConfirmation(string frequency, int interval, int? count, string? until)
    {
        using var f = new Fixture();
        var result = await f.Create.ExecuteAsync(Args(new { summary = "Academia", start = "2026-10-05T18:00:00-03:00", end = "2026-10-05T19:00:00-03:00", allDay = false,
            recurrence = new CalendarRecurrenceData(frequency, interval, frequency == "weekly" ? ["monday", "wednesday", "friday"] : null, count, until) }), await f.Context("Coloca academia na agenda com essa recorrência."));
        Assert.True(result.Success);
        Assert.True(JsonDocument.Parse(result.Content).RootElement.GetProperty("verified").GetBoolean());
        Assert.Single(Assert.Single(f.Google.Events).Value["recurrence"]!.AsArray());
        Assert.Equal(1, f.Google.Mutations);
        Assert.NotNull(Assert.Single(f.Db.PendingCalendarActions).ExecutedAt);
    }

    [Fact]
    public async Task MultipleExplicitCreationsInOneMessageKeepIndependentRecordsAndEvents()
    {
        using var f = new Fixture();
        var context = await f.Context("Coloca as provas de Grafos, SD e Teoria na agenda nos horários informados.");
        foreach (var title in new[] { "Grafos", "Sistemas Distribuídos", "Teoria da Computação" })
            Assert.True((await f.Create.ExecuteAsync(DirectArgs(title), context)).Success);
        Assert.Equal(3, f.Google.Events.Count);
        Assert.Equal(3, f.Db.PendingCalendarActions.Count());
        Assert.Equal(3, f.Db.CalendarActionAudits.Count(a => a.Success));
        Assert.All(f.Db.PendingCalendarActions, action => { Assert.NotNull(action.ExecutedAt); Assert.Null(action.SupersededAt); });
        Assert.Equal(3, f.Db.PendingCalendarActions.Select(a => a.EventId).Distinct().Count());
    }

    [Fact]
    public async Task MultipleCreationsReportPartialSuccessAndDoNotRetryUncertainSiblings()
    {
        using var f = new Fixture();
        var context = await f.Context("Cria Grafos, SD e Teoria na agenda.");
        var first = await f.Create.ExecuteAsync(DirectArgs("Grafos"), context);
        f.Google.ThrowAfterMutation = true;
        f.Google.FailGetsAfterMutation = true;
        var uncertain = await f.Create.ExecuteAsync(DirectArgs("SD"), context);
        f.Google.ThrowAfterMutation = false;
        f.Google.FailGetsAfterMutation = false;
        f.Google.FailureOperation = "events.insert";
        f.Google.FailureStatus = HttpStatusCode.Forbidden;
        var failed = await f.Create.ExecuteAsync(DirectArgs("Teoria"), context);
        Assert.True(first.Success);
        Assert.Equal("calendar_action_outcome_unknown", uncertain.ErrorCode);
        Assert.False(failed.Success);
        Assert.Equal(2, f.Google.Events.Count);
        Assert.Equal(3, f.Db.PendingCalendarActions.Count());
        Assert.Single(f.Db.PendingCalendarActions, a => a.ExecutedAt is not null);
        Assert.Equal("unknown", JsonDocument.Parse(uncertain.Content).RootElement.GetProperty("status").GetString());
        Assert.Equal(2, f.Google.Mutations); // The fixture rejects the third insert before accepting a mutation.
    }

    [Fact]
    public async Task DirectCreationRecoversLostResponseBeforeReportingSuccess()
    {
        using var f = new Fixture();
        f.Google.ThrowAfterMutation = true;
        var result = await f.Create.ExecuteAsync(DirectArgs(), await f.Context("Cria Grafos na agenda."));
        Assert.True(result.Success);
        Assert.True(JsonDocument.Parse(result.Content).RootElement.GetProperty("recoveredAfterFailure").GetBoolean());
        Assert.Single(f.Google.Events);
        Assert.NotNull(Assert.Single(f.Db.PendingCalendarActions).ExecutedAt);
    }

    [Fact]
    public async Task RetryAfterLostVerificationChecksBeforeAnyNewInsertAndSurvivesReload()
    {
        using var f = new Fixture();
        var original = await f.Context("Coloca Grafos na agenda.");
        f.Google.ThrowAfterMutation = true;
        f.Google.FailGetsAfterMutation = true;
        Assert.Equal("calendar_action_outcome_unknown", (await f.Create.ExecuteAsync(DirectArgs(), original)).ErrorCode);
        var id = Assert.Single(f.Db.PendingCalendarActions).EventId;
        f.Db.ChangeTracker.Clear(); // Recover entirely from persisted JSON instead of the original tracked object.
        var retry = new CalendarCreateEventTool(f.Db, f.Calendar, new(f.Db));
        var repeated = await f.Context("Tenta criar o compromisso de Grafos de novo.");
        Assert.Equal("calendar_action_outcome_unknown", (await retry.ExecuteAsync(DirectArgs(), repeated)).ErrorCode);
        Assert.Equal(1, f.Google.Mutations); // Unavailable verification never causes another insert.
        f.Google.FailGetsAfterMutation = false;
        f.Google.ThrowAfterMutation = false;
        Assert.True((await retry.ExecuteAsync(DirectArgs(), repeated)).Success);
        Assert.Single(f.Db.PendingCalendarActions);
        Assert.Equal(id, Assert.Single(f.Google.Events).Key);
        Assert.Equal(1, f.Google.Mutations);
    }

    [Fact]
    public async Task ReplayedToolAndResentRequestAfterCompletedCreationDoNotDuplicate()
    {
        using var f = new Fixture();
        const string text = "Coloca Grafos na agenda.";
        var context = await f.Context(text);
        Assert.True((await f.Create.ExecuteAsync(DirectArgs(), context)).Success);
        Assert.True((await f.Create.ExecuteAsync(DirectArgs(), context)).Success);
        Assert.True((await f.Create.ExecuteAsync(DirectArgs(), await f.Context(text))).Success);
        Assert.Single(f.Google.Events);
        Assert.Single(f.Db.PendingCalendarActions);
        Assert.Equal(1, f.Google.Mutations);
    }

    [Fact]
    public async Task CreationIdsAndRecoveryAreIsolatedBetweenConversations()
    {
        using var f = new Fixture();
        const string text = "Cria Grafos na agenda.";
        Assert.True((await f.Create.ExecuteAsync(DirectArgs(), await f.Context(text))).Success);
        var other = new Conversation("Outra conversa");
        f.Db.AddConversation(other);
        var message = new ChatMessage(other.Id, ChatRoles.User, text);
        f.Db.AddChatMessage(message);
        await f.Db.SaveChangesAsync();
        Assert.True((await f.Create.ExecuteAsync(DirectArgs(), new(other.Id, message.Id, text))).Success);
        Assert.Equal(2, f.Google.Events.Count);
        Assert.Equal(2, f.Db.PendingCalendarActions.Select(a => a.EventId).Distinct().Count());
    }

    [Fact]
    public async Task AuthorizationFailureAndMissingDurationNeverClaimCreation()
    {
        using var f = new Fixture(GoogleScopes.Gmail);
        var result = await f.Create.ExecuteAsync(DirectArgs(), await f.Context("Cria Grafos na agenda."));
        Assert.Equal("calendar_scope_missing", result.ErrorCode);
        Assert.False(result.Success);
        Assert.Empty(f.Google.Events);
        Assert.Empty(f.Db.PendingCalendarActions);
        using var connected = new Fixture();
        var ambiguous = await connected.Create.ExecuteAsync(Args(new { summary = "Grafos", start = Start, allDay = false }), await connected.Context("Cria Grafos às 14, não sei o término."));
        Assert.Equal("invalid_tool_arguments", ambiguous.ErrorCode);
        Assert.Empty(connected.Google.Events);
    }

    [Fact]
    public async Task LegacyProposalIsNotExecutedOrSupersededByAnUnrelatedDirectCreation()
    {
        using var f = new Fixture();
        var legacy = await f.PrepareCreate();
        Assert.True((await f.Create.ExecuteAsync(DirectArgs(), await f.Context("Cria somente Grafos na agenda."))).Success);
        Assert.True(legacy.IsOpen());
        Assert.Null(legacy.ExecutedAt);
        Assert.Null(legacy.SupersededAt);
        Assert.DoesNotContain(legacy.EventId, f.Google.Events.Keys);
        Assert.Single(f.Google.Events);
    }

    [Fact]
    public async Task CancellationAfterDirectInsertPreservesUncertaintyAndRetryDoesNotDuplicate()
    {
        using var f = new Fixture();
        using var cancel = new CancellationTokenSource();
        f.Google.CancelAfterMutation = cancel;
        var context = await f.Context("Cria Grafos na agenda.");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Create.ExecuteAsync(DirectArgs(), context, cancel.Token));
        f.Db.ChangeTracker.Clear();
        Assert.True(Assert.Single(f.Db.PendingCalendarActions).MayHaveAppliedChanges);
        Assert.Single(f.Google.Events);
        f.Google.CancelAfterMutation = null;
        Assert.True((await f.Create.ExecuteAsync(DirectArgs(), await f.Context("Retoma a criação de Grafos."))).Success);
        Assert.Equal(1, f.Google.Mutations);
        Assert.Single(f.Db.PendingCalendarActions);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownCreationCannotMoveToAnotherGoogleAccountOnRetry(bool canonicalNewDestination)
    {
        using var f = new Fixture();
        f.Google.FailGetsAfterMutation = true;
        var context = await f.Context("Cria Grafos na agenda.");
        Assert.Equal("calendar_action_outcome_unknown", (await f.Create.ExecuteAsync(DirectArgs(), context)).ErrorCode);
        var connection = f.Db.EmailAccountConnections.Single();
        connection.Disconnect();
        f.Db.AddEmailAccountConnection(new("gmail", "other@gmail.com", f.Protector.Protect("other-access"), f.Protector.Protect("other-refresh"),
            DateTimeOffset.UtcNow.AddHours(1), GoogleScopes.Combined));
        await f.Db.SaveChangesAsync();
        f.Google.FailGetsAfterMutation = false;
        await f.ToolContext.RememberCalendarsAsync(f.ConversationId, [new("other@gmail.com", "Nova principal", true, "owner")], "calendar_list_calendars");
        var retryArgs = canonicalNewDestination ? Args(new { calendarId = "other@gmail.com", summary = "Prova de Grafos", start = Start, end = End, allDay = false }) : DirectArgs();
        var retry = await f.Create.ExecuteAsync(retryArgs, await f.Context("Tenta criar Grafos novamente."));
        Assert.Equal("calendar_action_outcome_unknown", retry.ErrorCode);
        Assert.Single(f.Db.PendingCalendarActions);
        Assert.Single(f.Google.Events);
        Assert.Equal(1, f.Google.Mutations);
    }

    [Fact]
    public async Task UnknownCreationKeepsOriginalReminderDefaultsOnRetry()
    {
        using var f = new Fixture();
        f.Google.FailGetsAfterMutation = true;
        Assert.Equal("calendar_action_outcome_unknown", (await f.Create.ExecuteAsync(DirectArgs(), await f.Context("Cria Grafos na agenda."))).ErrorCode);
        f.Google.FailGetsAfterMutation = false;
        var changedDefaults = new Aegis.Infrastructure.Calendar.GoogleCalendarService(f.Http, f.Tokens, f.Connection, f.Log,
            Microsoft.Extensions.Options.Options.Create(new Aegis.Infrastructure.Calendar.GoogleCalendarOptions { TimedEventDefaultReminders = [30] }));
        var retry = new CalendarCreateEventTool(f.Db, changedDefaults, f.ToolContext);
        Assert.True((await retry.ExecuteAsync(DirectArgs(), await f.Context("Retoma Grafos."))).Success);
        Assert.Single(f.Db.PendingCalendarActions);
        Assert.Equal(1, f.Google.Mutations);
        Assert.Equal(new[] { 4320, 1440, 240, 60, 0 }, ReminderMinutes(Assert.Single(f.Google.Events).Value));
    }

    [Fact]
    public async Task EquivalentTimestampOffsetAndPrimaryAliasReuseUncertainCreation()
    {
        using var f = new Fixture();
        f.Google.FailGetsAfterMutation = true;
        Assert.Equal("calendar_action_outcome_unknown", (await f.Create.ExecuteAsync(DirectArgs(), await f.Context("Cria Grafos na agenda."))).ErrorCode);
        await f.ToolContext.RememberCalendarsAsync(f.ConversationId, [new("name@gmail.com", "Principal", true, "owner")], "calendar_list_calendars");
        f.Google.FailGetsAfterMutation = false;
        var retry = await f.Create.ExecuteAsync(Args(new { calendarId = "name@gmail.com", summary = "Prova de Grafos", start = "2026-10-10T17:00:00Z", end = "2026-10-10T18:00:00Z", allDay = false }), await f.Context("Tenta de novo na agenda principal."));
        Assert.True(retry.Success);
        Assert.Single(f.Db.PendingCalendarActions);
        Assert.Single(f.Google.Events);
        Assert.Equal(1, f.Google.Mutations);
    }

    [Fact]
    public async Task CancellingUncertainCreationDoesNotEraseItsDeduplicationRecord()
    {
        using var f = new Fixture();
        f.Google.FailGetsAfterMutation = true;
        Assert.Equal("calendar_action_outcome_unknown", (await f.Create.ExecuteAsync(DirectArgs(), await f.Context("Cria Grafos na agenda."))).ErrorCode);
        Assert.True((await new CalendarCancelPendingActionTool(f.Db).ExecuteAsync(Empty, await f.Context("Cancela a tentativa."))).Success);
        f.Google.FailGetsAfterMutation = false;
        Assert.True((await f.Create.ExecuteAsync(DirectArgs(), await f.Context("Retoma Grafos na agenda."))).Success);
        Assert.Single(f.Db.PendingCalendarActions);
        Assert.Single(f.Google.Events);
        Assert.Equal(1, f.Google.Mutations);
    }

    [Fact]
    public async Task ChangedFieldsCannotCreateDuplicateOfAnUncertainTarget()
    {
        using var f = new Fixture();
        f.Google.FailGetsAfterMutation = true;
        Assert.Equal("calendar_action_outcome_unknown", (await f.Create.ExecuteAsync(DirectArgs(), await f.Context("Cria Grafos na agenda."))).ErrorCode);
        var retry = await f.Create.ExecuteAsync(Args(new { summary = "Prova de Grafos", start = Start, end = End, allDay = false, description = "Levar documento" }), await f.Context("Tenta Grafos de novo com anotação."));
        Assert.Equal("calendar_action_outcome_unknown", retry.ErrorCode);
        Assert.Single(f.Db.PendingCalendarActions);
        Assert.Single(f.Google.Events);
        Assert.Equal(1, f.Google.Mutations);
    }

    [Fact]
    public async Task AuthorizationFailureDuringRecoveryKeepsUncertaintyAndOffersOAuthCause()
    {
        using var f = new Fixture();
        f.Google.FailGetsAfterMutation = true;
        Assert.Equal("calendar_action_outcome_unknown", (await f.Create.ExecuteAsync(DirectArgs(), await f.Context("Cria Grafos na agenda."))).ErrorCode);
        f.Db.EmailAccountConnections.Single().Disconnect();
        await f.Db.SaveChangesAsync();
        var retry = await f.Create.ExecuteAsync(DirectArgs(), await f.Context("Retoma Grafos."));
        Assert.Equal("calendar_not_connected", retry.ErrorCode);
        var body = JsonDocument.Parse(retry.Content).RootElement;
        Assert.Equal("unknown", body.GetProperty("status").GetString());
        Assert.Contains("calendar_create_connect_link", body.GetProperty("authorizationMessage").GetString());
        Assert.Equal(1, f.Google.Mutations);
        Assert.Single(f.Db.PendingCalendarActions);
    }

    [Fact]
    public async Task BatchCreatesFiveEventsInOneToolCallAndRetryDoesNotDuplicate()
    {
        using var f = new Fixture();
        var args = Args(new { events = Enumerable.Range(1, 5).Select(i => DirectArgs("Prova " + i)).ToArray() });
        var context = await f.Context("Cria as cinco provas na agenda.");
        var result = await f.Create.ExecuteAsync(args, context);
        Assert.True(result.Success);
        var body = JsonDocument.Parse(result.Content).RootElement;
        Assert.Equal("completed", body.GetProperty("status").GetString());
        Assert.Equal(5, body.GetProperty("succeeded").GetInt32());
        Assert.Equal(0, body.GetProperty("failed").GetInt32());
        Assert.Equal(0, body.GetProperty("unknown").GetInt32());
        Assert.Equal(5, f.Google.Events.Count);
        Assert.True((await f.Create.ExecuteAsync(args, await f.Context("Cria as cinco provas na agenda."))).Success);
        Assert.Equal(5, f.Google.Mutations);
        Assert.Equal(5, f.Db.PendingCalendarActions.Count());
    }

    [Fact]
    public async Task BatchSeparatesVerifiedFailureAndUnknownAndDoesNotRepeatUncertainCreation()
    {
        using var f = new Fixture();
        f.Google.FailGetsAfterMutation = true;
        f.Google.MutationCountBeforeFailedGets = 1;
        var args = Args(new { events = new[] { DirectArgs("Grafos"), DirectArgs("SD"), Args(new { summary = "Teoria", start = Start, allDay = false }) } });
        var context = await f.Context("Cria Grafos, SD e Teoria na agenda.");
        var result = await f.Create.ExecuteAsync(args, context);
        Assert.False(result.Success);
        var body = JsonDocument.Parse(result.Content).RootElement;
        Assert.Equal("partial", body.GetProperty("status").GetString());
        Assert.False(body.GetProperty("verified").GetBoolean());
        Assert.Equal(1, body.GetProperty("succeeded").GetInt32());
        Assert.Equal(1, body.GetProperty("failed").GetInt32());
        Assert.Equal(1, body.GetProperty("unknown").GetInt32());
        var items = body.GetProperty("results").EnumerateArray().ToArray();
        Assert.True(items[0].GetProperty("success").GetBoolean());
        Assert.Equal("calendar_action_outcome_unknown", items[1].GetProperty("errorCode").GetString());
        Assert.Equal("invalid_tool_arguments", items[2].GetProperty("errorCode").GetString());
        Assert.Equal(2, f.Google.Mutations);
        Assert.Equal("calendar_action_outcome_unknown", (await f.Create.ExecuteAsync(DirectArgs("SD"), context)).ErrorCode);
        Assert.Equal(2, f.Google.Mutations); // Same-turn uncertainty cannot send another insert.
        f.Google.FailGetsAfterMutation = false;
        Assert.True((await f.Create.ExecuteAsync(DirectArgs("SD"), await f.Context("Retoma somente SD."))).Success);
        Assert.Equal(2, f.Google.Mutations);
        Assert.Equal(2, f.Google.Events.Count);
        var resumed = await f.Create.ExecuteAsync(args, await f.Context("Tenta de novo esse lote."));
        Assert.False(resumed.Success); // The missing-duration item still needs clarification.
        var resumedBody = JsonDocument.Parse(resumed.Content).RootElement;
        Assert.Equal(2, resumedBody.GetProperty("succeeded").GetInt32());
        Assert.Equal(1, resumedBody.GetProperty("failed").GetInt32());
        Assert.Equal(0, resumedBody.GetProperty("unknown").GetInt32());
        Assert.Equal(2, f.Google.Mutations);
        Assert.Equal(2, f.Google.Events.Count);
        Assert.Equal(2, f.Db.PendingCalendarActions.Count());
    }

    [Fact]
    public async Task InvalidOrOversizedBatchNeverSendsAnyMutation()
    {
        using var f = new Fixture();
        var context = await f.Context("Cria eventos na agenda.");
        var oversized = Args(new { events = Enumerable.Range(1, 11).Select(i => DirectArgs("Evento " + i)).ToArray() });
        var mixed = Args(new { events = new[] { DirectArgs() }, summary = "Não misturar" });
        var nested = Args(new { events = new[] { Args(new { events = new[] { DirectArgs() } }) } });
        foreach (var args in new[] { oversized, mixed, nested, Args(new { events = Array.Empty<JsonElement>() }) })
            Assert.False((await f.Create.ExecuteAsync(args, context)).Success);
        Assert.Empty(f.Db.PendingCalendarActions);
        Assert.Equal(0, f.Google.Mutations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FiveEventBatchFitsExistingTurnBudgetAfterThreeQueries(bool streaming)
    {
        using var f = new Fixture();
        var batch = Args(new { events = Enumerable.Range(1, 5).Select(i => DirectArgs("Prova " + i)).ToArray() });
        var context = await f.Context("Cria as cinco provas informadas na agenda.");
        var model = new BatchModel(batch);
        using var metrics = new AegisMetrics();
        var loop = new AegisToolLoop(model, new AegisToolRegistry([new CalendarGetStatusTool(f.Calendar), f.Create]), NullLogger<AegisToolLoop>.Instance, metrics);
        var request = new ModelRequest("identity", context.UserContent);
        if (streaming)
        {
            var chunks = new List<ModelStreamChunk>();
            await foreach (var chunk in loop.StreamAsync(request, context)) chunks.Add(chunk);
            Assert.Contains(chunks, c => c.ToolStatus?.Message == "Criando evento…");
            Assert.Contains(chunks, c => c.ToolStatus?.Message == "Evento criado" && c.ToolStatus.State == "completed");
            Assert.True(chunks[^1].IsDone);
        }
        else Assert.Equal("Cinco eventos criados.", (await loop.RunAsync(request, context)).Content);
        Assert.Equal(5, model.Calls);
        Assert.Equal(5, f.Google.Events.Count);
        Assert.Equal(5, f.Db.PendingCalendarActions.Count(a => a.ExecutedAt != null));
    }

    private sealed class BatchModel(JsonElement batch) : IAegisModelClient
    {
        public int Calls;
        public Task<ModelToolResponse> RespondWithToolsAsync(ModelToolRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            var name = Calls < 4 ? "calendar_get_status" : "calendar_create_event";
            var args = Calls == 4 ? batch : Empty;
            IReadOnlyList<ModelToolCall> calls = Calls < 5 ? [new("call_" + Calls, name, args)] : [];
            if (Calls == 5) Assert.Empty(request.Tools);
            IReadOnlyList<JsonElement> output = Calls < 5
                ? [Args(new { type = "function_call", call_id = "call_" + Calls, name, arguments = args.GetRawText() })] : [];
            return Task.FromResult(new ModelToolResponse(Calls == 5 ? "Cinco eventos criados." : "", "fake", "fake", ModelPurpose.Chat,
                calls, output, null, null, new LlmRequestAuditData("fake", "fake", true, 1, "{}", 200, "{}", null, null)));
        }
        public async IAsyncEnumerable<ModelToolStreamChunk> RespondWithToolsStreamAsync(ModelToolRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var result = await RespondWithToolsAsync(request, cancellationToken);
            if (!string.IsNullOrEmpty(result.Content)) yield return new(result.Content, false, [], []);
            yield return new(null, true, result.ToolCalls, result.OutputItems, Provider: "fake", Model: "fake", Purpose: ModelPurpose.Chat, AuditData: result.AuditData);
        }
    }

}
