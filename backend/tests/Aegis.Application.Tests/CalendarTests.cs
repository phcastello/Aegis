using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aegis.Application.Calendar;
using Aegis.Application.Calendar.Tools;
using Aegis.Application.Email;
using Aegis.Application.Google;
using Aegis.Application.Tools;
using Aegis.Domain;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Calendar;
using Aegis.Infrastructure.Email;
using Aegis.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aegis.Application.Tests;

public sealed partial class CalendarTests
{
    private const string Start = "2026-10-10T14:00:00-03:00";
    private const string End = "2026-10-10T15:00:00-03:00";
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement Empty => Args(new { });

    [Fact]
    public async Task OldGmailOnlyConnectionStillReadsGmailAndReportsMissingCalendarScope()
    {
        using var f = new Fixture(GoogleScopes.Gmail);
        var gmail = new GmailService(f.Http, Options.Create(new GmailOptions()), f.Tokens);
        Assert.Empty((await gmail.SearchEmailsAsync("test")).Emails);
        var status = await f.Calendar.GetStatusAsync();
        Assert.True(status.IsConnected);
        Assert.Equal("name@gmail.com", status.EmailAddress);
        Assert.False(status.CalendarAuthorized);
        Assert.Equal("calendar_scope_missing", status.AuthorizationState);
        Assert.Equal("calendar_scope_missing", (await Assert.ThrowsAsync<CalendarException>(() => f.Calendar.ListEventsAsync(null, null, null))).Code);
        Assert.Null(f.Db.EmailAccountConnections.Single().DisconnectedAt);
    }

    [Fact]
    public async Task ReauthorizationAddsCalendarToSameConnectionAndPreservesRefreshToken()
    {
        using var f = new Fixture(GoogleScopes.Gmail);
        var old = f.Db.EmailAccountConnections.Single();
        var originalId = old.Id;
        var refresh = old.RefreshTokenEncrypted;
        f.Google.OAuthScopes = GoogleScopes.Combined;
        var url = (await f.Connection.CreateAuthorizationUrlAsync()).AuthorizationUrl;
        Assert.Contains(Uri.EscapeDataString(GoogleScopes.Calendar), url);
        Assert.Contains("include_granted_scopes=true", url);
        var state = Uri.UnescapeDataString(new Uri(url).Query.TrimStart('?').Split('&').Single(p => p.StartsWith("state="))[6..]);
        await f.Connection.HandleOAuthCallbackAsync("code", state);
        Assert.Single(f.Db.EmailAccountConnections);
        Assert.Equal(originalId, old.Id);
        Assert.Equal(refresh, old.RefreshTokenEncrypted);
        Assert.True(GoogleScopes.Contains(old.Scopes, GoogleScopes.Gmail));
        Assert.True((await f.Calendar.GetStatusAsync()).CalendarAuthorized);
        Assert.Empty((await new GmailService(f.Http, Options.Create(new GmailOptions()), f.Tokens).SearchEmailsAsync("test")).Emails);
    }

    [Fact]
    public async Task ReauthorizationCannotMixTokensFromDifferentAccounts()
    {
        using var f = new Fixture(GoogleScopes.Gmail);
        var old = f.Db.EmailAccountConnections.Single();
        var original = old.AccessTokenEncrypted;
        f.Google.ProfileEmail = "other@gmail.com";
        var url = (await f.Connection.CreateAuthorizationUrlAsync()).AuthorizationUrl;
        var state = Uri.UnescapeDataString(new Uri(url).Query.TrimStart('?').Split('&').Single(p => p.StartsWith("state="))[6..]);
        Assert.Equal("google_account_mismatch", (await Assert.ThrowsAsync<EmailConnectionException>(() => f.Connection.HandleOAuthCallbackAsync("code", state))).Code);
        Assert.Equal(original, old.AccessTokenEncrypted);
    }

    [Fact]
    public async Task SharedRefreshIsUsedByCalendarThenGmailWithoutSecondRefresh()
    {
        using var f = new Fixture(expired: true);
        Assert.Empty((await f.Calendar.ListEventsAsync(null, null, null)).Events);
        Assert.Empty((await new GmailService(f.Http, Options.Create(new GmailOptions()), f.Tokens).SearchEmailsAsync("test")).Emails);
        Assert.Equal(1, f.Google.Refreshes);
        Assert.Equal("renewed", f.Protector.Unprotect(f.Db.EmailAccountConnections.Single().AccessTokenEncrypted));
        Assert.Equal("refresh", f.Protector.Unprotect(f.Db.EmailAccountConnections.Single().RefreshTokenEncrypted!));
    }

    [Fact]
    public async Task InvalidRefreshDisconnectsSharedConnectionButTemporaryFailureDoesNot()
    {
        using var f = new Fixture(expired: true);
        f.Google.RefreshStatus = HttpStatusCode.ServiceUnavailable;
        await Assert.ThrowsAsync<HttpRequestException>(() => f.Tokens.GetAccessTokenAsync(GoogleScopes.Calendar));
        Assert.Null(f.Db.EmailAccountConnections.Single().DisconnectedAt);
        f.Google.RefreshStatus = HttpStatusCode.BadRequest;
        Assert.Equal("calendar_not_connected", (await Assert.ThrowsAsync<CalendarException>(() => f.Calendar.ListEventsAsync(null, null, null))).Code);
        Assert.NotNull(f.Db.EmailAccountConnections.Single().DisconnectedAt);
    }

    [Fact]
    public async Task IntervalTextSearchTimezoneAndDefensiveLimitReachGoogle()
    {
        using var f = new Fixture();
        for (var i = 0; i < 55; i++) f.Google.AddEvent("e" + i)["summary"] = "Grafos";
        var result = await f.Calendar.ListEventsAsync("2026-10-10T00:00:00-03:00", "2026-10-11T00:00:00-03:00", "Grafos", 100);
        Assert.Equal(50, result.Events.Count);
        Assert.True(result.HasMore);
        Assert.Equal("America/Sao_Paulo", result.Events[0].TimeZone);
        var query = Uri.UnescapeDataString(f.Google.LastListQuery!);
        Assert.Contains("singleEvents=true", query);
        Assert.Contains("orderBy=startTime", query);
        Assert.Contains("timeMin=2026-10-10T00:00:00.0000000-03:00", query);
        Assert.Contains("timeMax=2026-10-11T00:00:00.0000000-03:00", query);
        Assert.Contains("q=Grafos", query);
        Assert.Contains("maxResults=50", query);
        Assert.Contains("timeZone=America/Sao_Paulo", query);
    }

    [Fact]
    public async Task ListDoesNotDumpRawResourcesAndDetailsAreBounded()
    {
        using var f = new Fixture();
        var item = f.Google.AddEvent();
        item["description"] = new string('a', 10000);
        Assert.Null((await f.Calendar.ListEventsAsync(null, null, null)).Events.Single().Description);
        Assert.Equal(8001, (await f.Calendar.GetEventAsync("primary", "event1")).Description!.Length);
        Assert.Equal("calendar_event_not_found", (await Assert.ThrowsAsync<CalendarException>(() => f.Calendar.GetEventAsync("primary", "missing"))).Code);
    }

    [Fact]
    public async Task AllDayUsesInclusiveToolDatesAndExclusiveGoogleEnd()
    {
        using var f = new Fixture();
        var prepared = await f.Calendar.PrepareAsync(CalendarActionTypes.Create, "primary", "abcde", new("Prova", "2026-10-10", "2026-10-10", true));
        Assert.Equal("2026-10-11", prepared.Fields["end"]!["date"]!.GetValue<string>());
        await f.Calendar.ExecuteAsync(CalendarActionTypes.Create, "primary", "abcde", prepared);
        var result = await f.Calendar.GetEventAsync("primary", "abcde");
        Assert.True(result.AllDay);
        Assert.Equal("2026-10-10", result.Start);
        Assert.Equal("2026-10-10", result.End);
        Assert.True(await f.Calendar.VerifyAsync(CalendarActionTypes.Create, "primary", "abcde", prepared));
    }

    [Theory]
    [InlineData("2026-10-10T14:00:00", End, false)]
    [InlineData(Start, "2026-10-10T13:00:00-03:00", false)]
    [InlineData("2026-10-10", "2026-10-09", true)]
    [InlineData(Start, End, true)]
    public async Task InvalidDatesAreRecoverableWithoutSendingMutations(string start, string end, bool allDay)
    {
        using var f = new Fixture();
        var result = await f.Create.ExecuteAsync(Args(new { summary = "Dentista", start, end, allDay }), await f.Context());
        Assert.False(result.Success);
        Assert.Contains("invalid_calendar_tool_arguments", result.Content);
        Assert.Empty(f.Db.PendingCalendarActions);
        Assert.Equal(0, f.Google.Mutations);
    }

    [Fact]
    public async Task CreateWithoutDurationFailsAndTimezoneIsValidated()
    {
        using var f = new Fixture();
        var result = await f.Create.ExecuteAsync(Args(new { summary = "Dentista", start = Start, allDay = false }), await f.Context());
        Assert.False(result.Success);
        Assert.Empty(f.Db.PendingCalendarActions);
        await Assert.ThrowsAsync<CalendarException>(() => f.Calendar.PrepareAsync(CalendarActionTypes.Create, "primary", "abcde", new("Dentista", Start, End, false, "Invalid/Zone")));
    }

    [Fact]
    public async Task ObservedEventCanBeReadAndChangedButInventedOrExpiredIdsCannot()
    {
        using var f = new Fixture();
        f.Google.AddEvent();
        var ctx = await f.Context();
        var get = new CalendarGetEventTool(f.Calendar, f.ToolContext);
        Assert.False((await get.ExecuteAsync(Args(new { eventId = "event1" }), ctx)).Success);
        Assert.True((await new CalendarListEventsTool(f.Calendar, f.ToolContext).ExecuteAsync(Empty, ctx)).Success);
        Assert.True((await get.ExecuteAsync(Args(new { eventId = "event1" }), ctx)).Success);
        Assert.False((await f.Update.ExecuteAsync(Args(new { eventId = "invented", summary = "Changed" }), ctx)).Success);
        Assert.True((await f.Update.ExecuteAsync(Args(new { eventId = "event1", summary = "Changed" }), ctx)).Success);
        var expiredKey = f.Db.ToolContextEntries.First().Key;
        foreach (var entry in f.Db.ToolContextEntries) entry.Replace();
        f.Db.AddToolContextEntry(new(f.ConversationId, CalendarToolContextService.Scope, CalendarToolContextService.EntryType, expiredKey, "{}", "calendar_list_events", DateTimeOffset.UtcNow.AddMinutes(-1)));
        await f.Db.SaveChangesAsync();
        Assert.False((await f.Update.ExecuteAsync(Args(new { eventId = "event1", summary = "Changed" }), ctx)).Success);
        Assert.False((await new CalendarDeleteEventTool(f.Db, f.Calendar, f.ToolContext).ExecuteAsync(Args(new { eventId = "event1" }), ctx)).Success);
        Assert.False(await f.ToolContext.WasObservedAsync(Guid.NewGuid(), "primary", "event1"));
        Assert.Equal(0, f.Google.Mutations);
    }

