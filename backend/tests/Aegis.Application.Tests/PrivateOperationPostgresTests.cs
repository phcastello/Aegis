using Aegis.Domain;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Chat;
using Aegis.Infrastructure.Models;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Aegis.Application.Tests;

// The operation check uses only random schemas in an explicitly named disposable DB.
public sealed class PrivateOperationPostgresTests
{
    private sealed class OperationFactAttribute : FactAttribute
    {
        public OperationFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEGIS_OPERATION_TEST_DATABASE")))
                Skip = "Set AEGIS_OPERATION_TEST_DATABASE to the disposable aegis_operation_test database.";
        }
    }

    private static async Task WithSchema(Func<Func<AegisDbContext>, Task> test)
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("AEGIS_OPERATION_TEST_DATABASE"));
        if (connection.Database != "aegis_operation_test")
            throw new InvalidOperationException("Only the disposable aegis_operation_test database is accepted.");
        var schema = "operation_" + Guid.NewGuid().ToString("N");
        connection.SearchPath = schema;
        AegisDbContext Create() => new(new DbContextOptionsBuilder<AegisDbContext>().UseNpgsql(connection.ConnectionString,
            options => options.MigrationsHistoryTable("__EFMigrationsHistory", schema)).Options);
        await using var initial = Create();
        await initial.Database.ExecuteSqlRawAsync("CREATE SCHEMA \"" + schema + "\"");
        try { await test(Create); }
        finally { await initial.Database.ExecuteSqlRawAsync("DROP SCHEMA \"" + schema + "\" CASCADE"); }
    }

    [OperationFact]
    public Task FreshDatabaseMigrationsFinishAndSecondRunIsIdempotent() => WithSchema(async create =>
    {
        await using var first = create();
        Assert.NotEmpty(await first.Database.GetPendingMigrationsAsync());
        await first.Database.MigrateAsync(); // Same operation called by NodeAdmin migrate.
        var applied = (await first.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.NotEmpty(applied);
        Assert.Empty(await first.Database.GetPendingMigrationsAsync());
        await using var restarted = create();
        await restarted.Database.MigrateAsync();
        Assert.Equal(applied, await restarted.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await restarted.Database.GetPendingMigrationsAsync());
    });

    [OperationFact]
    public Task ExistingDatabaseUpgradePreservesConversationAndMessageAcrossRestartAndRepeat() => WithSchema(async create =>
    {
        var conversationId = Guid.NewGuid(); var messageId = Guid.NewGuid();
        var createdAt = DateTimeOffset.Parse("2026-10-21T12:00:00Z");
        await using (var previous = create())
        {
            await previous.GetService<IMigrator>().MigrateAsync("20260604044343_InitialChatPersistence");
            await previous.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO conversations ("Id", "Title", "CreatedAt", "UpdatedAt")
                VALUES ({conversationId}, 'Synthetic upgrade conversation', {createdAt}, {createdAt})
                """);
            await previous.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO chat_messages ("Id", "ConversationId", "Role", "Content", "CreatedAt", "UpdatedAt")
                VALUES ({messageId}, {conversationId}, 'user', 'Synthetic preserved message', {createdAt}, {createdAt})
                """);
        }
        await using (var upgraded = create())
        {
            Assert.NotEmpty(await upgraded.Database.GetPendingMigrationsAsync());
            await upgraded.Database.MigrateAsync();
        }
        await using var restarted = create();
        var conversation = Assert.Single(await restarted.Conversations.ToListAsync());
        Assert.Equal(conversationId, conversation.Id);
        Assert.Equal("Synthetic upgrade conversation", conversation.Title);
        Assert.Equal(createdAt, conversation.CreatedAt);
        var message = Assert.Single(await restarted.ChatMessages.ToListAsync());
        Assert.Equal(messageId, message.Id); Assert.Equal(conversationId, message.ConversationId);
        Assert.Equal("Synthetic preserved message", message.Content);
        var applied = (await restarted.Database.GetAppliedMigrationsAsync()).ToArray();
        await restarted.Database.MigrateAsync();
        Assert.Equal(applied, await restarted.Database.GetAppliedMigrationsAsync());
        Assert.Equal(1, await restarted.Conversations.CountAsync());
        Assert.Equal(1, await restarted.ChatMessages.CountAsync());
    });

    [OperationFact]
    public Task WebSearchUsageUpsertsKeepUniqueCallsAndAggregateExactUtcRanges() => WithSchema(async create =>
    {
        var first = DateTimeOffset.Parse("2026-11-25T23:59:59Z");
        var boundary = DateTimeOffset.Parse("2026-11-26T00:00:00Z");
        var end = boundary.AddDays(1);
        await using (var db = create())
        {
            await db.Database.MigrateAsync();
            Assert.True(await WebSearchUsageRecorder.UpsertAsync(db, new("ws_prev", null, "unknown", "in_progress", first)));
            Assert.True(await WebSearchUsageRecorder.UpsertAsync(db, new("ws_prev", "resp_prev", "search", "completed", boundary)));
            Assert.True(await WebSearchUsageRecorder.UpsertAsync(db, new("ws_prev", "resp_prev", "unknown", "in_progress", boundary.AddSeconds(2))));
            Assert.True(await WebSearchUsageRecorder.UpsertAsync(db, new("ws_search", "resp_1", "search", "completed", boundary)));
            Assert.True(await WebSearchUsageRecorder.UpsertAsync(db, new("ws_open", "resp_1", "open_page", "completed", boundary.AddHours(1))));
            Assert.True(await WebSearchUsageRecorder.UpsertAsync(db, new("ws_find", "resp_1", "find_in_page", "failed", boundary.AddHours(1))));
            Assert.True(await WebSearchUsageRecorder.UpsertAsync(db, new("ws_unknown", null, "unknown", "in_progress", boundary.AddHours(2))));
            Assert.True(await WebSearchUsageRecorder.UpsertAsync(db, new("ws_other", "resp_other", "other", "incomplete", boundary.AddHours(2))));
            Assert.True(await WebSearchUsageRecorder.UpsertAsync(db, new("ws_next", "resp_next", "search", "completed", end)));
        }
        // Replay from separate contexts concurrently; the unique key handles both.
        async Task<bool> Replay()
        {
            await using var db = create();
            return await WebSearchUsageRecorder.UpsertAsync(db, new("ws_open", "resp_1", "open_page", "completed", boundary.AddHours(3)));
        }
        Assert.All(await Task.WhenAll(Replay(), Replay()), result => Assert.True(result));

        await using var restarted = create();
        var conflict = await WebSearchUsageRecorder.UpsertAsync(restarted,
            new("ws_open", "different_response", "search", "completed", boundary));
        Assert.False(conflict);
        var old = await restarted.WebSearchUsage.SingleAsync(item => item.CallId == "ws_prev");
        Assert.Equal("resp_prev", old.ResponseId);
        Assert.Equal("search", old.ActionType);
        Assert.Equal("completed", old.Status);
        Assert.Equal(first, old.FirstObservedAt);
        Assert.Equal(boundary.AddSeconds(2), old.LastObservedAt);
        var report = await WebSearchUsageReport.ReadAsync(restarted, boundary, end);
        Assert.Equal(1, report.Search);
        Assert.Equal(1, report.OpenPage);
        Assert.Equal(1, report.FindInPage);
        Assert.Equal(1, report.Unknown);
        Assert.Equal(1, report.Other);
        Assert.Equal(5, report.Total);
        Assert.Equal(2, report.Statuses["completed"]);
        Assert.Equal(1, report.Statuses["failed"]);
        Assert.Equal(1, report.Statuses["in_progress"]);
        Assert.Equal(1, report.Statuses["incomplete"]);
        var before = await WebSearchUsageReport.ReadAsync(restarted, boundary.AddDays(-1), boundary);
        Assert.Equal(1, before.Total);
        Assert.Equal(1, before.Search);
        Assert.Equal(7, await restarted.WebSearchUsage.CountAsync());
        await restarted.Database.MigrateAsync();
        Assert.Equal(5, (await WebSearchUsageReport.ReadAsync(restarted, boundary, end)).Total);
    });

    [OperationFact]
    public Task ResearchJobMigrationsAndConcurrentReplayPreserveSingleCanonicalRequest() => WithSchema(async create =>
    {
        await using (var initial = create())
        {
            await initial.GetService<IMigrator>().MigrateAsync("20261126180000_AddWebSearchUsage");
            Assert.Contains("20261126202048_AddBackgroundResearchJobs", await initial.Database.GetPendingMigrationsAsync());
            await initial.Database.MigrateAsync();
        }

        var request = new CreateResearchJobRequest(Guid.NewGuid(), null, "Synthetic background research");
        async Task<ResearchJobSnapshot> Start()
        {
            await using var db = create();
            return await new ResearchJobStore(db, TimeProvider.System).CreateAsync(request, CancellationToken.None);
        }
        var snapshots = await Task.WhenAll(Start(), Start());
        Assert.Equal(snapshots[0].Id, snapshots[1].Id);
        Assert.Equal(snapshots[0].ConversationId, snapshots[1].ConversationId);
        Assert.Equal(snapshots[0].UserMessageId, snapshots[1].UserMessageId);

        await using var restarted = create();
        await restarted.Database.MigrateAsync();
        Assert.Empty(await restarted.Database.GetPendingMigrationsAsync());
        Assert.Equal(1, await restarted.ResearchJobs.CountAsync());
        Assert.Equal(1, await restarted.ChatMessages.CountAsync());
        Assert.Equal(1, await restarted.Conversations.CountAsync());
        var store = new ResearchJobStore(restarted, TimeProvider.System);
        Assert.Equal("queued", (await store.GetAsync(request.JobId, CancellationToken.None))!.State);
        Assert.Equal("cancelled", (await store.CancelAsync(request.JobId, CancellationToken.None))!.State);
        Assert.Equal("cancelled", Assert.Single(await store.ListAsync(snapshots[0].ConversationId, CancellationToken.None)).State);
    });

    [OperationFact]
    public Task BackgroundJobClaimsRespectTheEnabledToolWithoutASeparateSchema() => WithSchema(async create =>
    {
        const string original = "Analise {\"values\":[5,2,9]}.";
        Guid pythonId;
        Guid webId;
        await using (var setup = create())
        {
            await setup.Database.MigrateAsync();
            var store = new ResearchJobStore(setup, TimeProvider.System);
            webId = (await store.CreateAsync(new(Guid.NewGuid(), null, "Consulte uma fonte oficial."), CancellationToken.None)).Id;
            var conversation = new Conversation(Conversation.DefaultTitle);
            var message = conversation.AddMessage(ChatRoles.User, original);
            setup.Conversations.Add(conversation);
            setup.ChatMessages.Add(message);
            await setup.SaveChangesAsync();
            store.Stage(message, "Calcule a mediana.", isPython: true);
            await setup.SaveChangesAsync();
            pythonId = message.Id;
        }

        await using (var db = create())
        {
            var store = new ResearchJobStore(db, TimeProvider.System);
            var python = await store.ClaimAsync(CancellationToken.None, allowWeb: false, allowPython: true);
            Assert.Equal(pythonId, python!.JobId);
            Assert.True(python.IsPython);
            Assert.Contains(original, python.Content);
            Assert.Null(await store.ClaimAsync(CancellationToken.None, allowWeb: false, allowPython: true));
            Assert.Equal(ResearchJob.Queued, (await store.GetAsync(webId, CancellationToken.None))!.State);
        }

        await using (var db = create())
        {
            var store = new ResearchJobStore(db, TimeProvider.System);
            var web = await store.ClaimAsync(CancellationToken.None, allowWeb: true, allowPython: false);
            Assert.Equal(webId, web!.JobId);
            Assert.False(web.IsPython);
            Assert.Equal("python", (await store.GetAsync(pythonId, CancellationToken.None))!.Kind);
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        }
    });

}
