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
}