    [Theory]
    [InlineData(CalendarActionTypes.Create)]
    [InlineData(CalendarActionTypes.Update)]
    [InlineData(CalendarActionTypes.Delete)]
    public async Task EveryMutationPreparesOnlyAndRequiresLaterTurn(string type)
    {
        using var f = new Fixture();
        f.Google.AddEvent();
        await f.Observe();
        var ctx = await f.Context("sim");
        var tool = type switch { CalendarActionTypes.Create => (IAegisTool)f.Create, CalendarActionTypes.Update => f.Update, _ => new CalendarDeleteEventTool(f.Db, f.Calendar, f.ToolContext) };
        var args = type == CalendarActionTypes.Create ? Args(new { summary = "Dentista", start = Start, end = End, allDay = false }) :
            type == CalendarActionTypes.Update ? Args(new { eventId = "event1", summary = "Changed" }) : Args(new { eventId = "event1" });
        Assert.True((await tool.ExecuteAsync(args, ctx)).Success);
        Assert.Equal(0, f.Google.Mutations);
        var action = Assert.Single(f.Db.PendingCalendarActions);
        Assert.Equal(type, action.ActionType);
        Assert.False((await f.Confirm.ExecuteAsync(Empty, ctx)).Success);
        Assert.Equal(0, f.Google.Mutations);
        Assert.True((await f.Confirm.ExecuteAsync(Empty, await f.Context("sim"))).Success);
        Assert.NotNull(action.ExecutedAt);
        Assert.Single(f.Db.CalendarActionAudits.Where(a => a.Success));
    }

    [Theory]
    [InlineData("sim")]
    [InlineData("confirmo")]
    [InlineData("pode fazer")]
    [InlineData("pode executar")]
    [InlineData("confirmado!")]
    public async Task ConfirmationToolDoesNotClassifyUserText(string confirmation)
    {
        using var f = new Fixture();
        await f.PrepareCreate();
        Assert.True((await f.Confirm.ExecuteAsync(Empty, await f.Context(confirmation))).Success);
    }

    [Theory]
    [InlineData("Manda bala")]
    [InlineData("Pode")]
    [InlineData("É isso aí")]
    [InlineData("texto sem palavras reservadas")]
    public async Task ModelSelectedConfirmationExecutesWithoutKeywordGate(string confirmation)
    {
        using var f = new Fixture();
        await f.PrepareCreate();
        var context = await f.Context(confirmation);
        Assert.True((await f.Confirm.ExecuteAsync(Empty, context with { UserContent = "outro texto" })).Success);
        Assert.Equal(1, f.Google.Mutations);
        Assert.Equal(context.UserMessageId, f.Db.CalendarActionAudits.Single().UserConfirmationMessageId);
        Assert.Equal("no_pending_calendar_action", (await f.Confirm.ExecuteAsync(Empty, await f.Context(confirmation))).ErrorCode);
        Assert.Equal(1, f.Google.Mutations);
    }

    [Fact]
    public async Task CancelAndExpirationPreventExecution()
    {
        using var f = new Fixture();
        var pending = await f.PrepareCreate();
        Assert.True((await new CalendarCancelPendingActionTool(f.Db).ExecuteAsync(Empty, await f.Context("cancela"))).Success);
        Assert.NotNull(pending.CancelledAt);
        Assert.Equal("no_pending_calendar_action", (await f.Confirm.ExecuteAsync(Empty, await f.Context("sim"))).ErrorCode);
        f.Db.AddPendingCalendarAction(new(f.ConversationId, CalendarActionTypes.Create, "abcde", pending.PayloadJson, pending.HumanSummary, DateTimeOffset.UtcNow.AddMinutes(-1)));
        await f.Db.SaveChangesAsync();
        Assert.Equal("no_pending_calendar_action", (await f.Confirm.ExecuteAsync(Empty, await f.Context("confirmo"))).ErrorCode);
        Assert.Equal(0, f.Google.Mutations);
    }

    [Fact]
    public async Task PreparingReplacementClosesPreviousAction()
    {
        using var f = new Fixture();
        var first = await f.PrepareCreate();
        var second = await f.PrepareCreate();
        Assert.NotNull(first.SupersededAt);
        Assert.Equal(second.Id, first.SupersededById);
        Assert.Null(first.CancelledAt);
        Assert.True((await f.Confirm.ExecuteAsync(Empty, await f.Context("sim"))).Success);
        Assert.Null(await f.Db.GetLatestOpenPendingCalendarActionAsync(f.ConversationId));
        Assert.Single(f.Google.Events);
        Assert.Contains(second.EventId, f.Google.Events.Keys);
    }

    [Fact]
    public async Task CreateAmbiguousResponseIsRecoveredByReadingTheFixedId()
    {
        using var f = new Fixture();
        var action = await f.PrepareCreate();
        Assert.Matches("^[0-9a-v]{5,1024}$", action.EventId);
        f.Google.ThrowAfterMutation = true;
        var result = await f.Confirm.ExecuteAsync(Empty, await f.Context("sim"));
        Assert.True(result.Success);
        Assert.Contains("\"recoveredAfterFailure\":true", result.Content);
        Assert.Single(f.Google.Events);
        Assert.NotNull(action.ExecutedAt);
    }

    [Fact]
    public async Task CreateRetryAfterUnavailableVerificationDoesNotDuplicate()
    {
        using var f = new Fixture();
        var action = await f.PrepareCreate();
        f.Google.ThrowAfterMutation = true;
        f.Google.FailGetsAfterMutation = true;
        var first = await f.Confirm.ExecuteAsync(Empty, await f.Context("sim"));
        Assert.Equal("calendar_action_outcome_unknown", first.ErrorCode);
        Assert.True(action.IsOpen());
        Assert.True(action.MayHaveAppliedChanges);
        f.Google.FailGetsAfterMutation = false;
        f.Google.ThrowAfterMutation = false;
        Assert.True((await f.Confirm.ExecuteAsync(Empty, await f.Context("sim"))).Success);
        Assert.Single(f.Google.Events);
        Assert.Equal(action.EventId, f.Google.Events.Single().Key);
        Assert.Equal(2, f.Google.Mutations); // POST 409 on retry, GET proves original insert.
    }

    [Fact]
    public async Task UpdateGetsFullResourcePreservesUnknownFieldsUsesETagAndVerifies()
    {
        using var f = new Fixture();
        var original = f.Google.AddEvent();
        original["attendees"] = JsonNode.Parse("""[{"email":"other@example.test","responseStatus":"accepted"}]""");
        original["conferenceData"] = JsonNode.Parse("""{"conferenceId":"kept"}""");
        original["extendedProperties"] = JsonNode.Parse("""{"private":{"custom":"kept"}}""");
        await f.Observe();
        Assert.True((await f.Update.ExecuteAsync(Args(new { eventId = "event1", start = "2026-10-10T16:00:00-03:00", end = "2026-10-10T17:00:00-03:00" }), await f.Context())).Success);
        var getBefore = f.Google.Gets;
        Assert.True((await f.Confirm.ExecuteAsync(Empty, await f.Context("confirmo"))).Success);
        var updated = f.Google.Events["event1"];
        Assert.Equal("kept", updated["conferenceData"]!["conferenceId"]!.ToString());
        Assert.Equal("accepted", updated["attendees"]![0]!["responseStatus"]!.ToString());
        Assert.Equal("kept", updated["extendedProperties"]!["private"]!["custom"]!.ToString());
        Assert.Equal("Evento", updated["summary"]!.ToString());
        Assert.Equal("\"v1\"", f.Google.LastIfMatch);
        Assert.Contains("conferenceDataVersion=1", f.Google.LastMutationQuery);
        Assert.Contains("supportsAttachments=true", f.Google.LastMutationQuery);
        Assert.True(f.Google.Gets >= getBefore + 2); // Preflight GET and post-state GET.
    }

    [Fact]
    public async Task ConcurrentEditDoesNotGetOverwrittenOrDeleted()
    {
        using var f = new Fixture();
        var original = f.Google.AddEvent();
        await f.Observe();
        await f.Update.ExecuteAsync(Args(new { eventId = "event1", summary = "Changed" }), await f.Context());
        original["etag"] = "\"v2\"";
        original["summary"] = "Someone else's edit";
        Assert.Equal("calendar_event_changed", (await f.Confirm.ExecuteAsync(Empty, await f.Context("sim"))).ErrorCode);
        Assert.Equal("Someone else's edit", f.Google.Events["event1"]["summary"]!.ToString());
        Assert.Equal(0, f.Google.Mutations);
    }

    [Fact]
    public async Task UpdateResponseWithoutActualChangeDoesNotReportSuccess()
    {
        using var f = new Fixture();
        f.Google.AddEvent();
        await f.Observe();
        await f.Update.ExecuteAsync(Args(new { eventId = "event1", summary = "Changed" }), await f.Context());
        f.Google.IgnorePut = true;
        Assert.Equal("calendar_action_outcome_unknown", (await f.Confirm.ExecuteAsync(Empty, await f.Context("sim"))).ErrorCode);
        Assert.Null(f.Db.PendingCalendarActions.Single().ExecutedAt);
    }

    [Fact]
    public async Task DeleteFinal404AndRepeatedDeleteAreSuccess()
    {
        using var f = new Fixture();
        f.Google.AddEvent();
        await f.Observe();
        await new CalendarDeleteEventTool(f.Db, f.Calendar, f.ToolContext).ExecuteAsync(Args(new { eventId = "event1" }), await f.Context());
        f.Google.ThrowAfterMutation = true;
        Assert.True((await f.Confirm.ExecuteAsync(Empty, await f.Context("sim"))).Success);
        var action = f.Db.PendingCalendarActions.Single();
        var payload = JsonSerializer.Deserialize<CalendarActionPayload>(action.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        await f.Calendar.ExecuteAsync(CalendarActionTypes.Delete, "primary", action.EventId, payload);
        Assert.True(await f.Calendar.VerifyAsync(CalendarActionTypes.Delete, "primary", action.EventId, payload));
        Assert.Empty(f.Google.Events);
        Assert.Equal(1, f.Google.Mutations);
    }

    [Fact]
    public async Task CancelledHttpAfterSendPersistsPossibleEffectsAndCancellationDoesNotClaimRollback()
    {
        using var f = new Fixture();
        var action = await f.PrepareCreate();
        using var source = new CancellationTokenSource();
        f.Google.CancelAfterMutation = source;
        var confirmContext = await f.Context("sim");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Confirm.ExecuteAsync(Empty, confirmContext, source.Token));
        f.Db.ChangeTracker.Clear();
        Assert.True(f.Db.PendingCalendarActions.Single().MayHaveAppliedChanges);
        Assert.Single(f.Google.Events);
        var cancel = await new CalendarCancelPendingActionTool(f.Db).ExecuteAsync(Empty, await f.Context("cancela"));
        Assert.True(cancel.Success);
        Assert.Contains("não foram revertidas", JsonDocument.Parse(cancel.Content).RootElement.GetProperty("userMessage").GetString());
        Assert.DoesNotContain("Nada foi alterado", cancel.Content);
        Assert.Null(f.Db.PendingCalendarActions.Single().ExecutedAt);
        Assert.Contains(f.Db.CalendarActionAudits, a => !a.Success);
    }

    [Fact]
    public async Task ConfirmationExecutesOnlyTheIntegrationSelectedByTheModel()
    {
        using var f = new Fixture();
        await f.PrepareCreate();
        f.Db.AddPendingEmailAction(new(f.ConversationId, EmailActionTypes.MarkRead, "[\"email1\"]", "Marcar email lido", DateTimeOffset.UtcNow.AddMinutes(10)));
        await f.Db.SaveChangesAsync();
        Assert.True((await f.Confirm.ExecuteAsync(Empty, await f.Context("Pode criar o evento"))).Success);
        Assert.Equal(1, f.Google.Mutations);
        Assert.True(f.Db.PendingEmailActions.Single().IsOpen());
    }


