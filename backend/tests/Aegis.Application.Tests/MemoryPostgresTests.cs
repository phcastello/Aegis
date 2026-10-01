using System.Text.Json;
using Aegis.Application.Memory;
using Aegis.Application.Observability;
using Aegis.Application.Tools;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Memory;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Aegis.Application.Tests;

// The connection must point to a disposable database. Every test gets an isolated schema.
public sealed class MemoryPostgresTests
{
    public sealed class PostgresFactAttribute : FactAttribute
    {
        public PostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEGIS_MEMORY_TEST_DATABASE")))
                Skip = "Set AEGIS_MEMORY_TEST_DATABASE to a disposable PostgreSQL database.";
        }
    }

    public sealed class PostgresQdrantFactAttribute : FactAttribute
    {
        public PostgresQdrantFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEGIS_MEMORY_TEST_DATABASE")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEGIS_MEMORY_TEST_QDRANT_URL")))
                Skip = "Set disposable AEGIS_MEMORY_TEST_DATABASE and AEGIS_MEMORY_TEST_QDRANT_URL.";
        }
    }

    private static AegisDbContext CreateDb(string schema) => new(new DbContextOptionsBuilder<AegisDbContext>()
        .UseNpgsql(Environment.GetEnvironmentVariable("AEGIS_MEMORY_TEST_DATABASE") + ";Search Path=" + schema,
            options => options.MigrationsHistoryTable("__EFMigrationsHistory", schema)).Options);

    private sealed class FixedClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeEmbedding : IMemoryEmbeddingClient
    {
        public bool Fail { get; set; }
        public Task<float[]> EmbedAsync(string text, CancellationToken ct) => Fail
            ? throw new MemorySemanticException("embedding_timeout") : Task.FromResult<float[]>([1, 0, 0]);
    }

    private sealed class FakeVectors : IMemoryVectorStore
    {
        public Dictionary<Guid, MemoryVectorPoint> Points { get; } = [];
        public bool FailDeletes { get; set; }
        public Task<bool> EnsureCollectionAsync(CancellationToken ct) => Task.FromResult(false);
        public Task<MemoryVectorPoint?> GetPointAsync(Guid id, CancellationToken ct) => Task.FromResult(Points.GetValueOrDefault(id));
        public Task UpsertAsync(MemoryVectorPoint point, float[] vector, CancellationToken ct) { Points[point.MemoryId] = point; return Task.CompletedTask; }
        public Task DeleteAsync(Guid id, CancellationToken ct)
        {
            if (FailDeletes) throw new MemorySemanticException("qdrant_unavailable");
            Points.Remove(id); return Task.CompletedTask;
        }
        public Task<IReadOnlyList<MemoryVectorCandidate>> SearchAsync(float[] vector, int limit, double threshold, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MemoryVectorCandidate>>(Points.Keys.Take(limit).Select(id => new MemoryVectorCandidate(id, 0.9)).ToArray());
    }

    private sealed class OfflineVectors : IMemoryVectorStore
    {
        private static MemorySemanticException Offline() => new("qdrant_unavailable");
        public Task<bool> EnsureCollectionAsync(CancellationToken ct) => throw Offline();
        public Task<MemoryVectorPoint?> GetPointAsync(Guid id, CancellationToken ct) => throw Offline();
        public Task UpsertAsync(MemoryVectorPoint point, float[] vector, CancellationToken ct) => throw Offline();
        public Task DeleteAsync(Guid id, CancellationToken ct) => throw Offline();
        public Task<IReadOnlyList<MemoryVectorCandidate>> SearchAsync(float[] vector, int limit, double threshold, CancellationToken ct) => throw Offline();
    }

    [PostgresFact]
    public Task FailedForgottenDeleteReconcilesAfterRecovery() => FailedDeleteReconcilesAsync(supersede: false);

    [PostgresFact]
    public Task FailedSupersededDeleteReconcilesAfterRecovery() => FailedDeleteReconcilesAsync(supersede: true);

    private static async Task FailedDeleteReconcilesAsync(bool supersede)
    {
        var schema = "memory_" + Guid.NewGuid().ToString("N");
        await using var db = CreateDb(schema);
        await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA \"" + schema + "\"");
        try
        {
            await db.Database.MigrateAsync();
            var conversation = new Conversation();
            var message = conversation.AddMessage("user", "Lembra que Pedro usa RX 6700 XT.");
            db.Conversations.Add(conversation); await db.SaveChangesAsync();
            var clock = new FixedClock(); using var metrics = new AegisMetrics();
            var service = new MemoryService(new MemoryStore(db), clock, metrics);
            var jobs = new MemorySemanticProjectionStore(db, clock);
            var vectors = new FakeVectors();
            var processor = new MemorySemanticProjectionProcessor(jobs, new FakeEmbedding(), vectors,
                new MemorySemanticOptions { EmbeddingDimensions = 3, EmbeddingModel = "fake" }, clock, metrics);
            var old = (await service.RememberAsync("Pedro usa RX 6700 XT.", null, null,
                new ToolExecutionContext(conversation.Id, message.Id, message.Content), default)).Record;
            await service.CreateEntityAsync("Pedro", "Person", default);
            var graphBefore = await db.MemoryProjectionJobs.AsNoTracking().SingleAsync(x => x.ProjectionTarget == MemoryProjectionTarget.Graph);
            Assert.True(await processor.ProcessNextAsync(default));
            Assert.Contains(old.Id, vectors.Points.Keys);

            MemoryRecord? replacement = null;
            if (supersede)
                replacement = await service.UpdateAsync(old.Id, "Pedro usa RTX 5080.", null, null,
                    new ToolExecutionContext(conversation.Id, message.Id, message.Content), default);
            else await service.ForgetAsync(old.Id, new ToolExecutionContext(conversation.Id, Guid.Empty, ""), default);
            var desiredDelete = await db.MemoryProjectionJobs.AsNoTracking().SingleAsync(x => x.AggregateId == old.Id &&
                x.AggregateRevision == 2 && x.Operation == MemoryProjectionOperation.Delete);
            vectors.FailDeletes = true;
            var backoff = new[] { 11, 31, 121, 301, 1801 };
            for (var attempt = 1; attempt <= 6; attempt++)
            {
                if (attempt > 1) clock.Now = clock.Now.AddSeconds(backoff[attempt - 2]);
                // The replacement Upsert may be claimed before the old Delete.
                for (var i = 0; i < 3; i++)
                {
                    var state = await db.MemoryProjectionJobs.AsNoTracking().SingleAsync(x => x.Id == desiredDelete.Id);
                    if (state.Attempt == attempt) break;
                    Assert.True(await processor.ProcessNextAsync(default));
                }
                var failedAttempt = await db.MemoryProjectionJobs.AsNoTracking().SingleAsync(x => x.Id == desiredDelete.Id);
                Assert.Equal(attempt, failedAttempt.Attempt);
            }
            while (await processor.ProcessNextAsync(default)) { } // finish replacement Upsert, if any
            var failed = await db.MemoryProjectionJobs.AsNoTracking().SingleAsync(x => x.Id == desiredDelete.Id);
            Assert.Equal(MemoryProjectionStatus.Failed, failed.Status);
            Assert.Contains(old.Id, vectors.Points.Keys);
            Assert.Equal(supersede ? MemoryStatus.Superseded : MemoryStatus.Forgotten,
                (await db.MemoryRecords.AsNoTracking().SingleAsync(x => x.Id == old.Id)).Status);
            if (replacement is not null) Assert.Contains(replacement.Id, vectors.Points.Keys);

            vectors.FailDeletes = false;
            Assert.Equal(supersede ? 2 : 1, await jobs.RequeueCurrentStateAsync(clock.Now, default));
            Assert.Equal(0, await jobs.RequeueCurrentStateAsync(clock.Now, default));
            Assert.Equal(0, await jobs.RequeueCurrentStateAsync(clock.Now, default));
            for (var i = 0; i < (supersede ? 2 : 1); i++) Assert.True(await processor.ProcessNextAsync(default));
            Assert.DoesNotContain(old.Id, vectors.Points.Keys);
            if (replacement is not null) Assert.Contains(replacement.Id, vectors.Points.Keys);
            Assert.Equal(MemoryProjectionStatus.Completed, (await db.MemoryProjectionJobs.AsNoTracking()
                .SingleAsync(x => x.Id == desiredDelete.Id)).Status);
            Assert.Equal(supersede ? 3 : 2, await db.MemoryProjectionJobs.CountAsync(x => x.ProjectionTarget == MemoryProjectionTarget.Semantic));
            var graphAfter = await db.MemoryProjectionJobs.AsNoTracking().SingleAsync(x => x.Id == graphBefore.Id);
            Assert.Equal(graphBefore.Status, graphAfter.Status);
            Assert.Equal(graphBefore.Attempt, graphAfter.Attempt);
            Assert.Equal(graphBefore.LeaseId, graphAfter.LeaseId);
            Assert.Equal(graphBefore.LeaseExpiresAt, graphAfter.LeaseExpiresAt);
            Assert.Equal(graphBefore.UpdatedAt, graphAfter.UpdatedAt);
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP SCHEMA \"" + schema + "\" CASCADE"); }
    }

    [PostgresFact]
    public async Task CompletedDeleteRepairsArtificialPointDrift()
    {
        var schema = "memory_" + Guid.NewGuid().ToString("N");
        await using var db = CreateDb(schema);
        await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA \"" + schema + "\"");
        try
        {
            await db.Database.MigrateAsync();
            var conversation = new Conversation();
            var message = conversation.AddMessage("user", "Lembra que Aegis não usa MCP.");
            db.Conversations.Add(conversation); await db.SaveChangesAsync();
            var clock = new FixedClock(); using var metrics = new AegisMetrics();
            var service = new MemoryService(new MemoryStore(db), clock, metrics);
            var jobs = new MemorySemanticProjectionStore(db, clock);
            var vectors = new FakeVectors();
            var processor = new MemorySemanticProjectionProcessor(jobs, new FakeEmbedding(), vectors,
                new MemorySemanticOptions { EmbeddingDimensions = 3, EmbeddingModel = "fake" }, clock, metrics);
            var record = (await service.RememberAsync("Aegis não usa MCP.", null, null,
                new ToolExecutionContext(conversation.Id, message.Id, message.Content), default)).Record;
            Assert.True(await processor.ProcessNextAsync(default));
            var stalePoint = vectors.Points[record.Id];
            await service.ForgetAsync(record.Id, new ToolExecutionContext(conversation.Id, Guid.Empty, ""), default);
            Assert.True(await processor.ProcessNextAsync(default));
            Assert.Empty(vectors.Points);
            vectors.Points[record.Id] = stalePoint;
            Assert.Equal(1, await jobs.RequeueCurrentStateAsync(clock.Now, default));
            Assert.Equal(0, await jobs.RequeueCurrentStateAsync(clock.Now, default));
            Assert.True(await processor.ProcessNextAsync(default));
            Assert.Empty(vectors.Points);
            Assert.Equal(MemoryProjectionStatus.Completed, (await db.MemoryProjectionJobs.AsNoTracking()
                .SingleAsync(x => x.AggregateId == record.Id && x.Operation == MemoryProjectionOperation.Delete)).Status);
            Assert.Equal(MemoryProjectionStatus.Completed, (await db.MemoryProjectionJobs.AsNoTracking()
                .SingleAsync(x => x.AggregateId == record.Id && x.Operation == MemoryProjectionOperation.Upsert)).Status);
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP SCHEMA \"" + schema + "\" CASCADE"); }
    }

    [PostgresQdrantFact]
    public async Task PhysicalQdrantProjectionFilteringAndRebuildUseCanonicalPostgres()
    {
        var schema = "memory_" + Guid.NewGuid().ToString("N");
        var collection = "memory_test_" + Guid.NewGuid().ToString("N");
        var url = Environment.GetEnvironmentVariable("AEGIS_MEMORY_TEST_QDRANT_URL")!;
        using var http = new HttpClient { BaseAddress = new Uri(url.TrimEnd('/') + "/") };
        var options = new MemorySemanticOptions { QdrantBaseUrl = url, CollectionName = collection, EmbeddingDimensions = 3, EmbeddingModel = "fake" };
        var vectors = new QdrantMemoryVectorStore(http, options);
        await using var db = CreateDb(schema);
        await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA \"" + schema + "\"");
        try
        {
            await db.Database.MigrateAsync();
            Assert.True(await vectors.EnsureCollectionAsync(default));
            var conversation = new Conversation();
            var message = conversation.AddMessage("user", "Lembra que Pedro usa RX 6700 XT.");
            db.Conversations.Add(conversation); await db.SaveChangesAsync();
            var clock = new FixedClock(); using var metrics = new AegisMetrics();
            var store = new MemoryStore(db);
            var service = new MemoryService(store, clock, metrics);
            var jobs = new MemorySemanticProjectionStore(db, clock);
            var embedding = new FakeEmbedding();
            var processor = new MemorySemanticProjectionProcessor(jobs, embedding, vectors, options, clock, metrics);
            var old = (await service.RememberAsync("Pedro usa RX 6700 XT.", null, null,
                new ToolExecutionContext(conversation.Id, message.Id, message.Content), default)).Record;
            await service.CreateEntityAsync("Pedro", "Person", default);
            Assert.True(await processor.ProcessNextAsync(default));
            Assert.Equal(old.Id, (await vectors.GetPointAsync(old.Id, default))!.MemoryId);
            await service.ForgetAsync(old.Id, new ToolExecutionContext(conversation.Id, Guid.Empty, ""), default);
            var search = new MemorySemanticSearch(store, embedding, vectors, options, clock, metrics);
            Assert.Empty((await search.SearchAsync("consulta indireta", 10, default)).Results); // point still present
            Assert.True(await processor.ProcessNextAsync(default));
            Assert.Null(await vectors.GetPointAsync(old.Id, default));

            var secondMessage = new ChatMessage(conversation.Id, "user", "Guarda que a Aegis usa PostgreSQL.");
            db.ChatMessages.Add(secondMessage); await db.SaveChangesAsync();
            var current = (await service.RememberAsync("Aegis usa PostgreSQL.", null, null,
                new ToolExecutionContext(conversation.Id, secondMessage.Id, secondMessage.Content), default)).Record;
            Assert.True(await processor.ProcessNextAsync(default));
            Assert.NotNull(await vectors.GetPointAsync(current.Id, default));
            using (var deleted = await http.DeleteAsync("collections/" + collection)) deleted.EnsureSuccessStatusCode();
            Assert.True(await vectors.EnsureCollectionAsync(default));
            Assert.Null(await vectors.GetPointAsync(current.Id, default));
            Assert.Equal(2, await jobs.RequeueCurrentStateAsync(clock.Now, default));
            Assert.Equal(0, await jobs.RequeueCurrentStateAsync(clock.Now, default));
            Assert.Equal(0, await jobs.RequeueCurrentStateAsync(clock.Now, default));
            Assert.True(await processor.ProcessNextAsync(default));
            Assert.True(await processor.ProcessNextAsync(default));
            Assert.Equal(current.Id, (await vectors.GetPointAsync(current.Id, default))!.MemoryId);
            Assert.Equal(current.Id, Assert.Single((await search.SearchAsync("o banco canônico", 10, default)).Results).Id);
            Assert.Equal(MemoryProjectionStatus.Pending, (await db.MemoryProjectionJobs.AsNoTracking()
                .SingleAsync(x => x.ProjectionTarget == MemoryProjectionTarget.Graph)).Status);
            Assert.Equal(2, await db.MemoryRecords.CountAsync());
        }
        finally
        {
            using var response = await http.DeleteAsync("collections/" + collection);
            await db.Database.ExecuteSqlRawAsync("DROP SCHEMA \"" + schema + "\" CASCADE");
        }
    }

    [PostgresFact]
    public async Task CanonicalWritesAndTextFallbackSurviveQdrantOutage()
    {
        var schema = "memory_" + Guid.NewGuid().ToString("N");
        await using var db = CreateDb(schema);
        await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA \"" + schema + "\"");
        try
        {
            await db.Database.MigrateAsync();
            var conversation = new Conversation();
            var message = conversation.AddMessage("user", "Lembra que Pedro usa RX 6700 XT.");
            db.Conversations.Add(conversation); await db.SaveChangesAsync();
            var clock = new FixedClock(); using var metrics = new AegisMetrics();
            var store = new MemoryStore(db);
            var semantic = new MemorySemanticSearch(store, new FakeEmbedding(), new OfflineVectors(),
                new MemorySemanticOptions { EmbeddingDimensions = 3 }, clock, metrics);
            var service = new MemoryService(store, clock, metrics, semantic);
            var old = (await service.RememberAsync("Pedro usa RX 6700 XT.", null, null,
                new ToolExecutionContext(conversation.Id, message.Id, message.Content), default)).Record;
            var fallback = await service.SearchWithModeAsync("RX 6700 XT", 10, conversation.Id, default);
            Assert.Equal("canonical_text_fallback", fallback.Mode);
            Assert.Equal(old.Id, Assert.Single(fallback.Results).Id);
            var replacement = await service.UpdateAsync(old.Id, "Pedro usa RTX 5080.", null, null,
                new ToolExecutionContext(conversation.Id, message.Id, message.Content), default);
            await service.ForgetAsync(replacement.Id, new ToolExecutionContext(conversation.Id, Guid.Empty, ""), default);
            Assert.Equal(MemoryStatus.Superseded, (await db.MemoryRecords.AsNoTracking().SingleAsync(x => x.Id == old.Id)).Status);
            Assert.Equal(MemoryStatus.Forgotten, (await db.MemoryRecords.AsNoTracking().SingleAsync(x => x.Id == replacement.Id)).Status);
            Assert.Equal(4, await db.MemoryProjectionJobs.CountAsync(x => x.ProjectionTarget == MemoryProjectionTarget.Semantic));
            Assert.Empty((await service.SearchWithModeAsync("RTX 5080", 10, conversation.Id, default)).Results);
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP SCHEMA \"" + schema + "\" CASCADE"); }
    }

    [PostgresFact]
    public async Task SemanticJobsRecoverReconcileAndRebuildWithoutTouchingGraph()
    {
        var schema = "memory_" + Guid.NewGuid().ToString("N");
        await using var db = CreateDb(schema);
        await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA \"" + schema + "\"");
        try
        {
            await db.Database.MigrateAsync();
            var conversation = new Conversation();
            var message = conversation.AddMessage("user", "Lembra que Aegis usa PostgreSQL.");
            db.Conversations.Add(conversation); await db.SaveChangesAsync();
            var clock = new FixedClock(); using var metrics = new AegisMetrics();
            var service = new MemoryService(new MemoryStore(db), clock, metrics);
            var record = (await service.RememberAsync("Aegis usa PostgreSQL.", null, null,
                new ToolExecutionContext(conversation.Id, message.Id, message.Content), default)).Record;
            await service.CreateEntityAsync("Aegis", "Project", default);
            var jobs = new MemorySemanticProjectionStore(db, clock);
            var firstClaim = await jobs.ClaimAsync(clock.Now, TimeSpan.FromMinutes(2), default);
            Assert.NotNull(firstClaim);
            Assert.Equal(1, firstClaim.Attempt);
            clock.Now = clock.Now.AddMinutes(3);
            var recovered = await jobs.ClaimAsync(clock.Now, TimeSpan.FromMinutes(2), default);
            Assert.Equal(firstClaim.Id, recovered!.Id);
            Assert.Equal(2, recovered.Attempt);
            Assert.NotEqual(firstClaim.LeaseId, recovered.LeaseId);
            clock.Now = clock.Now.AddMinutes(3);
            var embedding = new FakeEmbedding(); var vectors = new FakeVectors();
            var options = new MemorySemanticOptions { EmbeddingDimensions = 3, EmbeddingModel = "fake" };
            var processor = new MemorySemanticProjectionProcessor(jobs, embedding, vectors, options, clock, metrics);
            Assert.True(await processor.ProcessNextAsync(default));
            Assert.Equal(record.Id, Assert.Single(vectors.Points).Key);
            Assert.Equal(MemoryProjectionStatus.Completed, (await db.MemoryProjectionJobs.SingleAsync(x => x.Id == firstClaim.Id)).Status);
            Assert.All(await db.MemoryProjectionJobs.Where(x => x.ProjectionTarget == MemoryProjectionTarget.Graph).ToListAsync(),
                x => { Assert.Equal(MemoryProjectionStatus.Pending, x.Status); Assert.Equal(0, x.Attempt); });

            await service.ForgetAsync(record.Id, new ToolExecutionContext(conversation.Id, Guid.Empty, ""), default);
            var search = new MemorySemanticSearch(new MemoryStore(db), embedding, vectors, options, clock, metrics);
            Assert.Empty((await search.SearchAsync("sem relação lexical", 10, default)).Results); // stale Qdrant point
            Assert.True(await processor.ProcessNextAsync(default)); // rev2 Delete
            Assert.Empty(vectors.Points);
            var oldJob = await db.MemoryProjectionJobs.SingleAsync(x => x.Id == firstClaim.Id);
            oldJob.Requeue(clock.Now); await db.SaveChangesAsync();
            Assert.True(await processor.ProcessNextAsync(default)); // delayed rev1 Upsert resolves to DELETE
            Assert.Empty(vectors.Points);

            var secondMessage = new ChatMessage(conversation.Id, "user", "Guarda que Pedro prefere backend.");
            db.ChatMessages.Add(secondMessage); await db.SaveChangesAsync();
            var active = (await service.RememberAsync("Pedro prefere backend.", null, null,
                new ToolExecutionContext(conversation.Id, secondMessage.Id, secondMessage.Content), default)).Record;
            embedding.Fail = true;
            Assert.True(await processor.ProcessNextAsync(default));
            var retry = await db.MemoryProjectionJobs.SingleAsync(x => x.AggregateId == active.Id);
            Assert.Equal(MemoryProjectionStatus.Pending, retry.Status);
            Assert.Equal("embedding_timeout", retry.LastError);
            Assert.Equal(1, retry.Attempt);
            Assert.False(await processor.ProcessNextAsync(default));
            // Collection recovery reopens a delayed current job immediately.
            Assert.Equal(2, await jobs.RequeueCurrentStateAsync(clock.Now, default));
            var reopened = await db.MemoryProjectionJobs.AsNoTracking().SingleAsync(x => x.AggregateId == active.Id);
            Assert.Null(reopened.NextAttemptAt);
            Assert.Equal(0, reopened.Attempt);
            embedding.Fail = false;
            Assert.True(await processor.ProcessNextAsync(default));
            Assert.True(await processor.ProcessNextAsync(default));
            Assert.Contains(active.Id, vectors.Points.Keys);
            vectors.Points.Clear(); // lost Qdrant collection
            Assert.Equal(2, await jobs.RequeueCurrentStateAsync(clock.Now, default));
            Assert.True(await processor.ProcessNextAsync(default));
            Assert.True(await processor.ProcessNextAsync(default));
            Assert.Contains(active.Id, vectors.Points.Keys);
            await jobs.RequeueCurrentStateAsync(clock.Now, default);
            Assert.Equal(3, await db.MemoryProjectionJobs.CountAsync(x => x.ProjectionTarget == MemoryProjectionTarget.Semantic));
            Assert.True(await processor.ProcessNextAsync(default));
            Assert.True(await processor.ProcessNextAsync(default));

            var thirdMessage = new ChatMessage(conversation.Id, "user", "Guarda que Bisky estuda na Unicentro.");
            db.ChatMessages.Add(thirdMessage); await db.SaveChangesAsync();
            var terminal = (await service.RememberAsync("Bisky estuda na Unicentro.", null, null,
                new ToolExecutionContext(conversation.Id, thirdMessage.Id, thirdMessage.Content), default)).Record;
            embedding.Fail = true;
            var backoff = new[] { 11, 31, 121, 301, 1801 };
            for (var attempt = 1; attempt <= 6; attempt++)
            {
                if (attempt > 1) clock.Now = clock.Now.AddSeconds(backoff[attempt - 2]);
                Assert.True(await processor.ProcessNextAsync(default));
                var state = await db.MemoryProjectionJobs.AsNoTracking().SingleAsync(x => x.AggregateId == terminal.Id);
                Assert.Equal(attempt, state.Attempt);
                Assert.Equal(attempt == 6 ? MemoryProjectionStatus.Failed : MemoryProjectionStatus.Pending, state.Status);
                Assert.Equal("embedding_timeout", state.LastError);
            }
            Assert.False(await processor.ProcessNextAsync(default));
            Assert.Equal(MemoryStatus.Active, (await db.MemoryRecords.AsNoTracking().SingleAsync(x => x.Id == terminal.Id)).Status);
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP SCHEMA \"" + schema + "\" CASCADE"); }
    }

    [PostgresFact]
    public async Task ConcurrentSemanticClaimsAreAtomicAndIgnoreGraph()
    {
        var schema = "memory_" + Guid.NewGuid().ToString("N");
        await using var setup = CreateDb(schema);
        await setup.Database.ExecuteSqlRawAsync("CREATE SCHEMA \"" + schema + "\"");
        try
        {
            await setup.Database.MigrateAsync();
            var clock = new FixedClock();
            var record = new MemoryRecord("Sakamoto namora Bisky.", null, null, clock.Now);
            setup.MemoryRecords.Add(record);
            setup.MemoryProjectionJobs.Add(new MemoryProjectionJob(MemoryProjectionTarget.Semantic, MemoryAggregateType.MemoryRecord,
                record.Id, 1, MemoryProjectionOperation.Upsert, clock.Now));
            setup.MemoryProjectionJobs.Add(new MemoryProjectionJob(MemoryProjectionTarget.Graph, MemoryAggregateType.MemoryEntity,
                Guid.NewGuid(), 1, MemoryProjectionOperation.Upsert, clock.Now));
            await setup.SaveChangesAsync();
            await using var first = CreateDb(schema);
            await using var second = CreateDb(schema);
            var claims = await Task.WhenAll(
                new MemorySemanticProjectionStore(first, clock).ClaimAsync(clock.Now, TimeSpan.FromMinutes(2), default),
                new MemorySemanticProjectionStore(second, clock).ClaimAsync(clock.Now, TimeSpan.FromMinutes(2), default));
            Assert.Single(claims, x => x is not null);
            Assert.Null(await new MemorySemanticProjectionStore(second, clock).ClaimAsync(clock.Now, TimeSpan.FromMinutes(2), default));
            var graph = await setup.MemoryProjectionJobs.AsNoTracking().SingleAsync(x => x.ProjectionTarget == MemoryProjectionTarget.Graph);
            Assert.Equal(MemoryProjectionStatus.Pending, graph.Status);
            Assert.Equal(0, graph.Attempt);
        }
        finally { await setup.Database.ExecuteSqlRawAsync("DROP SCHEMA \"" + schema + "\" CASCADE"); }
    }

    [PostgresFact]
    public async Task LifecycleEvidenceReferencesAndJobsAreAtomic()
    {
        var schema = "memory_" + Guid.NewGuid().ToString("N");
        await using var db = CreateDb(schema);
        await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA \"" + schema + "\"");
        try
        {
            await db.Database.MigrateAsync();
            var conversation = new Conversation();
            var firstMessage = conversation.AddMessage("user", "Lembra que Aegis usa PostgreSQL.");
            db.Conversations.Add(conversation); await db.SaveChangesAsync();
            var clock = new FixedClock(); using var metrics = new AegisMetrics();
            var service = new MemoryService(new MemoryStore(db), clock, metrics);
            var firstContext = new ToolExecutionContext(conversation.Id, firstMessage.Id, firstMessage.Content);
            var first = await service.RememberAsync("Aegis usa PostgreSQL.", null, null, firstContext, default);
            Assert.False(first.Deduplicated);
            Assert.Single(await db.MemoryProjectionJobs.ToListAsync());
            Assert.Equal(MemoryProjectionStatus.Pending, (await db.MemoryProjectionJobs.SingleAsync()).Status);

            var secondMessage = new ChatMessage(conversation.Id, "user", "Lembra que Aegis usa PostgreSQL.");
            db.ChatMessages.Add(secondMessage);
            await db.SaveChangesAsync();
            var secondContext = new ToolExecutionContext(conversation.Id, secondMessage.Id, secondMessage.Content);
            var duplicate = await service.RememberAsync("  aegis   USA   postgresql. ", null, null, secondContext, default);
            Assert.True(duplicate.Deduplicated);
            Assert.Equal(first.Record.Id, duplicate.Record.Id);
            Assert.Single(await db.MemoryRecords.ToListAsync());
            Assert.Equal(2, await db.MemoryEvidences.CountAsync());
            Assert.Single(await db.MemoryProjectionJobs.ToListAsync());
            await service.RememberAsync("Aegis usa PostgreSQL.", null, null, secondContext, default);
            Assert.Equal(2, await db.MemoryEvidences.CountAsync());

            clock.Now = clock.Now.AddMinutes(31);
            await Assert.ThrowsAsync<MemoryException>(() => service.UpdateAsync(first.Record.Id, "Aegis usa CockroachDB.", null, null, secondContext, default));
            var found = await service.SearchAsync("PostgreSQL", 10, conversation.Id, default);
            Assert.Single(found);
            await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync("PostgreSQL", 31, conversation.Id, default));
            var thirdMessage = new ChatMessage(conversation.Id, "user", "Corrige: Aegis usa CockroachDB.");
            db.ChatMessages.Add(thirdMessage);
            await db.SaveChangesAsync();
            var thirdContext = new ToolExecutionContext(conversation.Id, thirdMessage.Id, thirdMessage.Content);
            var replacement = await service.UpdateAsync(first.Record.Id, "Aegis usa CockroachDB.", null, null, thirdContext, default);
            db.ChangeTracker.Clear();
            var old = await db.MemoryRecords.SingleAsync(x => x.Id == first.Record.Id);
            Assert.Equal(MemoryStatus.Superseded, old.Status);
            Assert.Equal(replacement.Id, old.SupersededById);
            Assert.NotNull(old.SupersededAt);
            Assert.Equal(2, old.Revision);
            Assert.Equal(MemoryStatus.Active, (await db.MemoryRecords.SingleAsync(x => x.Id == replacement.Id)).Status);
            Assert.Empty(await service.SearchAsync("PostgreSQL", 10, conversation.Id, default));
            Assert.Single(await service.SearchAsync("CockroachDB", 10, conversation.Id, default));
            Assert.DoesNotContain("PostgreSQL", await service.GetContextAsync(conversation.Id, default) ?? "");
            Assert.Equal(3, await db.MemoryProjectionJobs.CountAsync());
            Assert.Contains(await db.MemoryProjectionJobs.ToListAsync(), x => x.AggregateId == old.Id && x.AggregateRevision == 2 && x.Operation == MemoryProjectionOperation.Delete);
            Assert.Contains(await db.MemoryProjectionJobs.ToListAsync(), x => x.AggregateId == replacement.Id && x.Operation == MemoryProjectionOperation.Upsert);

            var forgotten = await service.ForgetAsync(replacement.Id, new ToolExecutionContext(conversation.Id, Guid.Empty, ""), default);
            var timestamp = forgotten.ForgottenAt;
            await service.ForgetAsync(replacement.Id, new ToolExecutionContext(conversation.Id, Guid.Empty, ""), default);
            Assert.Equal(timestamp, forgotten.ForgottenAt);
            Assert.Empty(await service.SearchAsync("CockroachDB", 10, conversation.Id, default));
            Assert.Null(await service.GetContextAsync(conversation.Id, default));
            Assert.Equal(4, await db.MemoryProjectionJobs.CountAsync());
            Assert.All(await db.MemoryProjectionJobs.ToListAsync(), x => Assert.Equal(MemoryProjectionStatus.Pending, x.Status));
            Assert.Equal(2, await db.MemoryRecords.CountAsync());

            // Exercise the public adapters, including JSON contracts and observed IDs.
            var rememberTool = new MemoryRememberTool(service);
            var rememberedByTool = await rememberTool.ExecuteAsync(JsonSerializer.SerializeToElement(new { content = "Aegis testa memória." }), thirdContext);
            Assert.True(rememberedByTool.Success);
            var searchTool = new MemorySearchTool(service);
            var searchedByTool = await searchTool.ExecuteAsync(JsonSerializer.SerializeToElement(new { query = "testa" }), thirdContext);
            Assert.True(searchedByTool.Success);
            var observedId = JsonDocument.Parse(searchedByTool.Content).RootElement.GetProperty("memories")[0].GetProperty("memory").GetProperty("memoryId").GetGuid();
            var updateTool = new MemoryUpdateTool(service);
            var updatedByTool = await updateTool.ExecuteAsync(JsonSerializer.SerializeToElement(new { memoryId = observedId, content = "Aegis testa memória canônica." }), thirdContext);
            Assert.True(updatedByTool.Success);
            var replacementId = JsonDocument.Parse(updatedByTool.Content).RootElement.GetProperty("memory").GetProperty("memoryId").GetGuid();
            var forgetTool = new MemoryForgetTool(service);
            Assert.True((await forgetTool.ExecuteAsync(JsonSerializer.SerializeToElement(new { memoryId = replacementId }), thirdContext)).Success);

            var temporary = await service.RememberAsync("Aegis usa um teste temporário.", null, clock.Now.AddMinutes(1), thirdContext, default);
            Assert.Single(await service.SearchAsync("temporário", 10, conversation.Id, default));
            clock.Now = clock.Now.AddMinutes(2);
            Assert.Empty(await service.SearchAsync("temporário", 10, conversation.Id, default));
            var recurring = await service.RememberAsync(temporary.Record.Content, null, null, thirdContext, default);
            Assert.NotEqual(temporary.Record.Id, recurring.Record.Id);
            Assert.Equal(clock.Now, recurring.Record.ValidFrom);
            Assert.Single(await service.SearchAsync("temporário", 10, conversation.Id, default));
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP SCHEMA \"" + schema + "\" CASCADE"); }
    }

    [PostgresFact]
    public async Task ConversationDeletionRetainsEvidenceAndGraphStateRemainsCanonical()
    {
        var schema = "memory_" + Guid.NewGuid().ToString("N");
        await using var db = CreateDb(schema);
        await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA \"" + schema + "\"");
        try
        {
            await db.Database.MigrateAsync();
            var conversation = new Conversation();
            var message = conversation.AddMessage("user", "Guarda que Sakamoto namora Bisky.");
            db.Conversations.Add(conversation); await db.SaveChangesAsync();
            var clock = new FixedClock(); using var metrics = new AegisMetrics();
            var service = new MemoryService(new MemoryStore(db), clock, metrics);
            var remembered = await service.RememberAsync("Sakamoto namora Bisky.", null, null,
                new ToolExecutionContext(conversation.Id, message.Id, message.Content), default);
            var sakamoto = await service.CreateEntityAsync("Sakamoto", "person", default);
            var bisky = await service.CreateEntityAsync("Bisky", "person", default);
            var similar = await service.CreateEntityAsync("Sakamoto", "project", default);
            Assert.NotEqual(sakamoto.Id, similar.Id);
            var alias = await service.AddAliasAsync(sakamoto.Id, "o Sakamoto", default);
            Assert.Equal(alias.Id, (await service.AddAliasAsync(sakamoto.Id, "O   SAKAMOTO", default)).Id);
            Assert.Equal("O SAKAMOTO", alias.NormalizedAlias);
            var relation = await service.CreateRelationAsync(sakamoto.Id, "dates", bisky.Id,
                clock.Now.AddMonths(-6), null, default);
            Assert.Equal(relation.Id, (await service.CreateRelationAsync(sakamoto.Id, "DATES", bisky.Id,
                clock.Now.AddMonths(-6), null, default)).Id);
            await service.SupportRelationAsync(relation.Id, remembered.Record.Id, default);
            await service.SupportRelationAsync(relation.Id, remembered.Record.Id, default);
            var supportingMessage = new ChatMessage(conversation.Id, "user", "Eles estão juntos há seis meses.");
            db.ChatMessages.Add(supportingMessage); await db.SaveChangesAsync();
            var secondMemory = await service.RememberAsync("Sakamoto e Bisky estão juntos há seis meses.", null, null,
                new ToolExecutionContext(conversation.Id, supportingMessage.Id, supportingMessage.Content), default);
            await service.SupportRelationAsync(relation.Id, secondMemory.Record.Id, default);
            Assert.Equal(2, await db.MemoryRelationEvidences.CountAsync());
            Assert.Equal("DATES", (await db.MemoryRelations.SingleAsync()).Predicate);
            Assert.NotNull((await db.MemoryRelations.SingleAsync()).ValidFrom);
            Assert.All(await db.MemoryProjectionJobs.ToListAsync(), x => Assert.Equal(MemoryProjectionStatus.Pending, x.Status));
            Assert.Contains(await db.MemoryProjectionJobs.ToListAsync(), x => x.ProjectionTarget == MemoryProjectionTarget.Graph && x.AggregateType == MemoryAggregateType.MemoryRelation);
            db.Conversations.Remove(conversation); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            var evidences = await db.MemoryEvidences.ToListAsync();
            Assert.Equal(2, evidences.Count);
            Assert.All(evidences, evidence => { Assert.Null(evidence.SourceConversationId); Assert.Null(evidence.SourceMessageId); });
            Assert.All(await db.MemoryRecords.ToListAsync(), x => Assert.Equal(MemoryStatus.Active, x.Status));
            Assert.Equal(2, await db.MemoryRelationEvidences.CountAsync());
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP SCHEMA \"" + schema + "\" CASCADE"); }
    }

    [PostgresFact]
    public async Task ConcurrentExactRequestsCreateOneActiveRecordAndOneJob()
    {
        var schema = "memory_" + Guid.NewGuid().ToString("N");
        await using var setup = CreateDb(schema);
        await setup.Database.ExecuteSqlRawAsync("CREATE SCHEMA \"" + schema + "\"");
        try
        {
            await setup.Database.MigrateAsync();
            var conversation = new Conversation();
            var firstMessage = conversation.AddMessage("user", "Lembra que Aegis usa PostgreSQL.");
            setup.Conversations.Add(conversation);
            var secondMessage = new ChatMessage(conversation.Id, "user", "Guarda que Aegis usa PostgreSQL.");
            setup.ChatMessages.Add(secondMessage);
            await setup.SaveChangesAsync();
            await using var firstDb = CreateDb(schema);
            await using var secondDb = CreateDb(schema);
            var clock = new FixedClock(); using var metrics = new AegisMetrics();
            var first = new MemoryService(new MemoryStore(firstDb), clock, metrics);
            var second = new MemoryService(new MemoryStore(secondDb), clock, metrics);
            var writes = await Task.WhenAll(
                first.RememberAsync("Aegis usa PostgreSQL.", null, null,
                    new ToolExecutionContext(conversation.Id, firstMessage.Id, firstMessage.Content), default),
                second.RememberAsync(" aegis  USA postgresql. ", null, null,
                    new ToolExecutionContext(conversation.Id, secondMessage.Id, secondMessage.Content), default));
            Assert.Equal(writes[0].Record.Id, writes[1].Record.Id);
            await using var check = CreateDb(schema);
            Assert.Single(await check.MemoryRecords.ToListAsync());
            Assert.Equal(2, await check.MemoryEvidences.CountAsync());
            Assert.Single(await check.MemoryProjectionJobs.ToListAsync());
        }
        finally { await setup.Database.ExecuteSqlRawAsync("DROP SCHEMA \"" + schema + "\" CASCADE"); }
    }

    [PostgresFact]
    public async Task BadProvenanceRollsBackRecordAndProjectionJob()
    {
        var schema = "memory_" + Guid.NewGuid().ToString("N");
        await using var db = CreateDb(schema);
        await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA \"" + schema + "\"");
        try
        {
            await db.Database.MigrateAsync();
            var clock = new FixedClock(); using var metrics = new AegisMetrics();
            var service = new MemoryService(new MemoryStore(db), clock, metrics);
            await Assert.ThrowsAnyAsync<Exception>(() => service.RememberAsync("Não deve persistir.", null, null,
                new ToolExecutionContext(Guid.NewGuid(), Guid.NewGuid(), "pedido"), default));
            await using var check = CreateDb(schema);
            Assert.Empty(await check.MemoryRecords.ToListAsync());
            Assert.Empty(await check.MemoryProjectionJobs.ToListAsync());
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP SCHEMA \"" + schema + "\" CASCADE"); }
    }
}
