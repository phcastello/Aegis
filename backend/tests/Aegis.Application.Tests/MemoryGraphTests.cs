using Aegis.Application.Memory;
using Aegis.Application.Observability;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Memory;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class MemoryGraphTests
{
    public sealed class GraphFactAttribute : FactAttribute
    {
        public GraphFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEGIS_MEMORY_TEST_NEO4J_URI")) ||
                Environment.GetEnvironmentVariable("AEGIS_MEMORY_TEST_NEO4J_DISPOSABLE") != "YES_DELETE_AEGIS_PROJECTION")
                Skip = "Set disposable Neo4j URI and AEGIS_MEMORY_TEST_NEO4J_DISPOSABLE=YES_DELETE_AEGIS_PROJECTION.";
        }
    }
    public sealed class PostgresFactAttribute : FactAttribute
    {
        public PostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEGIS_MEMORY_TEST_DATABASE")))
                Skip = "Set AEGIS_MEMORY_TEST_DATABASE for disposable PostgreSQL.";
        }
    }
    public sealed class BothFactAttribute : FactAttribute
    {
        public BothFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEGIS_MEMORY_TEST_DATABASE")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEGIS_MEMORY_TEST_NEO4J_URI")) ||
                Environment.GetEnvironmentVariable("AEGIS_MEMORY_TEST_NEO4J_DISPOSABLE") != "YES_DELETE_AEGIS_PROJECTION")
                Skip = "Set disposable PostgreSQL and Neo4j, with AEGIS_MEMORY_TEST_NEO4J_DISPOSABLE=YES_DELETE_AEGIS_PROJECTION.";
        }
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static AegisDbContext Db(string schema) => new(new DbContextOptionsBuilder<AegisDbContext>()
        .UseNpgsql(Environment.GetEnvironmentVariable("AEGIS_MEMORY_TEST_DATABASE") + ";Search Path=" + schema,
            options => options.MigrationsHistoryTable("__EFMigrationsHistory", schema)).Options);
    private static async Task<AegisDbContext> CreateDbAsync(string schema)
    {
        var db = Db(schema);
        await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA \"" + schema + "\"");
        await db.Database.MigrateAsync();
        return db;
    }
    private static async Task DropDbAsync(AegisDbContext db, string schema)
    {
        await db.Database.ExecuteSqlRawAsync("DROP SCHEMA \"" + schema + "\" CASCADE");
        await db.DisposeAsync();
    }
    private static (IDriver Driver, Neo4jMemoryGraphStore Store) Graph()
    {
        var options = new MemoryGraphOptions
        {
            Neo4jUri = Environment.GetEnvironmentVariable("AEGIS_MEMORY_TEST_NEO4J_URI")!,
            Neo4jUsername = Environment.GetEnvironmentVariable("AEGIS_MEMORY_TEST_NEO4J_USERNAME") ?? "neo4j",
            Neo4jPassword = Environment.GetEnvironmentVariable("AEGIS_MEMORY_TEST_NEO4J_PASSWORD")!,
            Neo4jDatabase = Environment.GetEnvironmentVariable("AEGIS_MEMORY_TEST_NEO4J_DATABASE") ?? "neo4j"
        };
        var driver = GraphDatabase.Driver(options.Neo4jUri, AuthTokens.Basic(options.Neo4jUsername, options.Neo4jPassword));
        return (driver, new Neo4jMemoryGraphStore(driver, options));
    }
    private sealed class FakeGraph : IMemoryGraphStore
    {
        public Dictionary<Guid, MemoryGraphEntityProjection> Entities { get; } = [];
        public Dictionary<Guid, MemoryGraphRelationProjection> Relations { get; } = [];
        public IReadOnlyList<MemoryGraphCandidatePath> Candidates { get; set; } = [];
        public bool FailDeletes { get; set; }
        public Task EnsureSchemaAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<MemoryGraphEntityProjection?> GetEntityProjectionAsync(Guid id, CancellationToken ct) => Task.FromResult(Entities.GetValueOrDefault(id));
        public Task UpsertEntityAsync(MemoryGraphEntityProjection entity, CancellationToken ct) { Entities[entity.EntityId] = entity; return Task.CompletedTask; }
        public Task DeleteEntityAsync(Guid id, CancellationToken ct) { Entities.Remove(id); return Task.CompletedTask; }
        public Task<MemoryGraphRelationProjection?> GetRelationProjectionAsync(Guid id, CancellationToken ct) => Task.FromResult(Relations.GetValueOrDefault(id));
        public Task UpsertRelationAsync(MemoryGraphRelationProjection relation, CancellationToken ct) { Relations[relation.RelationId] = relation; return Task.CompletedTask; }
        public Task DeleteRelationAsync(Guid id, CancellationToken ct)
        {
            if (FailDeletes) throw new MemoryGraphException("neo4j_unavailable");
            Relations.Remove(id); return Task.CompletedTask;
        }
        public Task<IReadOnlyList<MemoryGraphCandidatePath>> TraverseAsync(IReadOnlyList<Guid> startIds, MemoryGraphDirection direction,
            IReadOnlyList<string> predicates, int maxDepth, int limit, DateTimeOffset asOf, CancellationToken ct) =>
            Task.FromResult(Candidates);
        public Task DeleteManagedProjectionAsync(CancellationToken ct) { Entities.Clear(); Relations.Clear(); return Task.CompletedTask; }
    }

    [PostgresFact]
    public async Task GraphFailedDeleteLeaseRecoveryAndCompletedDriftConvergeWithoutSemanticWrites()
    {
        var schema = "graph_" + Guid.NewGuid().ToString("N");
        var db = await CreateDbAsync(schema);
        try
        {
            var clock = new Clock(); using var metrics = new AegisMetrics();
            var service = new MemoryService(new MemoryStore(db), clock, metrics);
            var jobs = new MemoryGraphProjectionStore(db, clock); var graph = new FakeGraph();
            var processor = new MemoryGraphProjectionProcessor(jobs, graph, clock, metrics);
            var a = await service.CreateEntityAsync("Sakamoto", "Person", default);
            var b = await service.CreateEntityAsync("Bisky", "Person", default);
            var r = await service.CreateRelationAsync(a.Id, "DATES", b.Id, null, null, default);
            var semantic = new MemoryProjectionJob(MemoryProjectionTarget.Semantic, MemoryAggregateType.MemoryRecord,
                Guid.NewGuid(), 1, MemoryProjectionOperation.Upsert, clock.Now);
            db.MemoryProjectionJobs.Add(semantic); await db.SaveChangesAsync();
            // A claimed entity job survives process loss through its two-minute lease.
            var first = await jobs.ClaimAsync(clock.Now, TimeSpan.FromMinutes(2), default);
            Assert.NotNull(first);
            clock.Now = clock.Now.AddMinutes(3);
            Assert.True(await processor.ProcessNextAsync(default));
            Assert.Equal(2, (await db.MemoryProjectionJobs.AsNoTracking().SingleAsync(x => x.Id == first!.Id)).Attempt);
            while (await processor.ProcessNextAsync(default)) { }
            Assert.Contains(r.Id, graph.Relations.Keys);
            await service.ForgetRelationAsync(r.Id, default);
            graph.FailDeletes = true;
            var delete = await db.MemoryProjectionJobs.AsNoTracking().SingleAsync(x => x.AggregateId == r.Id && x.Operation == MemoryProjectionOperation.Delete);
            var backoff = new[] { 11, 31, 121, 301, 1801 };
            for (var attempt = 1; attempt <= 6; attempt++)
            {
                if (attempt > 1) clock.Now = clock.Now.AddSeconds(backoff[attempt - 2]);
                Assert.True(await processor.ProcessNextAsync(default));
            }
            Assert.Equal(MemoryProjectionStatus.Failed,
                (await db.MemoryProjectionJobs.AsNoTracking().SingleAsync(x => x.Id == delete.Id)).Status);
            Assert.Contains(r.Id, graph.Relations.Keys);
            graph.FailDeletes = false;
            Assert.Equal(3, await jobs.RequeueCurrentStateAsync(clock.Now, default));
            Assert.Equal(0, await jobs.RequeueCurrentStateAsync(clock.Now, default));
            while (await processor.ProcessNextAsync(default)) { }
            Assert.DoesNotContain(r.Id, graph.Relations.Keys);
            Assert.Equal(MemoryProjectionStatus.Completed,
                (await db.MemoryProjectionJobs.AsNoTracking().SingleAsync(x => x.Id == delete.Id)).Status);
            // Completed Delete can be reopened to repair a stale edge restored independently.
            graph.Relations[r.Id] = new(r.Id, a.Id, "DATES", b.Id, 1, null, null);
            Assert.Equal(3, await jobs.RequeueCurrentStateAsync(clock.Now, default));
            while (await processor.ProcessNextAsync(default)) { }
            Assert.DoesNotContain(r.Id, graph.Relations.Keys);
            var semanticAfter = await db.MemoryProjectionJobs.AsNoTracking().SingleAsync(x => x.Id == semantic.Id);
            Assert.Equal(MemoryProjectionStatus.Pending, semanticAfter.Status);
            Assert.Equal(0, semanticAfter.Attempt);
            Assert.Equal(semantic.UpdatedAt, semanticAfter.UpdatedAt);
        }
        finally { await DropDbAsync(db, schema); }
    }

    [PostgresFact]
    public async Task CanonicalTraversalRejectsStaleTemporalAndForgottenEdgesAndUsesCurrentNames()
    {
        var schema = "graph_" + Guid.NewGuid().ToString("N");
        var db = await CreateDbAsync(schema);
        try
        {
            var clock = new Clock(); using var metrics = new AegisMetrics();
            var service = new MemoryService(new MemoryStore(db), clock, metrics);
            var graph = new FakeGraph(); var jobs = new MemoryGraphProjectionStore(db, clock);
            var query = new MemoryGraphQuery(graph, jobs, new MemoryGraphOptions(), clock, metrics);
            var pedro = await service.CreateEntityAsync("Pedro", "Person", default);
            var sakamoto = await service.CreateEntityAsync("Sakamoto", "Person", default);
            var bisky = await service.CreateEntityAsync("Bisky", "Person", default);
            var friend = await service.CreateRelationAsync(pedro.Id, "FRIEND_OF", sakamoto.Id, null, null, default);
            var jan = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var dates = await service.CreateRelationAsync(sakamoto.Id, "DATES", bisky.Id, jan, jan.AddMonths(5), default);
            graph.Candidates = [new([pedro.Id, sakamoto.Id, bisky.Id], [friend.Id, dates.Id])];
            var march = await query.TraverseAsync(new([pedro.Id], MemoryGraphDirection.Outgoing, MaxDepth: 2,
                AsOf: jan.AddMonths(2)), default);
            Assert.Equal("Sakamoto", Assert.Single(march).Entities[1].CanonicalName);
            Assert.Empty(await query.TraverseAsync(new([pedro.Id], MemoryGraphDirection.Outgoing, MaxDepth: 2,
                AsOf: jan.AddMonths(7)), default));
            await service.ForgetRelationAsync(friend.Id, default);
            Assert.Empty(await query.TraverseAsync(new([pedro.Id], MemoryGraphDirection.Outgoing, MaxDepth: 2,
                AsOf: jan.AddMonths(2)), default));
            await Assert.ThrowsAsync<ArgumentException>(() => query.TraverseAsync(new([pedro.Id], MemoryGraphDirection.Both, MaxDepth: 4), default));
        }
        finally { await DropDbAsync(db, schema); }
    }

    [PostgresFact]
    public async Task OldUpsertAfterCurrentDeleteCannotResurrectRelation()
    {
        var schema = "graph_" + Guid.NewGuid().ToString("N");
        var db = await CreateDbAsync(schema);
        try
        {
            var clock = new Clock(); using var metrics = new AegisMetrics();
            var service = new MemoryService(new MemoryStore(db), clock, metrics);
            var jobs = new MemoryGraphProjectionStore(db, clock); var graph = new FakeGraph();
            var processor = new MemoryGraphProjectionProcessor(jobs, graph, clock, metrics);
            var a = await service.CreateEntityAsync("Aegis", "Project", default);
            var b = await service.CreateEntityAsync("MCP", "Technology", default);
            while (await processor.ProcessNextAsync(default)) { }
            var r = await service.CreateRelationAsync(a.Id, "DOES_NOT_USE", b.Id, null, null, default);
            var old = await db.MemoryProjectionJobs.SingleAsync(x => x.AggregateId == r.Id && x.Operation == MemoryProjectionOperation.Upsert);
            old.Claim(Guid.NewGuid(), clock.Now, TimeSpan.FromHours(2));
            await db.SaveChangesAsync();
            await service.ForgetRelationAsync(r.Id, default);
            Assert.True(await processor.ProcessNextAsync(default)); // revision 2 Delete runs first
            graph.Relations[r.Id] = new(r.Id, a.Id, "DOES_NOT_USE", b.Id, 1, null, null);
            clock.Now = clock.Now.AddHours(3);
            Assert.True(await processor.ProcessNextAsync(default)); // revision 1 Upsert lease recovers last
            Assert.DoesNotContain(r.Id, graph.Relations.Keys);
            Assert.Equal(MemoryProjectionStatus.Completed,
                (await db.MemoryProjectionJobs.AsNoTracking().SingleAsync(x => x.Id == old.Id)).Status);
        }
        finally { await DropDbAsync(db, schema); }
    }

    [PostgresFact]
    public async Task RelationIntervalsReplayResolutionAndJobsAreCanonical()
    {
        var schema = "graph_" + Guid.NewGuid().ToString("N");
        var db = await CreateDbAsync(schema);
        try
        {
            var clock = new Clock(); using var metrics = new AegisMetrics();
            var store = new MemoryStore(db); var service = new MemoryService(store, clock, metrics);
            var resolver = new MemoryEntityResolver(store, clock, metrics);
            var sakamoto = await resolver.ResolveOrCreateAsync("Sakamoto", "Person");
            var same = await resolver.ResolveOrCreateAsync(" SAKAMOTO ", "Person");
            Assert.Equal(sakamoto.Id, same.Id);
            await service.AddAliasAsync(sakamoto.Id, "o Sakamoto", default);
            Assert.Equal(sakamoto.Id, (await resolver.ResolveAsync("O   SAKAMOTO", "person")).Entity!.Id);
            Assert.Equal(MemoryEntityResolutionKind.NotFound, (await resolver.ResolveAsync("o Sakamoto", "Project")).Kind);
            Assert.Equal(MemoryEntityResolutionKind.NotFound, (await resolver.ResolveAsync("desconhecido")).Kind);
            var other = await service.CreateEntityAsync("Sakamoto", "Project", default);
            await service.AddAliasAsync(other.Id, "o Sakamoto", default);
            Assert.Equal(MemoryEntityResolutionKind.Ambiguous, (await resolver.ResolveAsync("Sakamoto")).Kind);
            Assert.Equal(MemoryEntityResolutionKind.Ambiguous, (await resolver.ResolveAsync("o Sakamoto")).Kind);
            Assert.Equal("memory_entity_ambiguous", (await Assert.ThrowsAsync<MemoryException>(() =>
                resolver.ResolveOrCreateAsync("o Sakamoto"))).Code);
            Assert.Equal(other.Id, (await resolver.ResolveAsync("Sakamoto", "project")).Entity!.Id);
            await service.RetireEntityAsync(other.Id, default);
            Assert.Equal(sakamoto.Id, (await resolver.ResolveAsync("Sakamoto")).Entity!.Id);
            Assert.Equal(MemoryEntityResolutionKind.Ambiguous, (await resolver.ResolveAsync("Sakamoto", includeRetired: true)).Kind);
            var bisky = await service.CreateEntityAsync("Bisky", "Person", default);
            var jan = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var jun = jan.AddMonths(5); var sep = jan.AddMonths(8);
            var r1 = await service.CreateRelationAsync(sakamoto.Id, "dates", bisky.Id, jan, jun, default);
            var r2 = await service.CreateRelationAsync(sakamoto.Id, "DATES", bisky.Id, sep, null, default);
            Assert.NotEqual(r1.Id, r2.Id);
            Assert.Equal(r1.Id, (await service.CreateRelationAsync(sakamoto.Id, "Dates", bisky.Id, jan, jun, default)).Id);
            var conflict = await Assert.ThrowsAsync<MemoryException>(() => service.CreateRelationAsync(sakamoto.Id,
                "DATES", bisky.Id, jan.AddMonths(4), jan.AddMonths(10), default));
            Assert.Equal("memory_relation_overlap", conflict.Code);
            Assert.Equal(2, await db.MemoryRelations.CountAsync());
            Assert.Equal(2, await db.MemoryProjectionJobs.CountAsync(x => x.AggregateType == MemoryAggregateType.MemoryRelation));
            await service.ForgetRelationAsync(r1.Id, default);
            await service.ForgetRelationAsync(r1.Id, default);
            Assert.Equal(MemoryStatus.Forgotten, (await db.MemoryRelations.AsNoTracking().SingleAsync(x => x.Id == r1.Id)).Status);
            Assert.Equal(1, await db.MemoryProjectionJobs.CountAsync(x => x.AggregateId == r1.Id && x.Operation == MemoryProjectionOperation.Delete));
            var replacement = await service.SupersedeRelationAsync(r2.Id, sakamoto.Id, "FRIEND_OF", bisky.Id, sep, null, default);
            Assert.Equal(MemoryStatus.Superseded, (await db.MemoryRelations.AsNoTracking().SingleAsync(x => x.Id == r2.Id)).Status);
            Assert.Equal(replacement.Id, (await db.MemoryRelations.AsNoTracking().SingleAsync(x => x.Id == r2.Id)).SupersededById);
        }
        finally { await DropDbAsync(db, schema); }
    }

    [GraphFact]
    public async Task PhysicalNeo4jSchemaTraversalTemporalRebuildAndIdempotency()
    {
        var (driver, graph) = Graph();
        var marker = Guid.NewGuid().ToString();
        await using (driver)
        {
            await graph.EnsureSchemaAsync(default);
            try
            {
                await using (var session = driver.AsyncSession())
                    await session.ExecuteWriteAsync(async tx =>
                    {
                        var cursor = await tx.RunAsync("CREATE (:ExternalTestNode {marker:$marker})", new { marker });
                        await cursor.ConsumeAsync();
                    });
                var pedro = Guid.NewGuid(); var sakamoto = Guid.NewGuid(); var bisky = Guid.NewGuid();
                var e1 = new MemoryGraphEntityProjection(pedro, "Pedro", "PEDRO", "PERSON", [], 1, null);
                var e2 = new MemoryGraphEntityProjection(sakamoto, "Sakamoto", "SAKAMOTO", "PERSON", [], 1, null);
                var e3 = new MemoryGraphEntityProjection(bisky, "Bisky", "BISKY", "PERSON", [], 1, null);
                foreach (var e in new[] { e1, e2, e3 }) { await graph.UpsertEntityAsync(e, default); await graph.UpsertEntityAsync(e, default); }
                e1 = e1 with { RetiredAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), Revision = 2 };
                await graph.UpsertEntityAsync(e1, default);
                Assert.Equal(e1.RetiredAt, (await graph.GetEntityProjectionAsync(pedro, default))!.RetiredAt);
                await graph.UpsertEntityAsync(e2 with { Aliases = ["o Sakamoto"], Revision = 2 }, default);
                Assert.Equal("o Sakamoto", Assert.Single((await graph.GetEntityProjectionAsync(sakamoto, default))!.Aliases));
                var a = new MemoryGraphRelationProjection(Guid.NewGuid(), pedro, "FRIEND_OF", sakamoto, 1, null, null);
                var jan = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
                var b = new MemoryGraphRelationProjection(Guid.NewGuid(), sakamoto, "DATES", bisky, 1, jan, jan.AddMonths(5));
                await graph.UpsertRelationAsync(a, default); await graph.UpsertRelationAsync(a, default);
                await graph.UpsertRelationAsync(b, default); await graph.UpsertRelationAsync(b, default);
                Assert.NotNull(await graph.GetRelationProjectionAsync(a.RelationId, default));
                Assert.Single(await graph.TraverseAsync([pedro], MemoryGraphDirection.Outgoing, ["FRIEND_OF"], 1, 10, jan, default));
                Assert.Contains(await graph.TraverseAsync([pedro], MemoryGraphDirection.Outgoing, [], 2, 10, jan.AddMonths(2), default),
                    p => p.RelationIds.SequenceEqual([a.RelationId, b.RelationId]));
                Assert.DoesNotContain(await graph.TraverseAsync([pedro], MemoryGraphDirection.Outgoing, [], 2, 10, jan.AddMonths(7), default),
                    p => p.RelationIds.Contains(b.RelationId));
                await graph.DeleteRelationAsync(b.RelationId, default); await graph.DeleteRelationAsync(b.RelationId, default);
                Assert.Null(await graph.GetRelationProjectionAsync(b.RelationId, default));
                await graph.UpsertRelationAsync(b, default);
                await graph.DeleteManagedProjectionAsync(default);
                Assert.Empty(await graph.TraverseAsync([pedro], MemoryGraphDirection.Both, [], 2, 10, jan, default));
                await using (var session = driver.AsyncSession())
                {
                    var count = await session.ExecuteReadAsync(async tx =>
                    {
                        var cursor = await tx.RunAsync("MATCH (n:ExternalTestNode {marker:$marker}) RETURN count(n) AS count", new { marker });
                        return (await cursor.SingleAsync())["count"].As<long>();
                    });
                    Assert.Equal(1, count);
                }
                foreach (var e in new[] { e1, e2, e3 }) await graph.UpsertEntityAsync(e, default);
                await graph.UpsertRelationAsync(a, default); await graph.UpsertRelationAsync(b, default);
                Assert.Contains(await graph.TraverseAsync([pedro], MemoryGraphDirection.Outgoing, [], 2, 10, jan.AddMonths(2), default),
                    p => p.RelationIds.SequenceEqual([a.RelationId, b.RelationId]));
            }
            finally
            {
                await graph.DeleteManagedProjectionAsync(default);
                await using var session = driver.AsyncSession();
                await session.ExecuteWriteAsync(async tx =>
                {
                    var cursor = await tx.RunAsync("MATCH (n:ExternalTestNode {marker:$marker}) DELETE n", new { marker });
                    await cursor.ConsumeAsync();
                });
            }
        }
    }

    [BothFact]
    public async Task PhysicalPostgresGraphProjectionRebuildAndCanonicalFiltering()
    {
        var schema = "graph_" + Guid.NewGuid().ToString("N");
        var db = await CreateDbAsync(schema); var (driver, graph) = Graph();
        await using (driver)
        {
            await graph.EnsureSchemaAsync(default);
            try
            {
                var clock = new Clock(); using var metrics = new AegisMetrics();
                var service = new MemoryService(new MemoryStore(db), clock, metrics);
                var jobs = new MemoryGraphProjectionStore(db, clock);
                var processor = new MemoryGraphProjectionProcessor(jobs, graph, clock, metrics);
                var query = new MemoryGraphQuery(graph, jobs, new MemoryGraphOptions(), clock, metrics);
                var pedro = await service.CreateEntityAsync("Pedro", "Person", default);
                var sakamoto = await service.CreateEntityAsync("Sakamoto", "Person", default);
                var bisky = await service.CreateEntityAsync("Bisky", "Person", default);
                var r1 = await service.CreateRelationAsync(pedro.Id, "FRIEND_OF", sakamoto.Id, null, null, default);
                var r2 = await service.CreateRelationAsync(sakamoto.Id, "DATES", bisky.Id, null, null, default);
                while (await processor.ProcessNextAsync(default)) { }
                Assert.Contains(await query.TraverseAsync(new([pedro.Id], MemoryGraphDirection.Outgoing, MaxDepth: 2), default),
                    p => p.Relations.Select(x => x.Id).SequenceEqual([r1.Id, r2.Id]));
                await service.AddAliasAsync(sakamoto.Id, "o Sakamoto", default);
                while (await processor.ProcessNextAsync(default)) { }
                Assert.Equal("o Sakamoto", Assert.Single((await graph.GetEntityProjectionAsync(sakamoto.Id, default))!.Aliases));
                await service.ForgetRelationAsync(r2.Id, default);
                Assert.DoesNotContain(await query.TraverseAsync(new([pedro.Id], MemoryGraphDirection.Outgoing, MaxDepth: 2), default),
                    p => p.Relations.Any(x => x.Id == r2.Id)); // stale physical edge is filtered canonically
                while (await processor.ProcessNextAsync(default)) { }
                Assert.Null(await graph.GetRelationProjectionAsync(r2.Id, default));
                await graph.UpsertRelationAsync(new(r2.Id, sakamoto.Id, "DATES", bisky.Id, 1, null, null), default);
                Assert.Equal(5, await jobs.RequeueCurrentStateAsync(clock.Now, default));
                Assert.Equal(0, await jobs.RequeueCurrentStateAsync(clock.Now, default));
                while (await processor.ProcessNextAsync(default)) { }
                Assert.Null(await graph.GetRelationProjectionAsync(r2.Id, default));
                var semantic = new MemoryProjectionJob(MemoryProjectionTarget.Semantic, MemoryAggregateType.MemoryRecord,
                    Guid.NewGuid(), 1, MemoryProjectionOperation.Upsert, clock.Now);
                db.MemoryProjectionJobs.Add(semantic); await db.SaveChangesAsync();
                var before = await db.MemoryProjectionJobs.AsNoTracking().SingleAsync(x => x.Id == semantic.Id);
                var rebuild = new MemoryGraphRebuild(graph, jobs, clock);
                Assert.Equal(5, await rebuild.RebuildAsync(default));
                Assert.Null(await graph.GetEntityProjectionAsync(pedro.Id, default));
                while (await processor.ProcessNextAsync(default)) { }
                Assert.NotNull(await graph.GetEntityProjectionAsync(pedro.Id, default));
                Assert.NotNull(await graph.GetRelationProjectionAsync(r1.Id, default));
                Assert.Null(await graph.GetRelationProjectionAsync(r2.Id, default));
                var after = await db.MemoryProjectionJobs.AsNoTracking().SingleAsync(x => x.Id == semantic.Id);
                Assert.Equal(before.Status, after.Status); Assert.Equal(before.Attempt, after.Attempt);
                Assert.Equal(before.UpdatedAt, after.UpdatedAt); Assert.Equal(before.LeaseId, after.LeaseId);
            }
            finally { await graph.DeleteManagedProjectionAsync(default); await DropDbAsync(db, schema); }
        }
    }
}