    [Fact]
    public async Task UpdateAndDeleteRetriesRecoverAfterLostVerification()
    {
        using var f = new Fixture();
        f.Google.AddEvent();
        await f.Observe();
        await f.Update.ExecuteAsync(Args(new { eventId = "event1", summary = "Changed" }), await f.Context());
        f.Google.FailGetsAfterMutation = true;
        Assert.Equal("calendar_action_outcome_unknown", (await f.Confirm.ExecuteAsync(Empty, await f.Context("sim"))).ErrorCode);
        f.Google.FailGetsAfterMutation = false;
        Assert.True((await f.Confirm.ExecuteAsync(Empty, await f.Context("sim"))).Success);
        Assert.Equal(1, f.Google.Mutations);
        await new CalendarDeleteEventTool(f.Db, f.Calendar, f.ToolContext).ExecuteAsync(Args(new { eventId = "event1" }), await f.Context());
        f.Google.MutationCountBeforeFailedGets = 1;
        f.Google.FailGetsAfterMutation = true;
        Assert.Equal("calendar_action_outcome_unknown", (await f.Confirm.ExecuteAsync(Empty, await f.Context("sim"))).ErrorCode);
        f.Google.FailGetsAfterMutation = false;
        Assert.True((await f.Confirm.ExecuteAsync(Empty, await f.Context("sim"))).Success);
        Assert.Equal(2, f.Google.Mutations);
        Assert.Empty(f.Google.Events);
    }

