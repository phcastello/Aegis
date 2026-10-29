using System.Text.Json;
using System.Text.Json.Nodes;
using Aegis.Application.Calendar;
using Aegis.Application.Calendar.Tools;
using Aegis.Application.Tools;
using Aegis.Domain;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class CalendarImmediatePostgresTests
{
    [NodePostgresTests.PostgresFact]
    public async Task PersistedRequestRecoversAfterContextRestartWithoutRepeatingExternalMutation()
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("AEGIS_NODE_TEST_DATABASE"));
        if (connection.Database != "aegis_nodes_test") throw new InvalidOperationException("Only disposable aegis_nodes_test is accepted.");
        var schema = "calendar_immediate_" + Guid.NewGuid().ToString("N");
        connection.SearchPath = schema;
        AegisDbContext Create() => new(new DbContextOptionsBuilder<AegisDbContext>().UseNpgsql(connection.ConnectionString,
            options => options.MigrationsHistoryTable("__EFMigrationsHistory", schema)).Options);
        await using var setup = Create();
        await setup.Database.ExecuteSqlRawAsync("CREATE SCHEMA \"" + schema + "\"");
        try
        {
            await setup.Database.MigrateAsync();
            var remote = new RemoteCalendar();
            Guid conversationId;
            Guid pendingId;
            string eventId;
            var args = JsonSerializer.SerializeToElement(new { summary = "Synthetic exam", start = "2026-11-05T10:00:00-03:00", end = "2026-11-05T12:00:00-03:00", allDay = false });
            await using (var db = Create())
            {
                var conversation = new Conversation("Disposable Calendar test");
                conversationId = conversation.Id;
                db.AddConversation(conversation);
                var message = new ChatMessage(conversation.Id, ChatRoles.User, "Create the synthetic exam.");
                db.AddChatMessage(message);
                await db.SaveChangesAsync();
                var tool = new CalendarCreateEventTool(db, remote, new(db));
                var result = await tool.ExecuteAsync(args, new(conversation.Id, message.Id, message.Content));
                Assert.Equal("calendar_action_outcome_unknown", result.ErrorCode);
                var pending = await db.PendingCalendarActions.SingleAsync();
                pendingId = pending.Id;
                eventId = pending.EventId;
                Assert.True(pending.MayHaveAppliedChanges);
                Assert.Null(pending.ExecutedAt);
                Assert.Single(await db.CalendarActionAudits.ToListAsync());
            }
            remote.VerificationAvailable = true;
            await using (var restarted = Create())
            {
                var message = new ChatMessage(conversationId, ChatRoles.User, "Retry the synthetic exam.");
                restarted.AddChatMessage(message);
                await restarted.SaveChangesAsync();
                var result = await new CalendarCreateEventTool(restarted, remote, new(restarted)).ExecuteAsync(args,
                    new(conversationId, message.Id, message.Content));
                Assert.True(result.Success);
                var pending = await restarted.PendingCalendarActions.SingleAsync();
                Assert.Equal(pendingId, pending.Id);
                Assert.Equal(eventId, pending.EventId);
                Assert.NotNull(pending.ExecutedAt);
                Assert.Null(pending.ConfirmedAt);
                var payload = JsonSerializer.Deserialize<CalendarActionPayload>(pending.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                Assert.NotNull(payload.CreationRequest);
                Assert.Equal(1, remote.Inserts);
                Assert.Equal(2, await restarted.CalendarActionAudits.CountAsync());
                Assert.True(await restarted.CalendarActionAudits.AnyAsync(a => a.Success && a.UserConfirmationMessageId == message.Id));
            }
        }
        finally { await setup.Database.ExecuteSqlRawAsync("DROP SCHEMA IF EXISTS \"" + schema + "\" CASCADE"); }
    }

    private sealed class RemoteCalendar : ICalendarService
    {
        public int Inserts;
        public bool VerificationAvailable;
        private readonly HashSet<string> events = [];
        public Task<CalendarActionPayload> PrepareAsync(string type, string calendarId, string eventId, CalendarEventChanges changes, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CalendarActionPayload("synthetic@example.test", null,
                new JsonObject { ["summary"] = changes.Summary, ["start"] = changes.Start, ["end"] = changes.End }, "Synthetic exam 10–12"));
        public Task ExecuteAsync(string type, string calendarId, string eventId, CalendarActionPayload payload, CancellationToken cancellationToken = default)
        {
            Inserts++;
            events.Add(eventId);
            throw new CalendarMutationException("calendar_temporarily_unavailable", "Simulated lost response", true);
        }
        public Task<bool> VerifyAsync(string type, string calendarId, string eventId, CalendarActionPayload payload, CancellationToken cancellationToken = default) =>
            VerificationAvailable ? Task.FromResult(events.Contains(eventId)) : throw new CalendarException("calendar_temporarily_unavailable", "Simulated verification outage");
        public Task<CalendarEventData> GetEventAsync(string calendarId, string eventId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CalendarEventData(eventId, calendarId, "Synthetic", "Synthetic exam", null, null, false, "America/Sao_Paulo", null, null, "confirmed", null));
        public Task<CalendarConnectionStatus> GetStatusAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CalendarListData> ListCalendarsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CalendarEventList> ListEventsAsync(string? timeMin, string? timeMax, string? query, int limit = 20, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
