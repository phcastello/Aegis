using Aegis.Application.Nodes;
using Aegis.Infrastructure.Nodes;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;
namespace Aegis.Application.Tests;

public sealed class NodePostgresTests
{
    public sealed class PostgresFactAttribute : FactAttribute
    {
        public PostgresFactAttribute() { if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEGIS_NODE_TEST_DATABASE"))) Skip = "Requires disposable AEGIS_NODE_TEST_DATABASE."; }
    }
    internal static async Task WithDatabase(Func<Func<AegisDbContext>, Task> test)
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("AEGIS_NODE_TEST_DATABASE"));
        if (connection.Database != "aegis_nodes_test") throw new InvalidOperationException("Only the disposable aegis_nodes_test database is accepted.");
        var schema = "nodes_" + Guid.NewGuid().ToString("N");
        connection.SearchPath = schema;
        AegisDbContext Create() => new(new DbContextOptionsBuilder<AegisDbContext>().UseNpgsql(connection.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", schema)).Options);
        await using var db = Create(); await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA \"" + schema + "\"");
        try { await db.Database.MigrateAsync(); await test(Create); }
        finally { await db.Database.ExecuteSqlRawAsync("DROP SCHEMA \"" + schema + "\" CASCADE"); }
    }
    [PostgresFact] public Task TwoDevicesCannotConsumeSameCodeConcurrently() => WithDatabase(async create => {
        await using var initial = create(); var registry = new NodeRegistry(initial, TimeProvider.System);
        var code = await registry.CreateCodeAsync(null);
        async Task<PairNodeReceipt?> Pair() { await using var db = create(); try { return await new NodeRegistry(db, TimeProvider.System).PairAsync(NodeIdentityTests.Request(code.Code)); } catch (NodeException) { return null; } }
        var receipts = await Task.WhenAll(Pair(), Pair()); Assert.Single(receipts, r => r is not null);
        Assert.Equal(1, await initial.NodePairingAttempts.CountAsync()); Assert.Empty(initial.Nodes);
    });
    [PostgresFact] public Task ConcurrentRetryAndFinalizeCreateExactlyOneIdentity() => WithDatabase(async create => {
        await using var initial = create(); var registry = new NodeRegistry(initial, TimeProvider.System);
        var request = NodeIdentityTests.Request((await registry.CreateCodeAsync(null)).Code);
        async Task<PairNodeReceipt> Pair() { await using var db = create(); return await new NodeRegistry(db, TimeProvider.System).PairAsync(request); }
        var receipts = await Task.WhenAll(Pair(), Pair()); Assert.Equal(receipts[0], receipts[1]);
        async Task<NodeView> Finalize() { await using var db = create(); return await new NodeRegistry(db, TimeProvider.System).FinalizeAsync(new(request.AttemptId, receipts[0].Credential)); }
        var nodes = await Task.WhenAll(Finalize(), Finalize()); Assert.Equal(nodes[0], nodes[1]);
        Assert.Single(await initial.Nodes.ToListAsync()); Assert.Empty(initial.NodePairingAttempts);
        await registry.RevokeAsync(nodes[0].Id, nodes[0].Id);
        await using var restart = create(); Assert.Equal("node_revoked", (await new NodeRegistry(restart, TimeProvider.System).AuthenticateAsync(receipts[0].Credential)).Error);
    });
    [PostgresFact] public Task RealDatabaseCommitsFailedAttemptBudgetAndEnforcesConstraints() => WithDatabase(async create => {
        await using var db = create(); var registry = new NodeRegistry(db, TimeProvider.System);
        var code = (await registry.CreateCodeAsync(null)).Code; var wrong = code[..^1] + (code[^1] == '0' ? '1' : '0');
        for (var i = 0; i < 5; i++) await Assert.ThrowsAsync<NodeException>(() => registry.PairAsync(NodeIdentityTests.Request(wrong)));
        await Assert.ThrowsAsync<NodeException>(() => registry.PairAsync(NodeIdentityTests.Request(code)));
        Assert.Equal(5, (await db.NodePairingCodes.AsNoTracking().SingleAsync()).FailedAttempts);
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE node_pairing_codes SET \"FailedAttempts\" = 6"));
        Assert.Empty(db.Nodes);
    });
}