    [Fact]
    public async Task CancellationDuringVerificationPreservesPossibleEffects()
    {
        using var f = new Fixture();
        await f.PrepareCreate();
        using var source = new CancellationTokenSource();
        f.Google.CancelOnGetAfterMutation = source;
        var context = await f.Context("sim");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Confirm.ExecuteAsync(Empty, context, source.Token));
        Assert.True(f.Db.PendingCalendarActions.Single().MayHaveAppliedChanges);
        Assert.Null(f.Db.PendingCalendarActions.Single().ExecutedAt);
        Assert.Single(f.Google.Events);
    }

    [Fact]
    public async Task VerificationTimeoutReportsUnknownOutcome()
    {
        using var f = new Fixture();
        await f.PrepareCreate();
        f.Google.TimeoutGetsAfterMutation = true;
        Assert.Equal("calendar_action_outcome_unknown", (await f.Confirm.ExecuteAsync(Empty, await f.Context("sim"))).ErrorCode);
        Assert.True(f.Db.PendingCalendarActions.Single().MayHaveAppliedChanges);
        Assert.Null(f.Db.PendingCalendarActions.Single().ExecutedAt);
    }

    [Fact]
    public async Task LongGoogleIdsFitExistingContextStorageAndRemainResolvable()
    {
        using var f = new Fixture();
        var id = new string('a', 1024);
        var item = f.Google.AddEvent(id);
        var eventData = await f.Calendar.GetEventAsync("primary", id);
        await f.ToolContext.RememberAsync(f.ConversationId, [eventData], "calendar_list_events");
        Assert.True(await f.ToolContext.WasObservedAsync(f.ConversationId, "primary", id));
        Assert.True(f.Db.ToolContextEntries.Single().Key.Length <= 200);
        Assert.Contains(id, f.Db.ToolContextEntries.Single().DataJson);
    }

    [Fact]
    public async Task InvalidIntervalAndWrongToolArgumentTypesAreRecoverable()
    {
        using var f = new Fixture();
        var list = new CalendarListEventsTool(f.Calendar, f.ToolContext);
        Assert.Equal("invalid_tool_arguments", (await list.ExecuteAsync(Args(new { timeMin = End, timeMax = Start }), await f.Context())).ErrorCode);
        Assert.Equal("invalid_tool_arguments", (await list.ExecuteAsync(Args(new { limit = "all" }), await f.Context())).ErrorCode);
        Assert.Equal("invalid_tool_arguments", (await f.Create.ExecuteAsync(Args(new { summary = "A", start = Start, end = End, allDay = false, attendees = new[] { "anyone" } }), await f.Context())).ErrorCode);
        Assert.Equal(0, f.Google.Mutations);
    }

    [Fact]
    public async Task MissingConnectionAndRemoteApiErrorsAreSpecificAndSanitized()
    {
        using var f = new Fixture();
        var list = new CalendarListEventsTool(f.Calendar, f.ToolContext);
        f.Db.EmailAccountConnections.Single().Disconnect();
        await f.Db.SaveChangesAsync();
        var result = await list.ExecuteAsync(Empty, await f.Context());
        Assert.Equal("calendar_not_connected", result.ErrorCode);
        Assert.DoesNotContain("access", result.Content);
    }

    [Fact]
    public async Task CalendarStatusAndConnectLinkUseTheExistingGoogleConnection()
    {
        using var f = new Fixture(GoogleScopes.Gmail);
        var status = await new CalendarGetStatusTool(f.Calendar).ExecuteAsync(Empty, await f.Context());
        Assert.True(status.Success);
        Assert.Contains("calendar_scope_missing", status.Content);
        var link = await new CalendarCreateConnectLinkTool(f.Connection).ExecuteAsync(Empty, await f.Context());
        Assert.True(link.Success);
        var url = JsonDocument.Parse(link.Content).RootElement.GetProperty("authorizationUrl").GetString()!;
        Assert.Equal("https://api.example.test/api/email/connect?redirect=true", url);
        Assert.DoesNotContain("state=", url);
        var googleUrl = (await f.Connection.CreateAuthorizationUrlAsync()).AuthorizationUrl;
        Assert.Contains(Uri.EscapeDataString(GoogleScopes.Calendar), googleUrl);
        Assert.Contains(Uri.EscapeDataString(GoogleScopes.Gmail), googleUrl);
        Assert.Contains(Uri.EscapeDataString(GoogleScopes.CalendarList), googleUrl);
        Assert.Single(f.Db.EmailAccountConnections);
    }

    [Fact]
    public async Task AllDayUpdatePreservesDescriptionLocationAndUsesExclusiveEnd()
    {
        using var f = new Fixture();
        var original = f.Google.AddEvent();
        original["description"] = "Keep description";
        var payload = await f.Calendar.PrepareAsync(CalendarActionTypes.Update, "primary", "event1", new(Start: "2026-10-12", End: "2026-10-13", AllDay: true));
        await f.Calendar.ExecuteAsync(CalendarActionTypes.Update, "primary", "event1", payload);
        Assert.Equal("2026-10-14", f.Google.Events["event1"]["end"]!["date"]!.ToString());
        Assert.Equal("Keep description", f.Google.Events["event1"]["description"]!.ToString());
        Assert.Equal("Campus", f.Google.Events["event1"]["location"]!.ToString());
        Assert.True(await f.Calendar.VerifyAsync(CalendarActionTypes.Update, "primary", "event1", payload));
    }

    [Fact]
    public async Task WholeRecurringSeriesCannotBePreparedButItsInstancesArePreserved()
    {
        using var f = new Fixture();
        var original = f.Google.AddEvent();
        original["recurrence"] = JsonNode.Parse("""["RRULE:FREQ=WEEKLY"]""");
        await Assert.ThrowsAsync<CalendarException>(() => f.Calendar.PrepareAsync(CalendarActionTypes.Update, "primary", "event1", new(Summary: "Changed")));
        original.Remove("recurrence");
        original["recurringEventId"] = "series1";
        original["originalStartTime"] = original["start"]!.DeepClone();
        var payload = await f.Calendar.PrepareAsync(CalendarActionTypes.Update, "primary", "event1", new(Summary: "Changed"));
        await f.Calendar.ExecuteAsync(CalendarActionTypes.Update, "primary", "event1", payload);
        Assert.Equal("series1", f.Google.Events["event1"]["recurringEventId"]!.ToString());
        Assert.NotNull(f.Google.Events["event1"]["originalStartTime"]);
    }

    [Fact]
    public async Task CalendarEventsOnlyConnectionNeedsListScopeWithoutDisconnectingGmail()
    {
        using var f = new Fixture(GoogleScopes.Gmail + " " + GoogleScopes.Calendar);
        var status = await f.Calendar.GetStatusAsync();
        Assert.True(status.IsConnected);
        Assert.True(status.CalendarEventsAuthorized);
        Assert.False(status.CalendarListAuthorized);
        Assert.False(status.CalendarAuthorized);
        Assert.Equal("calendar_list_scope_missing", status.AuthorizationState);
        Assert.Equal("calendar_list_scope_missing", (await Assert.ThrowsAsync<CalendarException>(() => f.Calendar.ListCalendarsAsync())).Code);
        Assert.Equal("calendar_list_scope_missing", (await Assert.ThrowsAsync<CalendarException>(() => f.Calendar.ListEventsAsync(null, null, null))).Code);
        Assert.Empty((await new GmailService(f.Http, Options.Create(new GmailOptions()), f.Tokens).SearchEmailsAsync("test")).Emails);
        Assert.Null(f.Db.EmailAccountConnections.Single().DisconnectedAt);
        var old = f.Db.EmailAccountConnections.Single();
        var id = old.Id;
        var refresh = old.RefreshTokenEncrypted;
        var url = (await f.Connection.CreateAuthorizationUrlAsync()).AuthorizationUrl;
        Assert.Contains(Uri.EscapeDataString(GoogleScopes.CalendarList), url);
        var state = Uri.UnescapeDataString(new Uri(url).Query.TrimStart('?').Split('&').Single(p => p.StartsWith("state="))[6..]);
        await f.Connection.HandleOAuthCallbackAsync("code", state);
        Assert.Equal(id, old.Id);
        Assert.Equal(refresh, old.RefreshTokenEncrypted);
        Assert.True((await f.Calendar.GetStatusAsync()).CalendarAuthorized);
        Assert.True(GoogleScopes.Contains(old.Scopes, GoogleScopes.Gmail));
        Assert.True(GoogleScopes.Contains(old.Scopes, GoogleScopes.Calendar));
        Assert.True(GoogleScopes.Contains(old.Scopes, GoogleScopes.CalendarList));
    }

    [Fact]
    public async Task CalendarListPagesIncludePrimaryNamesAndAccessRolesAndRememberReferences()
    {
        using var f = new Fixture();
        f.Google.CalendarPageSize = 1;
        f.Google.AddCalendar("diario@group.calendar.google.com", "Original")["summaryOverride"] = "Diario";
        f.Google.AddCalendar("faculty", "Faculdade", "reader");
        f.Google.AddCalendar("busy", "Ocupado", "freeBusyReader");
        var result = await new CalendarListCalendarsTool(f.Calendar, f.ToolContext).ExecuteAsync(Empty, await f.Context());
        Assert.True(result.Success);
        var list = JsonSerializer.Deserialize<CalendarListData>(result.Content, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(4, list.Calendars.Count);
        Assert.True(list.Calendars.Single(c => c.CalendarId == "primary").Primary);
        Assert.Equal("Diario", list.Calendars.Single(c => c.CalendarId.StartsWith("diario@")).Name);
        Assert.Equal("reader", list.Calendars.Single(c => c.CalendarId == "faculty").AccessRole);
        Assert.Equal("freeBusyReader", list.Calendars.Single(c => c.CalendarId == "busy").AccessRole);
        await f.ToolContext.RequireCalendarAsync(f.ConversationId, "faculty");
        Assert.DoesNotContain("backgroundColor", result.Content);
    }

    [Fact]
    public async Task PrimaryAndSecondaryEventsAreAggregatedSortedWithOriginAndGlobalLimit()
    {
        using var f = new Fixture();
        f.Google.AddCalendar("diario@group.calendar.google.com", "Diario");
        f.Google.AddCalendar("faculty", "Faculdade", "reader");
        f.Google.AddCalendar("busy", "Ocupado", "freeBusyReader");
        f.Google.AddEvent("primary-late");
        var morning = f.Google.AddEvent("secondary-morning", "diario@group.calendar.google.com");
        SetTiming(morning, "2026-10-10T09:00:00-03:00", "2026-10-10T10:00:00-03:00");
        var utc = f.Google.AddEvent("secondary-utc", "faculty");
        SetTiming(utc, "2026-10-10T13:00:00Z", "2026-10-10T14:00:00Z");
        f.Google.AddEvent("outside", "faculty")["start"]!["dateTime"] = "2026-10-12T14:00:00-03:00";
        var result = await f.Calendar.ListEventsAsync("2026-10-10T00:00:00-03:00", "2026-10-11T00:00:00-03:00", null, 10);
        Assert.Equal(new[] { "secondary-morning", "secondary-utc", "primary-late" }, result.Events.Select(e => e.EventId));
        Assert.Equal("Diario", result.Events[0].CalendarName);
        Assert.Equal("diario@group.calendar.google.com", result.Events[0].CalendarId);
        Assert.Equal("primary", result.Events[2].CalendarId);
        Assert.Equal("Principal", result.Events[2].CalendarName);
        Assert.DoesNotContain(f.Google.ListRequests, r => r.CalendarId == "busy");
        Assert.Equal(3, f.Google.ListRequests.Count);
        var limited = await f.Calendar.ListEventsAsync("2026-10-10T00:00:00-03:00", "2026-10-11T00:00:00-03:00", null, 2);
        Assert.Equal(2, limited.Events.Count);
        Assert.Equal(new[] { "secondary-morning", "secondary-utc" }, limited.Events.Select(e => e.EventId));
        Assert.True(limited.HasMore);
    }

    [Fact]
    public async Task TextSearchAndEventPaginationApplyToEveryCalendar()
    {
        using var f = new Fixture();
        f.Google.AddCalendar("diario", "Diario");
        f.Google.EventPageSize = 1;
        f.Google.AddEvent("ignored")["summary"] = "Dentista";
        f.Google.AddEvent("primary-match")["summary"] = "Prova de Grafos";
        f.Google.AddEvent("secondary-1", "diario")["summary"] = "Grafos revisão";
        f.Google.AddEvent("secondary-2", "diario")["description"] = "Estudar Grafos";
        var result = await f.Calendar.ListEventsAsync(null, null, "Grafos", 10);
        Assert.Equal(3, result.Events.Count);
        Assert.False(result.HasMore);
        Assert.DoesNotContain(result.Events, e => e.EventId == "ignored");
        Assert.All(f.Google.ListRequests, r => Assert.Contains("q=Grafos", Uri.UnescapeDataString(r.Query)));
        Assert.Contains(f.Google.ListRequests, r => r.CalendarId == "diario" && r.Query.Contains("pageToken="));
    }

    [Fact]
    public async Task AllDayAndTimedEventsAreSortedByTheirInstants()
    {
        using var f = new Fixture();
        f.Google.AddCalendar("diario", "Diario");
        var day = f.Google.AddEvent("day", "diario");
        day["start"] = new JsonObject { ["date"] = "2026-10-10" };
        day["end"] = new JsonObject { ["date"] = "2026-10-11" };
        var earlyUtc = f.Google.AddEvent("early-utc");
        SetTiming(earlyUtc, "2026-10-10T02:00:00Z", "2026-10-10T03:30:00Z");
        f.Google.AddEvent("afternoon");
        var result = await f.Calendar.ListEventsAsync(null, null, null);
        Assert.Equal(new[] { "early-utc", "day", "afternoon" }, result.Events.Select(e => e.EventId));
        Assert.True(result.Events[1].AllDay);
        Assert.Equal("2026-10-10", result.Events[1].End);
        Assert.Equal("America/Sao_Paulo", result.Events[1].TimeZone);
    }

    [Fact]
    public async Task CreateDefaultsToPrimaryEvenWithOtherWritableCalendarsObserved()
    {
        using var f = new Fixture();
        f.Google.AddCalendar("family", "Family", "owner");
        await new CalendarListCalendarsTool(f.Calendar, f.ToolContext).ExecuteAsync(Empty, await f.Context());
        var action = await f.PrepareCreate();
        Assert.Equal("primary", action.CalendarId);
        Assert.True((await f.Confirm.ExecuteAsync(Empty, await f.Context("sim"))).Success);
        Assert.Single(f.Google.Events);
        Assert.Empty(f.Google.EventsFor("family"));
        Assert.Equal("primary", Assert.Single(f.Google.MutationCalendars));
    }

    [Fact]
    public async Task ExplicitSecondaryDestinationIsPersistedConfirmedAndRetriedWithoutDuplicates()
    {
        using var f = new Fixture();
        const string destination = "diario@group.calendar.google.com";
        f.Google.AddCalendar(destination, "Diario");
        await new CalendarListCalendarsTool(f.Calendar, f.ToolContext).ExecuteAsync(Empty, await f.Context());
        var prepared = await f.Create.ExecuteAsync(Args(new { calendarId = destination, summary = "Dentista", start = Start, end = End, allDay = false }), await f.Context("Marca no Diario"));
        Assert.True(prepared.Success);
        var action = Assert.Single(f.Db.PendingCalendarActions);
        Assert.Equal(destination, action.CalendarId);
        Assert.Contains("Diario", action.HumanSummary);
        Assert.Equal(0, f.Google.Mutations);
        f.Google.ThrowAfterMutation = true;
        Assert.True((await f.Confirm.ExecuteAsync(Empty, await f.Context("sim"))).Success);
        var payload = JsonSerializer.Deserialize<CalendarActionPayload>(action.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        await Assert.ThrowsAsync<CalendarMutationException>(() => f.Calendar.ExecuteAsync(action.ActionType, action.CalendarId, action.EventId, payload));
        Assert.True(await f.Calendar.VerifyAsync(action.ActionType, action.CalendarId, action.EventId, payload));
        Assert.Single(f.Google.EventsFor(destination));
        Assert.Empty(f.Google.Events);
        Assert.Equal(destination, f.Db.CalendarActionAudits.Single().CalendarId);
        Assert.True(await f.ToolContext.WasObservedAsync(f.ConversationId, destination, action.EventId));
    }

    [Theory]
    [InlineData(CalendarActionTypes.Create)]
    [InlineData(CalendarActionTypes.Update)]
    [InlineData(CalendarActionTypes.Delete)]
    public async Task ReaderCalendarsRejectAllMutationPreparations(string type)
    {
        using var f = new Fixture();
        f.Google.AddCalendar("readonly", "Leitura", "reader");
        f.Google.AddEvent("readonly-event", "readonly");
        await new CalendarListCalendarsTool(f.Calendar, f.ToolContext).ExecuteAsync(Empty, await f.Context());
        await new CalendarListEventsTool(f.Calendar, f.ToolContext).ExecuteAsync(Empty, await f.Context());
        IAegisTool tool = type == CalendarActionTypes.Create ? f.Create : type == CalendarActionTypes.Update ? f.Update : new CalendarDeleteEventTool(f.Db, f.Calendar, f.ToolContext);
        var args = type == CalendarActionTypes.Create ? Args(new { calendarId = "readonly", summary = "A", start = Start, end = End, allDay = false })
            : type == CalendarActionTypes.Update ? Args(new { eventId = "readonly-event", summary = "A" }) : Args(new { eventId = "readonly-event" });
        Assert.Equal("calendar_write_access_denied", (await tool.ExecuteAsync(args, await f.Context())).ErrorCode);
        Assert.Empty(f.Db.PendingCalendarActions);
        Assert.Equal(0, f.Google.Mutations);
    }

    [Fact]
    public async Task PermissionIsCheckedAgainAtConfirmation()
    {
        using var f = new Fixture();
        await f.PrepareCreate();
        f.Google.Calendars["primary"]["accessRole"] = "reader";
        Assert.Equal("calendar_write_access_denied", (await f.Confirm.ExecuteAsync(Empty, await f.Context("sim"))).ErrorCode);
        Assert.Equal(0, f.Google.Mutations);
        Assert.False(f.Db.PendingCalendarActions.Single().MayHaveAppliedChanges);
    }

    [Theory]
    [InlineData(CalendarActionTypes.Update)]
    [InlineData(CalendarActionTypes.Delete)]
    public async Task ExistingEventMutationsUseObservedOriginWithoutUserRepeatingCalendar(string type)
    {
        using var f = new Fixture();
        f.Google.AddCalendar("diario", "Diario");
        f.Google.AddEvent("kenshiro", "diario");
        await new CalendarListEventsTool(f.Calendar, f.ToolContext).ExecuteAsync(Empty, await f.Context());
        IAegisTool tool = type == CalendarActionTypes.Update ? f.Update : new CalendarDeleteEventTool(f.Db, f.Calendar, f.ToolContext);
        var args = type == CalendarActionTypes.Update ? Args(new { eventId = "kenshiro", start = "2026-10-10T09:00:00-03:00", end = "2026-10-10T10:00:00-03:00" }) : Args(new { eventId = "kenshiro" });
        Assert.True((await tool.ExecuteAsync(args, await f.Context(type == CalendarActionTypes.Update ? "Muda o aniversário do Kenshiro para 9h" : "Cancela o aniversário"))).Success);
        Assert.Equal("diario", f.Db.PendingCalendarActions.Single().CalendarId);
        Assert.True((await f.Confirm.ExecuteAsync(Empty, await f.Context("sim"))).Success);
        Assert.Equal("diario", Assert.Single(f.Google.MutationCalendars));
        Assert.Equal("diario", f.Db.CalendarActionAudits.Single().CalendarId);
        Assert.Empty(f.Google.Events);
        if (type == CalendarActionTypes.Update) Assert.Equal("2026-10-10T09:00:00.0000000-03:00", f.Google.EventsFor("diario")["kenshiro"]["start"]!["dateTime"]!.ToString());
        else Assert.Empty(f.Google.EventsFor("diario"));
    }

    [Fact]
    public async Task ContextProtectsCalendarEventPairsAndRejectsInventedOrAmbiguousReferences()
    {
        using var f = new Fixture();
        f.Google.AddCalendar("diario", "Diario");
        f.Google.AddEvent("same");
        f.Google.AddEvent("same", "diario");
        f.Google.AddEvent("only-secondary", "diario");
        var ctx = await f.Context();
        await new CalendarListEventsTool(f.Calendar, f.ToolContext).ExecuteAsync(Empty, ctx);
        Assert.True(await f.ToolContext.WasObservedAsync(f.ConversationId, "primary", "same"));
        Assert.True(await f.ToolContext.WasObservedAsync(f.ConversationId, "diario", "same"));
        Assert.Equal(3, f.Db.ToolContextEntries.Count());
        var get = new CalendarGetEventTool(f.Calendar, f.ToolContext);
        Assert.True((await get.ExecuteAsync(Args(new { calendarId = "diario", eventId = "same" }), ctx)).Success);
        Assert.False((await f.Update.ExecuteAsync(Args(new { eventId = "same", summary = "A" }), ctx)).Success);
        Assert.True((await f.Update.ExecuteAsync(Args(new { calendarId = "diario", eventId = "same", summary = "A" }), ctx)).Success);
        Assert.False((await f.Update.ExecuteAsync(Args(new { calendarId = "primary", eventId = "only-secondary", summary = "A" }), ctx)).Success);
        Assert.False((await f.Update.ExecuteAsync(Args(new { calendarId = "invented", eventId = "same", summary = "A" }), ctx)).Success);
        Assert.False((await get.ExecuteAsync(Args(new { calendarId = "diario", eventId = "invented" }), ctx)).Success);
        Assert.False((await f.Create.ExecuteAsync(Args(new { calendarId = "invented", summary = "A", start = Start, end = End, allDay = false }), ctx)).Success);
        Assert.Equal(0, f.Google.Mutations);
    }

    [Fact]
    public async Task ExpiredSecondaryReferencesMustBeResolvedAgain()
    {
        using var f = new Fixture();
        f.Google.AddCalendar("diario", "Diario");
        f.Google.AddEvent("event1", "diario");
        await new CalendarListEventsTool(f.Calendar, f.ToolContext).ExecuteAsync(Empty, await f.Context());
        await new CalendarListCalendarsTool(f.Calendar, f.ToolContext).ExecuteAsync(Empty, await f.Context());
        foreach (var entry in f.Db.ToolContextEntries) entry.Replace();
        await f.Db.SaveChangesAsync();
        Assert.False(await f.ToolContext.WasObservedAsync(f.ConversationId, "diario", "event1"));
        Assert.False((await f.Update.ExecuteAsync(Args(new { calendarId = "diario", eventId = "event1", summary = "A" }), await f.Context())).Success);
        Assert.False((await f.Create.ExecuteAsync(Args(new { calendarId = "diario", summary = "A", start = Start, end = End, allDay = false }), await f.Context())).Success);
    }

    [Theory]
    [InlineData(403, "events.list", "accessNotConfigured", "SERVICE_DISABLED", "calendar_api_disabled")]
    [InlineData(403, "calendarList.list", "accessNotConfigured", "SERVICE_DISABLED", "calendar_api_disabled")]
    [InlineData(403, "events.list", "insufficientPermissions", "ACCESS_TOKEN_SCOPE_INSUFFICIENT", "calendar_scope_missing")]
    [InlineData(403, "events.list", "forbidden", "PERMISSION_DENIED", "calendar_access_denied")]
    [InlineData(403, "events.list", "rateLimitExceeded", "RESOURCE_EXHAUSTED", "calendar_temporarily_unavailable")]
    [InlineData(404, "events.get", "notFound", "NOT_FOUND", "calendar_event_not_found")]
    [InlineData(404, "calendarList.get", "notFound", "NOT_FOUND", "calendar_not_found")]
    [InlineData(404, "events.list", "notFound", "NOT_FOUND", "calendar_not_found")]
    [InlineData(503, "events.list", "backendError", "UNAVAILABLE", "calendar_temporarily_unavailable")]
    public async Task GoogleErrorsAreSpecificAndDiagnosticLogsNeverIncludeCredentials(int status, string operation, string reason, string detailReason, string expected)
    {
        using var f = new Fixture();
        f.Google.FailureStatus = (HttpStatusCode)status;
        f.Google.FailureOperation = operation;
        f.Google.FailureBody = JsonSerializer.Serialize(new { error = new { code = status, message = "secret-access-token refresh-token authorization-code client-secret",
            errors = new[] { new { reason } }, details = new[] { new { reason = detailReason, metadata = new { service = "calendar-json.googleapis.com", consumer = "projects/internal" } } } } });
        var exception = await Assert.ThrowsAsync<CalendarException>(() => operation.EndsWith("get") ? f.Calendar.GetEventAsync("primary", "missing") : List());
        Assert.Equal(expected, exception.Code);
        var log = Assert.Single(f.Log.Messages);
        Assert.Contains($"status={status}", log);
        Assert.Contains(reason, log);
        Assert.Contains(detailReason, log);
        Assert.Contains(operation, log);
        Assert.Contains("calendar-json.googleapis.com", log);
        Assert.DoesNotContain("secret-access-token", log + exception.Message);
        Assert.DoesNotContain("refresh-token", log + exception.Message);
        Assert.DoesNotContain("authorization-code", log + exception.Message);
        Assert.DoesNotContain("client-secret", log + exception.Message);
        async Task<object> List() => await f.Calendar.ListEventsAsync(null, null, null);
    }

    [Fact]
    public async Task ApiDisabledToolErrorExplainsActivationAndNeverReportsAccessDenied()
    {
        using var f = new Fixture();
        f.Google.FailureOperation = "calendarList.list";
        f.Google.FailureBody = """{"error":{"code":403,"errors":[{"reason":"accessNotConfigured"}],"details":[{"reason":"SERVICE_DISABLED"}]}}""";
        var result = await new CalendarListEventsTool(f.Calendar, f.ToolContext).ExecuteAsync(Empty, await f.Context());
        Assert.Equal("calendar_api_disabled", result.ErrorCode);
        Assert.Contains("Ative", result.Content);
        Assert.DoesNotContain("calendar_access_denied", result.Content);
    }

    [Fact]
    public async Task PrimaryAliasConfirmationReturnsCanonicalObservedPairThatCanBeReused()
    {
        using var f = new Fixture();
        f.Google.Calendars.Remove("primary");
        f.Google.AddCalendar("name@gmail.com", "Principal", "owner", true);
        var action = await f.PrepareCreate();
        Assert.Equal("primary", action.CalendarId);
        var confirmed = await f.Confirm.ExecuteAsync(Empty, await f.Context("sim"));
        Assert.True(confirmed.Success);
        var calendarId = JsonDocument.Parse(confirmed.Content).RootElement.GetProperty("calendarId").GetString();
        Assert.Equal("name@gmail.com", calendarId);
        Assert.True(await f.ToolContext.WasObservedAsync(f.ConversationId, calendarId!, action.EventId));
        var get = await new CalendarGetEventTool(f.Calendar, f.ToolContext).ExecuteAsync(Args(new { calendarId, eventId = action.EventId }), await f.Context());
        Assert.True(get.Success);
        Assert.True((await f.Update.ExecuteAsync(Args(new { calendarId, eventId = action.EventId, summary = "Dentista novo" }), await f.Context())).Success);
        Assert.Equal(calendarId, (await f.Db.GetLatestOpenPendingCalendarActionAsync(f.ConversationId))!.CalendarId);
    }

    [Theory]
    [InlineData("pt.brazilian#holiday@group.v.calendar.google.com", "Nome personalizado", "holiday")]
    [InlineData("en.usa#holiday@group.v.calendar.google.com", "Holidays in the United States", "holiday")]
    [InlineData("ja.japanese#holiday@group.v.calendar.google.com", "祝日", "holiday")]
    [InlineData("personal@group.calendar.google.com", "Feriados no Brasil", "calendar")]
    [InlineData("pt.brazilian#holiday@group.v.calendar.google.com.example.test", "Feriados", "calendar")]
    public async Task HolidayClassificationUsesGoogleCalendarIdInsteadOfDisplayName(string id, string name, string expected)
    {
        using var f = new Fixture();
        f.Google.AddCalendar(id, name, "reader");
        var result = await new CalendarListCalendarsTool(f.Calendar, f.ToolContext).ExecuteAsync(Empty, await f.Context());
        Assert.True(result.Success);
        var calendars = JsonSerializer.Deserialize<CalendarListData>(result.Content, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Calendars;
        Assert.Equal(expected, calendars.Single(c => c.CalendarId == id).CalendarType);
        Assert.Equal("calendar", calendars.Single(c => c.Primary).CalendarType);
    }

    [Fact]
    public async Task HolidayOnlyQueryReturnsNoAppointmentsAndKeepsObservedHolidayDetails()
    {
        using var f = new Fixture();
        const string calendarId = "pt.brazilian#holiday@group.v.calendar.google.com";
        f.Google.AddCalendar(calendarId, "Feriados no Brasil", "reader");
        AddHoliday(f.Google, calendarId, "holiday1", "Data comemorativa", "2026-10-10");
        var result = await new CalendarListEventsTool(f.Calendar, f.ToolContext).ExecuteAsync(
            Args(new { timeMin = "2026-10-10T00:00:00-03:00", timeMax = "2026-10-11T00:00:00-03:00" }), await f.Context());
        Assert.True(result.Success);
        var list = JsonSerializer.Deserialize<CalendarEventList>(result.Content, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Empty(list.Events);
        var holiday = Assert.Single(list.Holidays);
        Assert.Equal("holiday", holiday.Type);
        Assert.Equal(calendarId, holiday.CalendarId);
        Assert.Equal("Feriados no Brasil", holiday.CalendarName);
        Assert.Equal("2026-10-10", holiday.Start);
        Assert.Equal(holiday.Start, holiday.End);
        Assert.True(holiday.AllDay);
        Assert.False(list.HasMoreEvents);
        Assert.False(list.HasMoreHolidays);
        Assert.True(await f.ToolContext.WasObservedAsync(f.ConversationId, calendarId, holiday.EventId));
        var detail = await new CalendarGetEventTool(f.Calendar, f.ToolContext).ExecuteAsync(
            Args(new { calendarId, eventId = holiday.EventId }), await f.Context());
        Assert.True(detail.Success);
        Assert.Equal("holiday", JsonDocument.Parse(detail.Content).RootElement.GetProperty("calendarEvent").GetProperty("type").GetString());
        var invalid = await new CalendarGetEventTool(f.Calendar, f.ToolContext).ExecuteAsync(
            Args(new { calendarId, eventId = "invented" }), await f.Context());
        Assert.Equal("invalid_tool_arguments", invalid.ErrorCode);
        Assert.Contains("\"type\":\"holiday\"", JsonDocument.Parse(invalid.Content).RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task AppointmentsAndHolidaysAreSeparatedSortedAndSearchableAcrossCalendars()
    {
        using var f = new Fixture();
        const string calendarId = "en.usa#holiday@group.v.calendar.google.com";
        f.Google.AddCalendar(calendarId, "Dias especiais", "reader");
        f.Google.AddCalendar("diario", "Diario");
        f.Google.AddEvent("late")["summary"] = "Revisão";
        var morning = f.Google.AddEvent("early", "diario");
        morning["summary"] = "Revisão";
        SetTiming(morning, "2026-10-10T09:00:00-03:00", "2026-10-10T10:00:00-03:00");
        AddHoliday(f.Google, calendarId, "later-holiday", "Revisão local", "2026-10-12");
        AddHoliday(f.Google, calendarId, "earlier-holiday", "Revisão regional", "2026-10-10");
        AddHoliday(f.Google, calendarId, "not-a-match", "Outro dia", "2026-10-11");
        var result = await f.Calendar.ListEventsAsync(null, null, "Revisão");
        Assert.Equal(new[] { "early", "late" }, result.Events.Select(e => e.EventId));
        Assert.Equal(new[] { "earlier-holiday", "later-holiday" }, result.Holidays.Select(e => e.EventId));
        Assert.All(result.Events, e => Assert.Equal("event", e.Type));
        Assert.All(result.Holidays, e => Assert.Equal("holiday", e.Type));
        Assert.All(f.Google.ListRequests, r => Assert.Contains("q=Revisão", Uri.UnescapeDataString(r.Query)));
        Assert.False(result.HasMore);
    }

    [Fact]
    public async Task GlobalLimitPrioritizesNextAppointmentInsteadOfDisplacingItWithHoliday()
    {
        using var f = new Fixture();
        const string calendarId = "en.usa#holiday@group.v.calendar.google.com";
        f.Google.AddCalendar(calendarId, "Feriados", "reader");
        AddHoliday(f.Google, calendarId, "h1", "Primeiro feriado", "2026-10-09");
        AddHoliday(f.Google, calendarId, "h2", "Segundo feriado", "2026-10-10");
        f.Google.AddEvent("appointment");
        var next = await f.Calendar.ListEventsAsync("2026-10-09T00:00:00-03:00", null, null, 1);
        Assert.Equal("appointment", Assert.Single(next.Events).EventId);
        Assert.Empty(next.Holidays);
        Assert.True(next.HasMoreHolidays);
        Assert.False(next.HasMoreEvents);
        var limited = await f.Calendar.ListEventsAsync(null, null, null, 2);
        Assert.Equal(2, limited.Events.Count + limited.Holidays.Count);
        Assert.Equal("h1", Assert.Single(limited.Holidays).EventId);
        Assert.True(limited.HasMore);
    }

    [Fact]
    public async Task CreateOnOrdinaryDateHasNoHolidayWarningsAndQueriesOnlyHolidayCalendars()
    {
        using var f = new Fixture();
        const string calendarId = "pt.brazilian#holiday@group.v.calendar.google.com";
        f.Google.AddCalendar(calendarId, "Feriados", "reader");
        f.Google.AddCalendar("diario", "Diario");
        AddHoliday(f.Google, calendarId, "another-day", "Feriado de outro dia", "2026-10-11");
        var action = await f.PrepareCreate();
        var payload = JsonSerializer.Deserialize<CalendarActionPayload>(action.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Empty(payload.HolidayWarnings!);
        Assert.False(payload.HolidayWarningsHasMore);
        Assert.Equal(calendarId, Assert.Single(f.Google.ListRequests).CalendarId);
        Assert.Equal(0, f.Google.Mutations);
    }

    [Fact]
    public async Task CreateOnHolidayWarnsBeforeConfirmationAndStillCreatesAlongsideOtherAppointments()
    {
        using var f = new Fixture();
        const string calendarId = "pt.brazilian#holiday@group.v.calendar.google.com";
        f.Google.AddCalendar(calendarId, "Feriados", "reader");
        AddHoliday(f.Google, calendarId, "holiday", "Feriado regional", "2026-10-10");
        f.Google.AddEvent("existing-appointment");
        var context = await f.Context("Marca dentista amanhã às 14h por uma hora");
        var result = await f.Create.ExecuteAsync(Args(new { summary = "Dentista", start = Start, end = End, allDay = false }), context);
        Assert.True(result.Success);
        var warning = Assert.Single(JsonDocument.Parse(result.Content).RootElement.GetProperty("holidayWarnings").EnumerateArray());
        Assert.Equal("Feriado regional", warning.GetProperty("name").GetString());
        Assert.Equal("2026-10-10", warning.GetProperty("date").GetString());
        Assert.Equal(calendarId, warning.GetProperty("calendarId").GetString());
        var action = Assert.Single(f.Db.PendingCalendarActions);
        var payload = JsonSerializer.Deserialize<CalendarActionPayload>(action.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("Feriado regional", Assert.Single(payload.HolidayWarnings!).Name);
        Assert.Equal("primary", action.CalendarId);
        Assert.Equal(0, f.Google.Mutations);
        Assert.Equal("confirmation_required", (await f.Confirm.ExecuteAsync(Empty, context)).ErrorCode);
        Assert.True((await f.Confirm.ExecuteAsync(Empty, await f.Context("sim"))).Success);
        Assert.Equal(2, f.Google.Events.Count);
        Assert.Equal("primary", Assert.Single(f.Google.MutationCalendars));
        Assert.Single(f.Google.EventsFor(calendarId));
        var query = Uri.UnescapeDataString(Assert.Single(f.Google.ListRequests).Query);
        Assert.Contains("timeMin=2026-10-10T00:00:00.0000000-03:00", query);
        Assert.Contains("timeMax=2026-10-11T00:00:00.0000000-03:00", query);
        Assert.DoesNotContain("q=", query);
    }

    [Fact]
    public async Task MovingSecondaryEventToHolidayReturnsWarningAndPreservesOrigin()
    {
        using var f = new Fixture();
        const string calendarId = "ja.japanese#holiday@group.v.calendar.google.com";
        f.Google.AddCalendar(calendarId, "Jours fériés", "reader");
        AddHoliday(f.Google, calendarId, "holiday", "Dia regional", "2026-10-12");
        f.Google.AddCalendar("diario", "Diario");
        f.Google.AddEvent("appointment", "diario");
        await new CalendarListEventsTool(f.Calendar, f.ToolContext).ExecuteAsync(Empty, await f.Context());
        f.Google.ListRequests.Clear();
        var result = await f.Update.ExecuteAsync(Args(new { eventId = "appointment", start = "2026-10-12T09:00:00-03:00", end = "2026-10-12T10:00:00-03:00" }), await f.Context());
        Assert.True(result.Success);
        var warning = Assert.Single(JsonDocument.Parse(result.Content).RootElement.GetProperty("holidayWarnings").EnumerateArray());
        Assert.Equal("2026-10-12", warning.GetProperty("date").GetString());
        Assert.Equal("diario", Assert.Single(f.Db.PendingCalendarActions).CalendarId);
        Assert.Equal(0, f.Google.Mutations);
        Assert.Equal(calendarId, Assert.Single(f.Google.ListRequests).CalendarId);
        Assert.True((await f.Confirm.ExecuteAsync(Empty, await f.Context("sim"))).Success);
        Assert.Equal("diario", Assert.Single(f.Google.MutationCalendars));
        Assert.Equal("2026-10-12T09:00:00.0000000-03:00", f.Google.EventsFor("diario")["appointment"]["start"]!["dateTime"]!.ToString());
    }

    [Theory]
    [InlineData(CalendarActionTypes.Update)]
    [InlineData(CalendarActionTypes.Delete)]
    public async Task UnchangedDateOrDeletionDoesNotQueryHolidayCalendars(string actionType)
    {
        using var f = new Fixture();
        const string calendarId = "en.usa#holiday@group.v.calendar.google.com";
        f.Google.AddCalendar(calendarId, "Feriados", "reader");
        AddHoliday(f.Google, calendarId, "holiday", "Dia especial", "2026-10-10");
        f.Google.AddEvent();
        var payload = await f.Calendar.PrepareAsync(actionType, "primary", "event1", new(Summary: "Novo título"));
        Assert.Empty(payload.HolidayWarnings!);
        Assert.Empty(f.Google.ListRequests);
        Assert.Equal(0, f.Google.Mutations);
    }

    [Theory]
    [InlineData("2026-10-10", "2026-10-10", "2026-10-10", null)]
    [InlineData("2026-10-10", "2026-10-11", "2026-10-10", "2026-10-11")]
    public async Task AllDayWarningDatesRespectInclusiveToolsAndExclusiveGoogleEnd(string start, string end, string date, string? endDate)
    {
        using var f = new Fixture();
        const string calendarId = "en.usa#holiday@group.v.calendar.google.com";
        f.Google.AddCalendar(calendarId, "Feriados", "reader");
        AddHoliday(f.Google, calendarId, "multi-day", "Feriado prolongado", "2026-10-10", "2026-10-12");
        AddHoliday(f.Google, calendarId, "outside", "Dia posterior", "2026-10-12");
        var result = await f.Create.ExecuteAsync(Args(new { summary = "Viagem", start, end, allDay = true }), await f.Context());
        Assert.True(result.Success);
        var action = Assert.Single(f.Db.PendingCalendarActions);
        var payload = JsonSerializer.Deserialize<CalendarActionPayload>(action.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var warning = Assert.Single(payload.HolidayWarnings!);
        Assert.Equal(date, warning.Date);
        Assert.Equal(endDate, warning.EndDate);
        Assert.True((await f.Confirm.ExecuteAsync(Empty, await f.Context("sim"))).Success);
        Assert.Equal(DateOnly.Parse(end).AddDays(1).ToString("yyyy-MM-dd"), f.Google.Events[action.EventId]["end"]!["date"]!.ToString());
    }

    [Theory]
    [InlineData("2026-10-11T01:00:00Z", "2026-10-11T02:00:00Z", "2026-10-10")]
    [InlineData("2026-10-10T23:00:00-03:00", "2026-10-11T00:00:00-03:00", "2026-10-10")]
    [InlineData("2026-10-10T23:00:00-03:00", "2026-10-11T01:00:00-03:00", "2026-10-10,2026-10-11")]
    public async Task HolidayWarningsUseEventTimezoneAndExcludeNextDayAtMidnightEnd(string start, string end, string dates)
    {
        using var f = new Fixture();
        const string calendarId = "en.usa#holiday@group.v.calendar.google.com";
        f.Google.AddCalendar(calendarId, "Feriados", "reader")["timeZone"] = "America/New_York";
        AddHoliday(f.Google, calendarId, "first", "Primeiro dia", "2026-10-10");
        AddHoliday(f.Google, calendarId, "second", "Segundo dia", "2026-10-11");
        var payload = await f.Calendar.PrepareAsync(CalendarActionTypes.Create, "primary", "new-id", new(Summary: "Estudo", Start: start, End: end, AllDay: false));
        Assert.Equal(dates.Split(','), payload.HolidayWarnings!.Select(w => w.Date));
        var query = Uri.UnescapeDataString(Assert.Single(f.Google.ListRequests).Query);
        Assert.Contains("timeMin=2026-10-10T00:00:00.0000000-04:00", query);
        Assert.Equal(0, f.Google.Mutations);
    }

    [Fact]
    public async Task HolidayWarningPaginationAndGlobalLimitAreExplicitWithoutBlockingPreparation()
    {
        using var f = new Fixture();
        f.Google.EventPageSize = 10;
        foreach (var id in new[] { "pt.brazilian#holiday@group.v.calendar.google.com", "en.usa#holiday@group.v.calendar.google.com" })
        {
            f.Google.AddCalendar(id, "Feriados", "reader");
            for (var i = 0; i < 30; i++) AddHoliday(f.Google, id, "h" + i, "Feriado " + i, "2026-10-10");
        }
        var result = await f.Create.ExecuteAsync(Args(new { summary = "Estudo", start = Start, end = End, allDay = false }), await f.Context());
        Assert.True(result.Success);
        var document = JsonDocument.Parse(result.Content).RootElement;
        Assert.Equal(50, document.GetProperty("holidayWarnings").GetArrayLength());
        Assert.True(document.GetProperty("holidayWarningsHasMore").GetBoolean());
        Assert.Contains(f.Google.ListRequests, r => r.Query.Contains("pageToken="));
        Assert.Single(f.Db.PendingCalendarActions);
        Assert.Equal(0, f.Google.Mutations);
    }

    [Theory]
    [InlineData("Nah, deixa quieto")]
    [InlineData("Esquece")]
    [InlineData("texto interpretado pelo modelo")]
    public async Task CancellationDoesNotRequireKeywordsOrAnExtraConfirmation(string content)
    {
        using var f = new Fixture();
        var action = await f.PrepareCreate();
        var result = await new CalendarCancelPendingActionTool(f.Db).ExecuteAsync(Empty, await f.Context(content));
        Assert.True(result.Success);
        Assert.NotNull(action.CancelledAt);
        Assert.False(action.MayHaveAppliedChanges);
        Assert.Equal(0, f.Google.Mutations);
        Assert.Equal("no_pending_calendar_action", (await new CalendarCancelPendingActionTool(f.Db).ExecuteAsync(Empty, await f.Context(content))).ErrorCode);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("other_conversation")]
    [InlineData("assistant")]
    public async Task ConfirmationAndCancellationRequirePersistedUserMessageInCurrentConversation(string invalidState)
    {
        using var f = new Fixture();
        var action = await f.PrepareCreate();
        var message = new ChatMessage(invalidState == "other_conversation" ? Guid.NewGuid() : f.ConversationId,
            invalidState == "assistant" ? ChatRoles.Assistant : ChatRoles.User, "Manda bala");
        if (invalidState != "missing") f.Db.AddChatMessage(message);
        await f.Db.SaveChangesAsync();
        var context = new ToolExecutionContext(f.ConversationId, message.Id, "Manda bala");
        Assert.Equal("confirmation_required", (await f.Confirm.ExecuteAsync(Empty, context)).ErrorCode);
        Assert.False((await new CalendarCancelPendingActionTool(f.Db).ExecuteAsync(Empty, context)).Success);
        Assert.True(action.IsOpen());
        Assert.Equal(0, f.Google.Mutations);
    }

    [Fact]
    public async Task AmendPendingCreatePreservesOmittedFieldsAndSupersedesWithoutExternalMutation()
    {
        using var f = new Fixture();
        await f.Create.ExecuteAsync(Args(new { summary = "Entregar encomenda", start = "2026-10-28T14:00:00-03:00",
            end = "2026-10-28T15:00:00-03:00", allDay = false, description = "Pacote", location = "Correios" }), await f.Context());
        var first = Assert.Single(f.Db.PendingCalendarActions);
        var originalJson = first.PayloadJson;
        var context = await f.Context("Não, coloca dia 27 às 16h");
        var amend = new CalendarAmendPendingActionTool(f.Db, f.Calendar, f.ToolContext);
        var result = await amend.ExecuteAsync(Args(new { start = "2026-10-27T16:00:00-03:00", end = "2026-10-27T17:00:00-03:00" }), context);
        Assert.True(result.Success);
        var latest = (await f.Db.GetLatestOpenPendingCalendarActionAsync(f.ConversationId))!;
        var fields = Payload(latest).Fields;
        Assert.Equal("Entregar encomenda", fields["summary"]!.ToString());
        Assert.Equal("Pacote", fields["description"]!.ToString());
        Assert.Equal("Correios", fields["location"]!.ToString());
        Assert.Equal("2026-10-27T16:00:00.0000000-03:00", fields["start"]!["dateTime"]!.ToString());
        Assert.Equal(originalJson, first.PayloadJson);
        Assert.NotNull(first.SupersededAt);
        Assert.Equal(latest.Id, first.SupersededById);
        Assert.Null(first.CancelledAt);
        Assert.NotEqual(first.EventId, latest.EventId);
        Assert.Single(f.Db.PendingCalendarActions.AsEnumerable().Where(a => a.IsOpen()));
        Assert.Equal(0, f.Google.Mutations);
        Assert.Equal("confirmation_required", (await f.Confirm.ExecuteAsync(Empty, context)).ErrorCode);
        Assert.True((await f.Confirm.ExecuteAsync(Empty, await f.Context("Manda bala"))).Success);
        Assert.Equal(latest.EventId, Assert.Single(f.Google.Events).Key);
        Assert.Contains(f.Db.CalendarActionAudits, a => a.PendingActionId == first.Id && a.ActionType == "supersede_create" && a.UserConfirmationMessageId == context.UserMessageId);
    }

    [Fact]
    public async Task AmendPendingUpdatePreservesEarlierChangesAndCalendarOrigin()
    {
        using var f = new Fixture();
        f.Google.AddCalendar("diario", "Diario");
        f.Google.AddEvent("event1", "diario");
        await new CalendarListEventsTool(f.Calendar, f.ToolContext).ExecuteAsync(Empty, await f.Context());
        Assert.True((await f.Update.ExecuteAsync(Args(new { eventId = "event1", start = "2026-10-12T09:00:00-03:00", end = "2026-10-12T10:00:00-03:00" }), await f.Context())).Success);
        var first = Assert.Single(f.Db.PendingCalendarActions);
        var amended = await new CalendarAmendPendingActionTool(f.Db, f.Calendar, f.ToolContext).ExecuteAsync(Args(new { summary = "Consulta" }), await f.Context("Muda o nome para Consulta"));
        Assert.True(amended.Success);
        var latest = (await f.Db.GetLatestOpenPendingCalendarActionAsync(f.ConversationId))!;
        Assert.Equal(first.Id, Assert.Single(JsonDocument.Parse(amended.Content).RootElement.GetProperty("supersededActionIds").EnumerateArray()).GetGuid());
        Assert.Equal(CalendarActionTypes.Update, latest.ActionType);
        Assert.Equal("diario", latest.CalendarId);
        Assert.Equal("event1", latest.EventId);
        Assert.True((await f.Confirm.ExecuteAsync(Empty, await f.Context("Pode"))).Success);
        var updated = f.Google.EventsFor("diario")["event1"];
        Assert.Equal("Consulta", updated["summary"]!.ToString());
        Assert.Equal("2026-10-12T09:00:00.0000000-03:00", updated["start"]!["dateTime"]!.ToString());
        Assert.Equal("Campus", updated["location"]!.ToString());
    }

    [Fact]
    public async Task IncompatibleNewProposalSupersedesPendingDeleteWithoutDeletingTheEvent()
    {
        using var f = new Fixture();
        f.Google.AddEvent();
        await f.Observe();
        await new CalendarDeleteEventTool(f.Db, f.Calendar, f.ToolContext).ExecuteAsync(Args(new { eventId = "event1" }), await f.Context());
        var old = Assert.Single(f.Db.PendingCalendarActions);
        var latest = await f.PrepareCreate();
        Assert.Equal(latest.Id, old.SupersededById);
        Assert.Equal(0, f.Google.Mutations);
        Assert.True((await f.Confirm.ExecuteAsync(Empty, await f.Context("Faz"))).Success);
        Assert.Contains("event1", f.Google.Events.Keys);
        Assert.Contains(latest.EventId, f.Google.Events.Keys);
    }

    [Fact]
    public async Task InvalidReplacementDoesNotDiscardTheOriginalProposal()
    {
        using var f = new Fixture();
        var first = await f.PrepareCreate();
        var amend = new CalendarAmendPendingActionTool(f.Db, f.Calendar, f.ToolContext);
        Assert.False((await amend.ExecuteAsync(Args(new { end = "2026-10-10T13:00:00-03:00" }), await f.Context())).Success);
        Assert.False((await f.Create.ExecuteAsync(Args(new { summary = "A", start = Start, allDay = false }), await f.Context())).Success);
        Assert.True(first.IsOpen());
        Assert.Null(first.SupersededAt);
        Assert.Single(f.Db.PendingCalendarActions);
        Assert.Equal(0, f.Google.Mutations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PossibleExternalEffectsCannotBeSilentlyReplacedEvenWhenExpired(bool expired)
    {
        using var f = new Fixture();
        var prepared = await f.PrepareCreate();
        prepared.Cancel();
        var action = new PendingCalendarAction(f.ConversationId, CalendarActionTypes.Create, prepared.EventId, prepared.PayloadJson,
            prepared.HumanSummary, DateTimeOffset.UtcNow.AddMinutes(expired ? -1 : 10));
        action.RecordPossibleExternalEffects();
        f.Db.AddPendingCalendarAction(action);
        await f.Db.SaveChangesAsync();
        var context = await f.Context("Troca para outro dia");
        var result = await f.Create.ExecuteAsync(Args(new { summary = "Outro", start = Start, end = End, allDay = false }), context);
        Assert.Equal("calendar_action_outcome_unknown", result.ErrorCode);
        Assert.False((await new CalendarAmendPendingActionTool(f.Db, f.Calendar, f.ToolContext).ExecuteAsync(Args(new { summary = "Outro" }), context)).Success);
        Assert.Null(action.SupersededAt);
        Assert.Null(action.CancelledAt);
        Assert.Equal(0, f.Google.Mutations);
        if (expired) Assert.Equal("no_pending_calendar_action", (await f.Confirm.ExecuteAsync(Empty, await f.Context("Pode"))).ErrorCode);
        var cancelled = await new CalendarCancelPendingActionTool(f.Db).ExecuteAsync(Empty, await f.Context("Esquece essa tentativa"));
        Assert.True(cancelled.Success);
        Assert.Contains("não foram revertidas", JsonDocument.Parse(cancelled.Content).RootElement.GetProperty("userMessage").GetString());
        Assert.True(action.MayHaveAppliedChanges);
    }

    [Fact]
    public async Task AllUnexecutedProposalsAreSupersededSoOlderActionsNeverBecomeActiveAgain()
    {
        using var f = new Fixture();
        var first = await f.PrepareCreate();
        var legacy = new PendingCalendarAction(f.ConversationId, first.ActionType, "legacy-id", first.PayloadJson, first.HumanSummary, DateTimeOffset.UtcNow.AddMinutes(10));
        f.Db.AddPendingCalendarAction(legacy);
        await f.Db.SaveChangesAsync();
        var latest = await f.PrepareCreate();
        Assert.Equal(latest.Id, first.SupersededById);
        Assert.Equal(latest.Id, legacy.SupersededById);
        Assert.Single(await f.Db.GetUnresolvedPendingCalendarActionsAsync(f.ConversationId));
        Assert.True((await f.Confirm.ExecuteAsync(Empty, await f.Context("Vai"))).Success);
        Assert.Empty(await f.Db.GetUnresolvedPendingCalendarActionsAsync(f.ConversationId));
        Assert.Single(f.Google.Events);
    }

    [Fact]
    public async Task AmendedDateRechecksHolidaysAndCanSwitchToAllDay()
    {
        using var f = new Fixture();
        const string holidayCalendar = "en.usa#holiday@group.v.calendar.google.com";
        f.Google.AddCalendar(holidayCalendar, "Feriados", "reader");
        AddHoliday(f.Google, holidayCalendar, "h", "Dia local", "2026-10-12");
        var first = await f.PrepareCreate();
        Assert.Empty(Payload(first).HolidayWarnings!);
        var result = await new CalendarAmendPendingActionTool(f.Db, f.Calendar, f.ToolContext).ExecuteAsync(
            Args(new { start = "2026-10-12", end = "2026-10-12", allDay = true }), await f.Context());
        Assert.True(result.Success);
        Assert.Single(JsonDocument.Parse(result.Content).RootElement.GetProperty("holidayWarnings").EnumerateArray());
        var latest = (await f.Db.GetLatestOpenPendingCalendarActionAsync(f.ConversationId))!;
        Assert.Equal("2026-10-13", Payload(latest).Fields["end"]!["date"]!.ToString());
        Assert.Null(Payload(latest).Fields["start"]!["timeZone"]);
        Assert.True((await f.Confirm.ExecuteAsync(Empty, await f.Context("Beleza"))).Success);
    }

    [Fact]
    public async Task AmendmentRequiresValidPendingActionAndObservedCalendarDestination()
    {
        using var f = new Fixture();
        var amend = new CalendarAmendPendingActionTool(f.Db, f.Calendar, f.ToolContext);
        Assert.Equal("no_pending_calendar_action", (await amend.ExecuteAsync(Args(new { summary = "A" }), await f.Context())).ErrorCode);
        var action = await f.PrepareCreate();
        Assert.False((await amend.ExecuteAsync(Args(new { calendarId = "invented" }), await f.Context())).Success);
        Assert.False((await amend.ExecuteAsync(Empty, await f.Context())).Success);
        Assert.Null(action.SupersededAt);
        f.Google.AddCalendar("diario", "Diario");
        await new CalendarListCalendarsTool(f.Calendar, f.ToolContext).ExecuteAsync(Empty, await f.Context());
        Assert.True((await amend.ExecuteAsync(Args(new { calendarId = "diario" }), await f.Context("Coloca no Diario"))).Success);
        Assert.Equal("diario", (await f.Db.GetLatestOpenPendingCalendarActionAsync(f.ConversationId))!.CalendarId);
    }

    [Theory]
    [InlineData("Faz como achar melhor", "2026-10-31T19:00:00-03:00", "2026-10-31T23:00:00-03:00", false)]
    [InlineData("Tanto faz o horário", "2026-10-29", "2026-10-29", true)]
    public async Task ConcreteValuesChosenByModelAreAcceptedWithNormalTechnicalValidation(string content, string start, string end, bool allDay)
    {
        using var f = new Fixture();
        var result = await f.Create.ExecuteAsync(Args(new { summary = "Evento", start, end, allDay }), await f.Context(content));
        Assert.True(result.Success);
        Assert.Single(f.Db.PendingCalendarActions);
        Assert.Equal(0, f.Google.Mutations);
    }

    private static CalendarActionPayload Payload(PendingCalendarAction action) =>
        JsonSerializer.Deserialize<CalendarActionPayload>(action.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static JsonObject AddHoliday(GoogleHandler google, string calendarId, string id, string name, string date, string? exclusiveEnd = null)
    {
        var item = google.AddEvent(id, calendarId);
        item["summary"] = name;
        item["start"] = new JsonObject { ["date"] = date };
        item["end"] = new JsonObject { ["date"] = exclusiveEnd ?? DateOnly.Parse(date).AddDays(1).ToString("yyyy-MM-dd") };
        return item;
    }

    private static void SetTiming(JsonObject item, string start, string end)
    {
        item["start"]!["dateTime"] = start;
        item["end"]!["dateTime"] = end;
    }

    private sealed class Fixture : IDisposable
    {
        public AegisDbContext Db { get; } = new(new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public GoogleHandler Google { get; } = new();
        public HttpClient Http { get; }
        public EmailTokenProtector Protector { get; }
        public GoogleAccessTokenProvider Tokens { get; }
        public GmailConnectionService Connection { get; }
        public RecordingLogger Log { get; } = new();
        public GoogleCalendarService Calendar { get; }
        public CalendarToolContextService ToolContext { get; }
        public CalendarCreateEventTool Create { get; }
        public CalendarUpdateEventTool Update { get; }
        public CalendarConfirmPendingActionTool Confirm { get; }
        public Guid ConversationId { get; }
        public Fixture(string scopes = GoogleScopes.Combined, bool expired = false, GoogleCalendarOptions? calendarOptions = null)
        {
            var data = DataProtectionProvider.Create("Aegis.Calendar.Tests");
            Protector = new(data);
            var conversation = new Conversation("Calendar");
            ConversationId = conversation.Id;
            Db.AddConversation(conversation);
            Db.AddEmailAccountConnection(new("gmail", "name@gmail.com", Protector.Protect("access"), Protector.Protect("refresh"),
                DateTimeOffset.UtcNow.AddMinutes(expired ? -1 : 60), scopes));
            Db.SaveChanges();
            Http = new(Google);
            var options = Options.Create(new GmailOptions { ClientId = "test", ClientSecret = "test", RedirectUri = "https://api.example.test/api/email/oauth/callback", Scopes = GoogleScopes.Gmail });
            Tokens = new(Db, Http, options, Protector);
            Connection = new(Db, Http, options, Protector, data);
            Calendar = new(Http, Tokens, Connection, Log, Options.Create(calendarOptions ?? new GoogleCalendarOptions()));
            ToolContext = new(Db);
            Create = new(Db, Calendar, ToolContext);
            Update = new(Db, Calendar, ToolContext);
            Confirm = new(Db, Calendar, ToolContext, NullLogger<CalendarConfirmPendingActionTool>.Instance);
        }
        public async Task<ToolExecutionContext> Context(string content = "prepare")
        {
            var message = new ChatMessage(ConversationId, ChatRoles.User, content);
            Db.AddChatMessage(message);
            await Db.SaveChangesAsync();
            return new(ConversationId, message.Id, content);
        }
        public Task Observe() => ToolContext.RememberAsync(ConversationId, Google.Events.Values.Select(e => new CalendarEventData(e["id"]!.ToString(), "primary", "Principal", "Evento", Start, End, false, "America/Sao_Paulo", null, null, "confirmed", null)), "calendar_list_events");
        public async Task<PendingCalendarAction> PrepareCreate()
        {
            Assert.True((await Create.ExecuteAsync(Args(new { summary = "Dentista", start = Start, end = End, allDay = false }), await Context())).Success);
            return (await Db.GetLatestOpenPendingCalendarActionAsync(ConversationId))!;
        }
        public void Dispose() { Http.Dispose(); Db.Dispose(); }
    }

    private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger<GoogleCalendarService>
    {
        public List<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    private sealed class GoogleHandler : HttpMessageHandler
    {
        public Dictionary<string, JsonObject> Events { get; } = new();
        public Dictionary<string, JsonObject> Calendars { get; } = new();
        public Dictionary<string, Dictionary<string, JsonObject>> SecondaryEvents { get; } = new();
        public List<(string CalendarId, string Query)> ListRequests { get; } = new();
        public List<string> MutationCalendars { get; } = new();
        public int CalendarPageSize { get; set; } = 250;
        public int EventPageSize { get; set; } = 50;
        public string? FailureOperation { get; set; }
        public HttpStatusCode FailureStatus { get; set; } = HttpStatusCode.Forbidden;
        public string FailureBody { get; set; } = "{}";
        public string OAuthScopes { get; set; } = GoogleScopes.Combined;
        public string ProfileEmail { get; set; } = "name@gmail.com";
        public int Refreshes { get; private set; }
        public HttpStatusCode RefreshStatus { get; set; } = HttpStatusCode.OK;
        public int Mutations { get; private set; }
        public int Gets { get; private set; }
        public int CalendarRequests { get; private set; }
        public Action<JsonObject>? AdjustRemindersAfterMutation { get; set; }
        public string? LastListQuery { get; private set; }
        public string? LastIfMatch { get; private set; }
        public string? LastMutationQuery { get; private set; }
        public bool ThrowAfterMutation { get; set; }
        public bool FailGetsAfterMutation { get; set; }
        public int MutationCountBeforeFailedGets { get; set; }
        public bool IgnorePut { get; set; }
        public bool TimeoutGetsAfterMutation { get; set; }
        public CancellationTokenSource? CancelOnGetAfterMutation { get; set; }
        public CancellationTokenSource? CancelAfterMutation { get; set; }
        public GoogleHandler() => AddCalendar("primary", "Principal", "owner", true);
        public JsonObject AddCalendar(string id, string name, string role = "writer", bool primary = false)
        {
            var item = new JsonObject { ["id"] = id, ["summary"] = name, ["primary"] = primary, ["accessRole"] = role, ["timeZone"] = "America/Sao_Paulo" };
            Calendars[id] = item;
            if (id != "primary") SecondaryEvents.TryAdd(id, new());
            return item;
        }
        public Dictionary<string, JsonObject> EventsFor(string calendarId) => calendarId == "primary" || Calendars[calendarId]["primary"]?.GetValue<bool>() == true ? Events : SecondaryEvents[calendarId];
        public JsonObject AddEvent(string id = "event1", string calendarId = "primary")
        {
            var item = JsonNode.Parse("""
                {"id":"event1","summary":"Evento","start":{"dateTime":"2026-10-10T14:00:00-03:00","timeZone":"America/Sao_Paulo"},
                "end":{"dateTime":"2026-10-10T15:00:00-03:00","timeZone":"America/Sao_Paulo"},"status":"confirmed","etag":"\"v1\"","location":"Campus"}
                """)!.AsObject();
            item["id"] = id;
            EventsFor(calendarId)[id] = item;
            return item;
        }
        private static HttpResponseMessage Response(HttpStatusCode status, string? json = null) => new(status)
        { Content = new StringContent(json ?? "{}", Encoding.UTF8, "application/json") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Host == "oauth2.googleapis.com")
            {
                var form = await request.Content!.ReadAsStringAsync(cancellationToken);
                if (form.Contains("refresh_token")) { Refreshes++; return Response(RefreshStatus, """{"access_token":"renewed","expires_in":3600}"""); }
                return Response(HttpStatusCode.OK, JsonSerializer.Serialize(new { access_token = "reauthorized", expires_in = 3600, scope = OAuthScopes }));
            }
            if (uri.Host == "gmail.googleapis.com") return Response(HttpStatusCode.OK,
                uri.AbsolutePath.EndsWith("profile") ? JsonSerializer.Serialize(new { emailAddress = ProfileEmail }) : """{"messages":[],"resultSizeEstimate":0}""");
            Assert.Equal("www.googleapis.com", uri.Host);
            CalendarRequests++;
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
                .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
            var calendarList = uri.AbsolutePath.Contains("/calendarList");
            var collection = uri.AbsolutePath.EndsWith("/events");
            var operation = calendarList ? (uri.AbsolutePath.EndsWith("/calendarList") ? "calendarList.list" : "calendarList.get")
                : request.Method == HttpMethod.Get ? (collection ? "events.list" : "events.get")
                : request.Method == HttpMethod.Post ? "events.insert" : request.Method == HttpMethod.Put ? "events.update" : "events.delete";
            if (operation == FailureOperation) return Response(FailureStatus, FailureBody);
            if (calendarList)
            {
                if (operation == "calendarList.get")
                {
                    var requested = Uri.UnescapeDataString(uri.Segments.Last());
                    var calendar = requested == "primary" ? Calendars.Values.SingleOrDefault(c => c["primary"]?.GetValue<bool>() == true) : Calendars.GetValueOrDefault(requested);
                    return calendar is not null ? Response(HttpStatusCode.OK, calendar.ToJsonString()) : Response(HttpStatusCode.NotFound);
                }
                Assert.Equal("true", query["showHidden"]);
                return Page(Calendars.Values.ToList(), query, CalendarPageSize);
            }
            var calendarId = Uri.UnescapeDataString(uri.Segments[4].TrimEnd('/'));
            if (!Calendars.ContainsKey(calendarId) && calendarId != "primary") return Response(HttpStatusCode.NotFound);
            var events = EventsFor(calendarId);
            var id = Uri.UnescapeDataString(uri.Segments.Last());
            if (request.Method == HttpMethod.Get)
            {
                Gets++;
                if (Mutations > 0 && CancelOnGetAfterMutation is not null)
                { CancelOnGetAfterMutation.Cancel(); throw new OperationCanceledException(cancellationToken); }
                if (Mutations > 0 && TimeoutGetsAfterMutation) throw new TaskCanceledException("Simulated verification timeout");
                if (FailGetsAfterMutation && Mutations > MutationCountBeforeFailedGets) return Response(HttpStatusCode.ServiceUnavailable);
                if (!collection) return events.TryGetValue(id, out var item) ? Response(HttpStatusCode.OK, item.ToJsonString()) : Response(HttpStatusCode.NotFound);
                LastListQuery = uri.Query;
                ListRequests.Add((calendarId, uri.Query));
                var items = events.Values.Where(e => e["status"]?.ToString() != "cancelled");
                if (query.TryGetValue("q", out var text)) items = items.Where(e => new[] { "summary", "description", "location" }.Any(k => e[k]?.ToString().Contains(text, StringComparison.OrdinalIgnoreCase) == true));
                var zone = (Calendars.GetValueOrDefault(calendarId) ?? Calendars.Values.Single(c => c["primary"]?.GetValue<bool>() == true))["timeZone"]!.ToString();
                if (query.TryGetValue("timeMin", out var min)) items = items.Where(e => Instant(e, "end", zone) > DateTimeOffset.Parse(min));
                if (query.TryGetValue("timeMax", out var max)) items = items.Where(e => Instant(e, "start", zone) < DateTimeOffset.Parse(max));
                return Page(items.OrderBy(e => Instant(e, "start", zone)).ToList(), query, EventPageSize);
            }
            Mutations++;
            MutationCalendars.Add(calendarId);
            LastMutationQuery = uri.Query;
            LastIfMatch = request.Headers.TryGetValues("If-Match", out var values) ? values.Single() : null;
            JsonObject? resource = null;
            if (request.Method != HttpMethod.Delete) resource = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            if (request.Method == HttpMethod.Post)
            {
                id = resource!["id"]!.ToString();
                if (events.ContainsKey(id)) return Response(HttpStatusCode.Conflict);
                events[id] = resource;
            }
            else if (request.Method == HttpMethod.Put) { if (!IgnorePut) events[id] = resource!; }
            else events.Remove(id);
            if (request.Method != HttpMethod.Delete) AdjustRemindersAfterMutation?.Invoke(events[id]);
            if (CancelAfterMutation is not null) { CancelAfterMutation.Cancel(); throw new OperationCanceledException(cancellationToken); }
            if (ThrowAfterMutation) throw new HttpRequestException("Simulated lost response");
            return request.Method == HttpMethod.Delete ? Response(HttpStatusCode.NoContent) : Response(HttpStatusCode.OK, resource!.ToJsonString());
        }
        private static DateTimeOffset Instant(JsonObject item, string field, string timeZone)
        {
            if (item[field]!["dateTime"] is JsonNode time) return DateTimeOffset.Parse(time.ToString());
            var date = DateOnly.Parse(item[field]!["date"]!.ToString()).ToDateTime(TimeOnly.MinValue);
            return new(date, TimeZoneInfo.FindSystemTimeZoneById(timeZone).GetUtcOffset(date));
        }
        private static HttpResponseMessage Page(List<JsonObject> items, Dictionary<string, string> query, int pageSize)
        {
            var offset = query.TryGetValue("pageToken", out var token) ? int.Parse(token) : 0;
            var size = Math.Min(pageSize, query.TryGetValue("maxResults", out var max) ? int.Parse(max) : 250);
            return Response(HttpStatusCode.OK, new JsonObject { ["items"] = new JsonArray(items.Skip(offset).Take(size).Select(e => e.DeepClone()).ToArray()),
                ["nextPageToken"] = offset + size < items.Count ? (offset + size).ToString() : null }.ToJsonString());
        }
    }
}
