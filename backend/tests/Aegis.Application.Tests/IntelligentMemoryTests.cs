using Aegis.Application.Memory;
using Aegis.Application.Observability;
using Aegis.Application.Tools;
using Aegis.Api.Controllers;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Memory;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class IntelligentMemoryTests
{
    public sealed class PostgresFactAttribute : FactAttribute
    {
        public PostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEGIS_MEMORY_TEST_DATABASE")))
                Skip = "Set AEGIS_MEMORY_TEST_DATABASE to a disposable PostgreSQL database.";
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class NoEmbedding : IMemoryEmbeddingClient
    {
        public Task<float[]> EmbedAsync(string text, CancellationToken ct) => throw new InvalidOperationException("Disabled in test");
    }

    private sealed class NoVectors : IMemoryVectorStore
    {
        public Task<bool> EnsureCollectionAsync(CancellationToken ct) => Task.FromResult(false);
        public Task<MemoryVectorPoint?> GetPointAsync(Guid id, CancellationToken ct) => Task.FromResult<MemoryVectorPoint?>(null);
        public Task UpsertAsync(MemoryVectorPoint point, float[] vector, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<MemoryVectorCandidate>> SearchAsync(float[] vector, int limit, double threshold, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MemoryVectorCandidate>>([]);
    }

    private sealed class FakeEmbedding : IMemoryEmbeddingClient
    {
        public Task<float[]> EmbedAsync(string text, CancellationToken ct) => Task.FromResult<float[]>([1, 0, 0]);
    }

    private sealed class SlowEmbedding : IMemoryEmbeddingClient
    {
        public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return [1, 0, 0];
        }
    }

    private sealed class PausingExtractor : IMemoryExtractionClient
    {
        private readonly TaskCompletionSource<MemoryExtractionOutput> released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<MemoryExtractionOutput> ExtractAsync(MemoryExtractionInput input, CancellationToken ct)
        {
            Started.TrySetResult(true);
            return released.Task.WaitAsync(ct);
        }
        public void Release(MemoryExtractionOutput output) => released.TrySetResult(output);
    }

    private sealed class FakeVectors : IMemoryVectorStore
    {
        public List<MemoryVectorCandidate> Candidates { get; } = [];
        public Dictionary<Guid, MemoryVectorPoint> Projected { get; } = [];
        public bool Offline { get; set; }
        public Task<bool> EnsureCollectionAsync(CancellationToken ct) => Task.FromResult(false);
        public Task<MemoryVectorPoint?> GetPointAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(Projected.GetValueOrDefault(id));
        public Task UpsertAsync(MemoryVectorPoint point, float[] vector, CancellationToken ct)
        { Projected[point.MemoryId] = point; return Task.CompletedTask; }
        public Task DeleteAsync(Guid id, CancellationToken ct)
        { Projected.Remove(id); return Task.CompletedTask; }
        public Task<IReadOnlyList<MemoryVectorCandidate>> SearchAsync(float[] vector, int limit, double threshold, CancellationToken ct) =>
            Offline ? throw new MemorySemanticException("qdrant_unavailable") :
                Task.FromResult<IReadOnlyList<MemoryVectorCandidate>>(Candidates.Take(limit).ToArray());
    }

    private sealed class FakeGraph : IMemoryGraphStore
    {
        public bool Offline { get; set; }
        public List<MemoryGraphCandidatePath> Paths { get; } = [];
        public Task EnsureSchemaAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<MemoryGraphEntityProjection?> GetEntityProjectionAsync(Guid id, CancellationToken ct) => Task.FromResult<MemoryGraphEntityProjection?>(null);
        public Task UpsertEntityAsync(MemoryGraphEntityProjection entity, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteEntityAsync(Guid id, CancellationToken ct) => Task.CompletedTask;
        public Task<MemoryGraphRelationProjection?> GetRelationProjectionAsync(Guid id, CancellationToken ct) => Task.FromResult<MemoryGraphRelationProjection?>(null);
        public Task UpsertRelationAsync(MemoryGraphRelationProjection relation, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteRelationAsync(Guid id, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<MemoryGraphCandidatePath>> TraverseAsync(IReadOnlyList<Guid> startIds, MemoryGraphDirection direction,
            IReadOnlyList<string> predicates, int maxDepth, int limit, DateTimeOffset asOf, CancellationToken ct) =>
            Offline ? throw new MemoryGraphException("neo4j_unavailable") :
                Task.FromResult<IReadOnlyList<MemoryGraphCandidatePath>>(Paths.Where(x => startIds.Contains(x.EntityIds[0]) &&
                    x.RelationIds.Count <= maxDepth).Take(limit).ToArray());
        public Task DeleteManagedProjectionAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private static AegisDbContext Db(string schema) => new(new DbContextOptionsBuilder<AegisDbContext>()
        .UseNpgsql(Environment.GetEnvironmentVariable("AEGIS_MEMORY_TEST_DATABASE") + ";Search Path=" + schema,
            x => x.MigrationsHistoryTable("__EFMigrationsHistory", schema)).Options);

    private static async Task<(AegisDbContext Db, string Schema)> NewDbAsync()
    {
        var schema = "intelligent_" + Guid.NewGuid().ToString("N");
        var db = Db(schema);
        await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA \"" + schema + "\"");
        await db.Database.MigrateAsync();
        return (db, schema);
    }

    private static async Task<MemoryExtractionSource> SourceAsync(AegisDbContext db, string target)
    {
        var conversation = new Conversation();
        var message = conversation.AddMessage("user", target);
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();
        return new(conversation.Id, message.Id, target, message.CreatedAt, []);
    }

    private static MemoryExtractionCandidate Candidate(string content, string action = "create", string? old = null,
        DateTimeOffset? transitionAt = null, IReadOnlyList<MemoryExtractionEntity>? entities = null,
        IReadOnlyList<MemoryExtractionRelationAction>? relations = null) =>
        new(content, action, old, transitionAt, null, null, entities ?? [], relations ?? []);

    private static MemoryAutomaticIngestionService Ingestion(AegisDbContext db, Clock clock, AegisMetrics metrics)
    {
        var store = new MemoryStore(db);
        var semantic = new MemorySemanticSearch(store, new NoEmbedding(), new NoVectors(),
            new MemorySemanticOptions { Enabled = false }, clock, metrics);
        return new(store, semantic, clock, metrics);
    }

    [PostgresFact]
    public async Task AutomaticFactsRelationsReplayAndEvidenceStayCanonical()
    {
        var (db, schema) = await NewDbAsync();
        try
        {
            var clock = new Clock(); using var metrics = new AegisMetrics();
            var ingestion = Ingestion(db, clock, metrics);
            var source = await SourceAsync(db, "Meu amigo Sakamoto namora Bisky.");
            var input = new MemoryExtractionInput(source.Target, [], [], [], []);
            var pedro = new MemoryExtractionEntity("p", "Meu", "Pedro", "PERSON", []);
            var sakamoto = new MemoryExtractionEntity("s", "Sakamoto", "Sakamoto", "PERSON", []);
            var bisky = new MemoryExtractionEntity("b", "Bisky", "Bisky", "PERSON", []);
            var result = new MemoryExtractionOutput([
                Candidate("Pedro é amigo de Sakamoto.", entities: [pedro, sakamoto], relations: [
                    new("create", null, "p", "FRIEND_OF", "s", null, null, null)]),
                Candidate("Sakamoto namora Bisky.", entities: [sakamoto, bisky], relations: [
                    new("create", null, "s", "DATES", "b", null, null, null)])
            ]);
            var first = await ingestion.ApplyAsync(source, input, result, default);
            Assert.Equal(2, first.Created);
            Assert.Equal(2, first.GraphMutations);
            Assert.Equal(2, await db.MemoryRecords.CountAsync());
            Assert.Equal(3, await db.MemoryEntities.CountAsync());
            Assert.Equal(2, await db.MemoryRelations.CountAsync());
            Assert.Equal(2, await db.MemoryRelationEvidences.CountAsync());
            var jobs = await db.MemoryProjectionJobs.CountAsync();
            await ingestion.ApplyAsync(source, input, result, default);
            Assert.Equal(2, await db.MemoryRecords.CountAsync());
            Assert.Equal(2, await db.MemoryEvidences.CountAsync());
            Assert.Equal(2, await db.MemoryRelations.CountAsync());
            Assert.Equal(2, await db.MemoryRelationEvidences.CountAsync());
            Assert.Equal(jobs, await db.MemoryProjectionJobs.CountAsync());
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP SCHEMA \"" + schema + "\" CASCADE"); await db.DisposeAsync(); }
    }

    [PostgresFact]
    public async Task CorrectionTransitionAndHistoricalRecurrence()
    {
        var (db, schema) = await NewDbAsync();
        try
        {
            var clock = new Clock(); using var metrics = new AegisMetrics();
            var ingestion = Ingestion(db, clock, metrics);
            var firstSource = await SourceAsync(db, "Minha GPU é RX 6700 XT.");
            await ingestion.ApplyAsync(firstSource, new(firstSource.Target, [], [], [], []),
                new([Candidate("Pedro usa RX 6700 XT.")]), default);
            var rx = await db.MemoryRecords.SingleAsync();
            var transition = clock.Now.AddMinutes(1);
            clock.Now = transition;
            var secondSource = await SourceAsync(db, "Troquei minha GPU por uma RTX 5080.");
            var input = new MemoryExtractionInput(secondSource.Target, [], [new("m1", rx)], [], []);
            var output = new MemoryExtractionOutput([Candidate("Pedro usa RTX 5080.", "transition", "m1", transition)]);
            await ingestion.ApplyAsync(secondSource, input, output, default);
            await ingestion.ApplyAsync(secondSource, input, output, default);
            var records = await db.MemoryRecords.AsNoTracking().ToListAsync();
            Assert.Equal(2, records.Count);
            var old = records.Single(x => x.Id == rx.Id);
            var current = records.Single(x => x.Id != rx.Id);
            Assert.Equal(MemoryStatus.Active, old.Status);
            Assert.Equal(transition, old.ValidUntil);
            Assert.Equal(transition, current.ValidFrom);
            var store = new MemoryStore(db);
            Assert.Single(await store.LoadActiveByIdsAsync([old.Id, current.Id], transition.AddTicks(-1), default));
            Assert.Equal(current.Id, Assert.Single(await store.LoadActiveByIdsAsync([old.Id, current.Id], transition, default)).Id);
            var historical = await new MemoryService(store, clock, metrics).SearchHybridAsync("RX 6700", 10,
                transition.AddTicks(-1), firstSource.ConversationId, default);
            Assert.Equal(old.Id, Assert.Single(historical.Memories).Id);
            Assert.Contains("RX 6700", await store.GetContextAsync(firstSource.ConversationId, clock.Now, default));
            // The same content can become true again after a disjoint interval.
            (await db.MemoryRecords.SingleAsync(x => x.Id == current.Id))
                .CloseValidity(transition.AddHours(1), transition.AddHours(1));
            await db.SaveChangesAsync();
            clock.Now = transition.AddHours(2);
            var again = await SourceAsync(db, "Voltei para minha RX 6700 XT.");
            await ingestion.ApplyAsync(again, new(again.Target, [], [], [], []),
                new([Candidate("Pedro usa RX 6700 XT.")]), default);
            Assert.Equal(3, await db.MemoryRecords.CountAsync());
            Assert.Equal(2, await db.MemoryRecords.CountAsync(x => x.ContentHash == rx.ContentHash));

            var correctionSource = await SourceAsync(db, "Não, Windows era o correto; Linux estava errado.");
            await ingestion.ApplyAsync(correctionSource, new(correctionSource.Target, [], [], [], []),
                new([Candidate("Pedro usa Linux.")]), default);
            var linux = await db.MemoryRecords.SingleAsync(x => x.Content == "Pedro usa Linux.");
            await ingestion.ApplyAsync(correctionSource, new(correctionSource.Target, [], [new("m1", linux)], [], []),
                new([Candidate("Pedro usa Windows.", "correct", "m1")]), default);
            Assert.Equal(MemoryStatus.Superseded, (await db.MemoryRecords.AsNoTracking().SingleAsync(x => x.Id == linux.Id)).Status);
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP SCHEMA \"" + schema + "\" CASCADE"); await db.DisposeAsync(); }
    }

    [PostgresFact]
    public async Task AmbiguousEntityKeepsTextButSkipsGraphAndInvalidRefsAreSafe()
    {
        var (db, schema) = await NewDbAsync();
        try
        {
            var clock = new Clock(); using var metrics = new AegisMetrics();
            var service = new MemoryService(new MemoryStore(db), clock, metrics);
            var a = await service.CreateEntityAsync("Bisky", "PERSON", default);
            var b = await service.CreateEntityAsync("Beatriz", "PERSON", default);
            await service.AddAliasAsync(b.Id, "Bisky", default);
            var beforeEntities = await db.MemoryEntities.CountAsync();
            var ingestion = Ingestion(db, clock, metrics);
            var source = await SourceAsync(db, "Sakamoto namora Bisky.");
            var result = await ingestion.ApplyAsync(source, new(source.Target, [], [], [], []), new([
                Candidate("Sakamoto namora Bisky.", entities: [
                    new("s", "Sakamoto", "Sakamoto", "PERSON", []),
                    new("b", "Bisky", "Bisky", "PERSON", [])], relations: [
                        new("create", null, "s", "DATES", "b", null, null, null)])]), default);
            Assert.Equal(1, result.Created);
            Assert.Equal(1, result.Skipped);
            Assert.Single(await db.MemoryRecords.ToListAsync());
            Assert.Empty(await db.MemoryRelations.ToListAsync());
            Assert.Equal(beforeEntities, await db.MemoryEntities.CountAsync());
            Assert.Equal(a.Id, (await db.MemoryEntities.SingleAsync(x => x.CanonicalName == "Bisky")).Id);
            var invalid = await ingestion.ApplyAsync(source, new(source.Target, [], [], [], []),
                new([Candidate("Invenção.", "correct", "m999")]), default);
            Assert.Equal(1, invalid.Skipped);
            Assert.Equal(1, await db.MemoryRecords.CountAsync());
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP SCHEMA \"" + schema + "\" CASCADE"); await db.DisposeAsync(); }
    }

    [PostgresFact]
    public async Task FailedCandidateDoesNotLeakTrackedChangesIntoNextCandidate()
    {
        var (db, schema) = await NewDbAsync();
        try
        {
            var clock = new Clock(); using var metrics = new AegisMetrics();
            var source = await SourceAsync(db, "Pedro usa PostgreSQL e prefere backend.");
            var input = new MemoryExtractionInput(source.Target, [], [], [], []);
            var output = new MemoryExtractionOutput([
                Candidate("Pedro usa PostgreSQL.", entities: [
                    new("p", "Pedro", "Pedro", "PERSON", []),
                    new("db", "PostgreSQL", "PostgreSQL", "TECHNOLOGY", [])], relations: [
                    new("create", null, "p", "bad?", "db", null, null, null)]),
                Candidate("Pedro prefere backend.")
            ]);

            var summary = await Ingestion(db, clock, metrics).ApplyAsync(source, input, output, default);

            Assert.Equal(1, summary.Skipped);
            Assert.Equal("Pedro prefere backend.", Assert.Single(await db.MemoryRecords.AsNoTracking().ToListAsync()).Content);
            Assert.Empty(await db.MemoryEntities.AsNoTracking().ToListAsync());
            Assert.Empty(await db.MemoryRelations.AsNoTracking().ToListAsync());
            Assert.Single(await db.MemoryProjectionJobs.AsNoTracking().ToListAsync());
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP SCHEMA \"" + schema + "\" CASCADE"); await db.DisposeAsync(); }
    }

    [PostgresFact]
    public async Task ExtractionJobClaimsLeaseAndSkipsDeletedSource()
    {
        var (db, schema) = await NewDbAsync();
        try
        {
            var source = await SourceAsync(db, "Prefiro backend.");
            db.MemoryExtractionJobs.Add(new MemoryExtractionJob(source.ConversationId, source.UserMessageId, source.ObservedAt));
            await db.SaveChangesAsync();
            var jobs = new MemoryExtractionJobStore(db);
            var claim = await jobs.ClaimAsync(source.ObservedAt, TimeSpan.FromMinutes(2), default);
            Assert.NotNull(claim);
            Assert.Null(await jobs.ClaimAsync(source.ObservedAt.AddMinutes(1), TimeSpan.FromMinutes(2), default));
            var recovered = await jobs.ClaimAsync(source.ObservedAt.AddMinutes(3), TimeSpan.FromMinutes(2), default);
            Assert.NotNull(recovered);
            Assert.Equal(2, recovered.Attempt);
            Assert.True(await jobs.FailAsync(recovered, "memory_extraction_timeout", source.ObservedAt.AddMinutes(3), null, default));
            Assert.Equal(MemoryExtractionStatus.Failed, (await db.MemoryExtractionJobs.AsNoTracking().SingleAsync()).Status);
            var conversation = await db.Conversations.SingleAsync(x => x.Id == source.ConversationId);
            conversation.Delete(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
            Assert.Null(await jobs.ReadSourceAsync(recovered, default));
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP SCHEMA \"" + schema + "\" CASCADE"); await db.DisposeAsync(); }
    }

    [PostgresFact]
    public async Task ExplicitForgetSuppressesAlreadyClaimedExtractionBeforeCandidateWrite()
    {
        var (db, schema) = await NewDbAsync();
        try
        {
            var clock = new Clock(); using var metrics = new AegisMetrics();
            var store = new MemoryStore(db);
            var service = new MemoryService(store, clock, metrics);
            var initial = await SourceAsync(db, "Lembra que eu prefiro backend.");
            var record = (await service.RememberAsync("Pedro prefere backend.", null, null,
                new(initial.ConversationId, initial.UserMessageId, initial.Target), default)).Record;
            var forget = await SourceAsync(db, "Esquece que eu prefiro backend.");
            db.MemoryExtractionJobs.Add(new MemoryExtractionJob(forget.ConversationId, forget.UserMessageId, clock.Now));
            await store.ObserveAsync(forget.ConversationId, [record], "memory_search", clock.Now, default);
            await db.SaveChangesAsync();

            var extractor = new PausingExtractor();
            using var services = new ServiceCollection().AddLogging()
                .AddSingleton<TimeProvider>(clock).AddSingleton(metrics)
                .AddSingleton<IMemoryExtractionClient>(extractor)
                .AddSingleton(new MemorySemanticOptions { Enabled = false })
                .AddSingleton<IMemoryEmbeddingClient>(new NoEmbedding())
                .AddSingleton<IMemoryVectorStore>(new NoVectors())
                .AddScoped(_ => Db(schema))
                .AddScoped<IMemoryStore, MemoryStore>()
                .AddScoped<IMemoryExtractionJobStore, MemoryExtractionJobStore>()
                .AddScoped<MemorySemanticSearch>()
                .AddScoped<MemoryAutomaticIngestionService>()
                .BuildServiceProvider();
            var worker = new MemoryExtractionWorker(services.GetRequiredService<IServiceScopeFactory>(),
                new MemoryAutomaticOptions(), clock, metrics, NullLogger<MemoryExtractionWorker>.Instance);
            var running = worker.ProcessNextAsync(default);
            await extractor.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await service.ForgetAsync(record.Id,
                new ToolExecutionContext(forget.ConversationId, forget.UserMessageId, forget.Target), default);
            extractor.Release(new([Candidate("Pedro prefere backend.")]));
            Assert.True(await running.WaitAsync(TimeSpan.FromSeconds(15)));

            Assert.Equal(MemoryStatus.Forgotten, (await db.MemoryRecords.AsNoTracking().SingleAsync()).Status);
            Assert.Empty(await db.MemoryRecords.AsNoTracking().Where(x => x.Status == MemoryStatus.Active).ToListAsync());
            Assert.Empty(await db.MemoryEvidences.AsNoTracking().Where(x => x.SourceMessageId == forget.UserMessageId &&
                x.SourceKind == MemorySourceKind.UserStatement).ToListAsync());
            Assert.Equal(MemoryExtractionStatus.Suppressed,
                (await db.MemoryExtractionJobs.AsNoTracking().SingleAsync()).Status);
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP SCHEMA \"" + schema + "\" CASCADE"); await db.DisposeAsync(); }
    }

    [PostgresFact]
    public async Task ExplicitForgetSuppressesPendingExtractionJobIdempotently()
    {
        var (db, schema) = await NewDbAsync();
        try
        {
            var clock = new Clock(); using var metrics = new AegisMetrics();
            var store = new MemoryStore(db); var service = new MemoryService(store, clock, metrics);
            var initial = await SourceAsync(db, "Lembra que eu prefiro backend.");
            var record = (await service.RememberAsync("Pedro prefere backend.", null, null,
                new(initial.ConversationId, initial.UserMessageId, initial.Target), default)).Record;
            var forget = await SourceAsync(db, "Esquece que eu prefiro backend.");
            db.MemoryExtractionJobs.Add(new MemoryExtractionJob(forget.ConversationId, forget.UserMessageId, clock.Now));
            await store.ObserveAsync(forget.ConversationId, [record], "memory_search", clock.Now, default);
            await db.SaveChangesAsync();
            var context = new ToolExecutionContext(forget.ConversationId, forget.UserMessageId, forget.Target);
            await service.ForgetAsync(record.Id, context, default);
            var suppressed = await db.MemoryExtractionJobs.AsNoTracking().SingleAsync();
            Assert.Equal(MemoryExtractionStatus.Suppressed, suppressed.Status);
            Assert.Null(await new MemoryExtractionJobStore(db).ClaimAsync(clock.Now, TimeSpan.FromMinutes(2), default));
            await service.ForgetAsync(record.Id, context, default);
            Assert.Equal(suppressed.CompletedAt, (await db.MemoryExtractionJobs.AsNoTracking().SingleAsync()).CompletedAt);
            Assert.Single(await db.MemoryProjectionJobs.AsNoTracking().Where(x => x.AggregateId == record.Id &&
                x.Operation == MemoryProjectionOperation.Delete).ToListAsync());
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP SCHEMA \"" + schema + "\" CASCADE"); await db.DisposeAsync(); }
    }

    [PostgresFact]
    public async Task ExtractionBeforeExplicitForgetEndsForgottenAndRemovesHistoricalRelation()
    {
        var (db, schema) = await NewDbAsync();
        try
        {
            var clock = new Clock(); using var metrics = new AegisMetrics();
            var store = new MemoryStore(db); var service = new MemoryService(store, clock, metrics);
            var initial = await SourceAsync(db, "Lembra que Sakamoto namora Bisky.");
            var record = (await service.RememberAsync("Sakamoto namora Bisky.", null, null,
                new(initial.ConversationId, initial.UserMessageId, initial.Target), default)).Record;
            var sakamoto = await service.CreateEntityAsync("Sakamoto", "PERSON", default);
            var bisky = await service.CreateEntityAsync("Bisky", "PERSON", default);
            var relation = await service.CreateRelationAsync(sakamoto.Id, "DATES", bisky.Id, null, null, default);
            await service.SupportRelationAsync(relation.Id, record.Id, default);
            var forget = await SourceAsync(db, "Esquece que Sakamoto namora Bisky.");
            db.MemoryExtractionJobs.Add(new MemoryExtractionJob(forget.ConversationId, forget.UserMessageId, clock.Now));
            await store.ObserveAsync(forget.ConversationId, [record], "memory_search", clock.Now, default);
            await db.SaveChangesAsync();
            var jobs = new MemoryExtractionJobStore(db);
            var claim = (await jobs.ClaimAsync(clock.Now, TimeSpan.FromMinutes(2), default))!;
            var source = (await jobs.ReadSourceAsync(claim, default))!;
            var input = new MemoryExtractionInput(source.Target, [], [new("m1", record)], [], []);
            await Ingestion(db, clock, metrics).ApplyAsync(source, input,
                new([Candidate(record.Content, "reinforce", "m1")]), default);
            Assert.Single(await db.MemoryEvidences.AsNoTracking().Where(x => x.SourceMessageId == forget.UserMessageId).ToListAsync());

            await service.ForgetAsync(record.Id,
                new ToolExecutionContext(forget.ConversationId, forget.UserMessageId, forget.Target), default);
            Assert.Equal(MemoryStatus.Forgotten, (await db.MemoryRecords.AsNoTracking().SingleAsync()).Status);
            Assert.Equal(MemoryStatus.Forgotten, (await db.MemoryRelations.AsNoTracking().SingleAsync()).Status);
            Assert.Contains(await db.MemoryProjectionJobs.AsNoTracking().ToListAsync(), x => x.AggregateId == record.Id &&
                x.Operation == MemoryProjectionOperation.Delete && x.ProjectionTarget == MemoryProjectionTarget.Semantic);
            Assert.Contains(await db.MemoryProjectionJobs.AsNoTracking().ToListAsync(), x => x.AggregateId == relation.Id &&
                x.Operation == MemoryProjectionOperation.Delete && x.ProjectionTarget == MemoryProjectionTarget.Graph);
            Assert.Equal(MemoryExtractionStatus.Suppressed, (await db.MemoryExtractionJobs.AsNoTracking().SingleAsync()).Status);
            Assert.False(await jobs.CompleteAsync(claim, new(1, 0, 1, 0, 0, 0, 0), clock.Now, default));

            var graph = new FakeGraph();
            graph.Paths.Add(new([sakamoto.Id, bisky.Id], [relation.Id])); // stale projected edge
            var vectors = new FakeVectors(); vectors.Candidates.Add(new(record.Id, 0.9)); // stale point
            var semanticOptions = new MemorySemanticOptions { EmbeddingDimensions = 3 };
            var graphOptions = new MemoryGraphOptions();
            var semantic = new MemorySemanticSearch(store, new FakeEmbedding(), vectors,
                semanticOptions, clock, metrics);
            var query = new MemoryGraphQuery(graph, new MemoryGraphProjectionStore(db, clock),
                graphOptions, clock, metrics, store);
            var hybrid = new MemoryHybridRetriever(store, semantic, query, graphOptions, semanticOptions,
                new MemoryAutoContextOptions(), clock, metrics);
            foreach (var asOf in new DateTimeOffset?[] { null, initial.ObservedAt })
            {
                var result = await hybrid.SearchAsync("Sakamoto", 5, asOf, false, default);
                Assert.Empty(result.Memories);
                Assert.Empty(result.Paths);
            }
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP SCHEMA \"" + schema + "\" CASCADE"); await db.DisposeAsync(); }
    }

    [PostgresFact]
    public async Task ExplicitAliasAndPurchaseModalityAreConservative()
    {
        var (db, schema) = await NewDbAsync();
        try
        {
            var clock = new Clock(); using var metrics = new AegisMetrics();
            var ingestion = Ingestion(db, clock, metrics);
            var alias = await SourceAsync(db, "Na faculdade me chamam de Vecna.");
            await ingestion.ApplyAsync(alias, new(alias.Target, [], [], [], []), new([
                Candidate("Pedro é chamado de Vecna na faculdade.", entities: [
                    new("p", "me", "Pedro", "PERSON", ["Vecna"])])]), default);
            Assert.Equal("Vecna", Assert.Single(await db.MemoryEntityAliases.ToListAsync()).Alias);
            var plan = await SourceAsync(db, "Estou pensando em comprar uma RTX 5090.");
            var unsafeOutput = new MemoryExtractionOutput([
                Candidate("Pedro possui uma RTX 5090.", entities: [
                    new("p", "Estou", "Pedro", "PERSON", []),
                    new("g", "RTX 5090", "RTX 5090", "DEVICE", [])], relations: [
                    new("create", null, "p", "OWNS", "g", null, null, null)])]);
            Assert.Equal(1, (await ingestion.ApplyAsync(plan, new(plan.Target, [], [], [], []), unsafeOutput, default)).Skipped);
            Assert.DoesNotContain(await db.MemoryRecords.ToListAsync(), x => x.Content.Contains("possui"));
            var safeOutput = new MemoryExtractionOutput([
                Candidate("Pedro considera comprar uma RTX 5090.")]);
            await ingestion.ApplyAsync(plan, new(plan.Target, [], [], [], []), safeOutput, default);
            Assert.Contains(await db.MemoryRecords.ToListAsync(), x => x.Content.Contains("considera comprar"));
            var chatter = await SourceAsync(db, "kkkkkkkk");
            var summary = await ingestion.ApplyAsync(chatter, new(chatter.Target, [], [], [], []), new([]), default);
            Assert.Equal(0, summary.Candidates);
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP SCHEMA \"" + schema + "\" CASCADE"); await db.DisposeAsync(); }
    }

    [Fact]
    public async Task ExplicitSecretsAreRejectedBeforePersistence()
    {
        using var metrics = new AegisMetrics();
        var service = new MemoryService(null!, TimeProvider.System, metrics);
        var secret = "Lembra que minha API key é sk-" + new string('a', 32);
        var error = await Assert.ThrowsAsync<MemoryException>(() => service.RememberAsync(secret, null, null,
            new(Guid.NewGuid(), Guid.NewGuid(), secret), default));
        Assert.Equal("memory_secret_not_allowed", error.Code);
        Assert.DoesNotContain("sk-", error.Message);
    }

    [PostgresFact]
    public async Task DiagnosticsAreOptInAndExplainCanonicalIngestionAndRetrieval()
    {
        var (db, schema) = await NewDbAsync();
        try
        {
            var clock = new Clock(); using var metrics = new AegisMetrics();
            var source = await SourceAsync(db, "Meu PC tinha 16 GB de RAM.");
            db.MemoryExtractionJobs.Add(new MemoryExtractionJob(source.ConversationId, source.UserMessageId, clock.Now));
            await db.SaveChangesAsync();
            var store = new MemoryStore(db);
            var semanticOptions = new MemorySemanticOptions { EmbeddingDimensions = 3 };
            var semantic = new MemorySemanticSearch(store, new FakeEmbedding(), new FakeVectors { Offline = true },
                semanticOptions, clock, metrics);
            var summary = await new MemoryAutomaticIngestionService(store, semantic, clock, metrics)
                .ApplyAsync(source, new(source.Target, [], [], [], []),
                    new([Candidate("O PC de Pedro tinha 16 GB de RAM."),
                        Candidate("O PC de Pedro tem 32 GB de RAM.", "correct", "m9")]), default);
            var jobs = new MemoryExtractionJobStore(db);
            var claim = await jobs.ClaimAsync(clock.Now, TimeSpan.FromMinutes(2), default);
            Assert.True(await jobs.CompleteAsync(claim!, summary, clock.Now, default));
            var hybrid = new MemoryHybridRetriever(store, semantic, null!,
                new MemoryGraphOptions { Enabled = false }, semanticOptions,
                new MemoryAutoContextOptions(), clock, metrics);
            var disabled = new MemoryDiagnosticsController(db, hybrid, new ConfigurationBuilder().Build());
            Assert.IsType<NotFoundResult>(await disabled.Message(source.UserMessageId, default));
            Assert.IsType<NotFoundResult>(await disabled.Retrieval(
                new("Quanto de RAM meu PC tem hoje?"), default));
            var enabled = new MemoryDiagnosticsController(db, hybrid, new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                    { ["AEGIS_MEMORY_DIAGNOSTICS_ENABLED"] = "true" }).Build());
            var messageResult = Assert.IsType<OkObjectResult>(await enabled.Message(source.UserMessageId, default));
            var messageJson = System.Text.Json.JsonSerializer.Serialize(messageResult.Value);
            Assert.Contains("outcome", messageJson);
            Assert.Contains("invalid_memory_ref", messageJson);
            Assert.Contains("16 GB", messageJson);
            Assert.Contains("Completed", messageJson);
            var retrievalResult = Assert.IsType<OkObjectResult>(await enabled.Retrieval(
                new("Quanto de RAM meu PC tem hoje?"), default));
            var retrievalJson = System.Text.Json.JsonSerializer.Serialize(retrievalResult.Value);
            Assert.Contains("canonical_text_fallback", retrievalJson);
            Assert.Contains("qdrant_unavailable", retrievalJson);
            Assert.Contains("16 GB", retrievalJson);
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP SCHEMA \"" + schema + "\" CASCADE"); await db.DisposeAsync(); }
    }

    [PostgresFact]
    public async Task CanonicalLexicalFallbackKeepsTopicPrecisionWhenEmbeddingsFail()
    {
        var (db, schema) = await NewDbAsync();
        try
        {
            var clock = new Clock(); using var metrics = new AegisMetrics();
            var store = new MemoryStore(db); var service = new MemoryService(store, clock, metrics);
            async Task<MemoryRecord> Remember(string content)
            {
                var source = await SourceAsync(db, content);
                return (await service.RememberAsync(content, null, null,
                    new(source.ConversationId, source.UserMessageId, source.Target), default)).Record;
            }
            var faze = await Remember("O time favorito de Rainbow Six do Pedro é a FaZe Clan.");
            var monitor = await Remember("O monitor de Pedro é QHD a 180 Hz.");
            var project = await Remember("Pedro desenvolve a Aegis.");
            var postgres = await Remember("A Aegis usa PostgreSQL como fonte canônica do sistema de memória.");
            var ram = await Remember("O PC de Pedro tem 32 GB de RAM.");
            var historical = await store.SearchAsync("Historicamente, qual time eu dizia gostar mais depois da FaZe?",
                10, clock.Now, default);
            Assert.Equal(faze.Id, Assert.Single(historical).Id);
            var database = await store.SearchAsync(
                "Qual banco é usado como fonte canônica de memória pelo projeto que eu desenvolvo?",
                10, clock.Now, default);
            Assert.Contains(database, x => x.Id == postgres.Id);
            Assert.Contains(database, x => x.Id == project.Id);
            Assert.DoesNotContain(database, x => x.Id == monitor.Id || x.Id == ram.Id || x.Id == faze.Id);
            var currentRam = await store.SearchAsync("Quanto de RAM meu PC tem hoje?", 10, clock.Now, default);
            Assert.Equal(ram.Id, Assert.Single(currentRam).Id);
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP SCHEMA \"" + schema + "\" CASCADE"); await db.DisposeAsync(); }
    }

    [PostgresFact]
    public async Task ConcurrentWorkersRespectConversationOrderAndTerminalFailureReleasesSuccessor()
    {
        var (db, schema) = await NewDbAsync();
        await using var secondDb = Db(schema);
        try
        {
            var clock = new Clock();
            var same = new Conversation();
            var first = same.AddMessage("user", "Meu monitor é 4K 144 Hz.");
            var next = same.AddMessage("user", "Não, falei errado.");
            var other = new Conversation();
            var independent = other.AddMessage("user", "Meu PC tem 32 GB de RAM.");
            db.Conversations.AddRange(same, other);
            db.MemoryExtractionJobs.AddRange(new MemoryExtractionJob(same.Id, first.Id, clock.Now),
                new MemoryExtractionJob(same.Id, next.Id, clock.Now),
                new MemoryExtractionJob(other.Id, independent.Id, clock.Now));
            await db.SaveChangesAsync();
            var worker1 = new MemoryExtractionJobStore(db);
            var worker2 = new MemoryExtractionJobStore(secondDb);
            var claims = await Task.WhenAll(worker1.ClaimAsync(clock.Now, TimeSpan.FromMinutes(2), default),
                worker2.ClaimAsync(clock.Now, TimeSpan.FromMinutes(2), default));
            Assert.Equal(2, claims.Count(x => x is not null));
            Assert.Contains(claims, x => x!.UserMessageId == first.Id);
            Assert.Contains(claims, x => x!.UserMessageId == independent.Id);
            Assert.DoesNotContain(claims, x => x!.UserMessageId == next.Id);
            Assert.Null(await worker1.ClaimAsync(clock.Now, TimeSpan.FromMinutes(2), default));
            var firstClaim = claims.Single(x => x!.UserMessageId == first.Id)!;
            Assert.True(await worker1.FailAsync(firstClaim, "test_terminal_failure", clock.Now, null, default));
            var successor = await worker2.ClaimAsync(clock.Now, TimeSpan.FromMinutes(2), default);
            Assert.Equal(next.Id, successor?.UserMessageId);
            Assert.Equal(MemoryExtractionStatus.Failed,
                (await db.MemoryExtractionJobs.AsNoTracking().SingleAsync(x => x.UserMessageId == first.Id)).Status);
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP SCHEMA \"" + schema + "\" CASCADE"); await db.DisposeAsync(); }
    }

    [PostgresFact]
    public async Task RecentCanonicalContextCorrectsAndTransitionsWhileQdrantIsOffline()
    {
        var (db, schema) = await NewDbAsync();
        try
        {
            var clock = new Clock(); using var metrics = new AegisMetrics();
            var store = new MemoryStore(db);
            var vectors = new FakeVectors { Offline = true };
            var semantic = new MemorySemanticSearch(store, new FakeEmbedding(), vectors,
                new MemorySemanticOptions { EmbeddingDimensions = 3 }, clock, metrics);
            var ingestion = new MemoryAutomaticIngestionService(store, semantic, clock, metrics);
            async Task<(MemoryExtractionSource, MemoryExtractionSource)> Sources(string first, string second)
            {
                var conversation = new Conversation();
                var firstMessage = conversation.AddMessage("user", first);
                var secondMessage = conversation.AddMessage("user", second);
                db.Conversations.Add(conversation); await db.SaveChangesAsync();
                var firstSource = new MemoryExtractionSource(conversation.Id, firstMessage.Id, first,
                    firstMessage.CreatedAt, []);
                var secondSource = new MemoryExtractionSource(conversation.Id, secondMessage.Id, second,
                    secondMessage.CreatedAt, [new("user", first, firstMessage.Id)]);
                return (firstSource, secondSource);
            }
            var (monitorFirst, monitorSecond) = await Sources("Meu monitor é 4K 144 Hz.",
                "Não, eu falei errado. Meu monitor é QHD 180 Hz. Ele nunca foi 4K 144 Hz.");
            await ingestion.ApplyAsync(monitorFirst, new(monitorFirst.Target, [], [], [], []),
                new([Candidate("O monitor de Pedro é 4K 144 Hz.")]), default);
            var monitorOld = await db.MemoryRecords.AsNoTracking().SingleAsync();
            var monitorInput = await ingestion.BuildInputAsync(monitorSecond, default);
            Assert.Equal("recent_conversation", Assert.Single(monitorInput.ExistingMemories).Source);
            Assert.Equal(monitorOld.Id, monitorInput.ExistingMemories[0].Record.Id);
            var monitorSummary = await ingestion.ApplyAsync(monitorSecond, monitorInput,
                new([Candidate("O monitor de Pedro é QHD 180 Hz; a informação de que era 4K 144 Hz estava errada.",
                    "correct", "m1")]), default);
            Assert.Equal(1, monitorSummary.Corrected);
            Assert.Equal(MemoryStatus.Superseded,
                (await db.MemoryRecords.AsNoTracking().SingleAsync(x => x.Id == monitorOld.Id)).Status);
            var correctedMonitor = await db.MemoryRecords.AsNoTracking().SingleAsync(x => x.Content.Contains("QHD"));
            Assert.Equal(MemoryStatus.Active, correctedMonitor.Status);
            Assert.DoesNotContain("4K", correctedMonitor.Content);

            var (ramFirst, ramSecond) = await Sources("Meu PC tinha 16 GB de RAM.",
                "Troquei a memória do PC e agora ele tem 32 GB de RAM.");
            await ingestion.ApplyAsync(ramFirst, new(ramFirst.Target, [], [], [], []),
                new([Candidate("O PC de Pedro tinha 16 GB de RAM.")]), default);
            var ramOld = await db.MemoryRecords.AsNoTracking().SingleAsync(x => x.Content.Contains("16 GB"));
            clock.Now = clock.Now.AddMinutes(1);
            var ramInput = await ingestion.BuildInputAsync(ramSecond, default);
            Assert.Equal(ramOld.Id, ramInput.ExistingMemories[0].Record.Id);
            Assert.Equal("recent_conversation", ramInput.ExistingMemories[0].Source);
            var ramSummary = await ingestion.ApplyAsync(ramSecond, ramInput,
                new([Candidate("O PC de Pedro tem 32 GB de RAM.", "transition", "m1")]), default);
            Assert.Equal(1, ramSummary.Transitioned);
            var ramOldFinal = await db.MemoryRecords.AsNoTracking().SingleAsync(x => x.Id == ramOld.Id);
            var ramNewFinal = await db.MemoryRecords.AsNoTracking().SingleAsync(x => x.Content.Contains("32 GB"));
            Assert.Equal(MemoryStatus.Active, ramOldFinal.Status);
            Assert.Equal(ramSecond.ObservedAt.ToUnixTimeMilliseconds(), ramOldFinal.ValidUntil!.Value.ToUnixTimeMilliseconds());
            Assert.Equal(ramSecond.ObservedAt.ToUnixTimeMilliseconds(), ramNewFinal.ValidFrom!.Value.ToUnixTimeMilliseconds());
            Assert.Equal(MemoryStatus.Active, ramNewFinal.Status);
            var current = await store.SearchAsync("Quanto de RAM meu PC tem hoje?", 10, clock.Now, default);
            Assert.Equal(ramNewFinal.Id, Assert.Single(current).Id);
            var history = await store.SearchHistoricalAsync("Quanto de RAM meu PC tem hoje? e quanto tinha no passado?",
                10, default);
            Assert.Contains(history, x => x.Id == ramOldFinal.Id);
            Assert.Contains(history, x => x.Id == ramNewFinal.Id);
            var monitorCurrent = await store.SearchAsync("Qual é a resolução e a frequência do meu monitor?",
                10, clock.Now, default);
            Assert.Contains(monitorCurrent, x => x.Content.Contains("QHD"));
            Assert.DoesNotContain(monitorCurrent, x => x.Content.Contains("4K"));
            var graphOptions = new MemoryGraphOptions { Enabled = false };
            var hybrid = new MemoryHybridRetriever(store, semantic,
                new MemoryGraphQuery(new FakeGraph(), new MemoryGraphProjectionStore(db, clock),
                    graphOptions, clock, metrics, store), graphOptions,
                new MemorySemanticOptions { EmbeddingDimensions = 3 },
                new MemoryAutoContextOptions(), clock, metrics);
            var correctedHistory = await hybrid.SearchTracedAsync("Meu monitor já foi 4K 144 Hz?",
                10, null, false, default);
            Assert.DoesNotContain(correctedHistory.Memories, x => x.Content.Contains("4K"));
            Assert.Contains(correctedHistory.Corrections!, x => x.IncorrectContent.Contains("4K") &&
                x.ReplacementContent.Contains("QHD"));
            var automaticHistory = await hybrid.BuildAutomaticContextResultAsync(
                "Meu monitor já foi 4K 144 Hz?", default);
            Assert.Contains("estava errada", automaticHistory.Text);
            Assert.Empty(vectors.Candidates); // no semantic projection was ever processed
            vectors.Offline = false;
            var projector = new MemorySemanticProjectionProcessor(new MemorySemanticProjectionStore(db, clock),
                new FakeEmbedding(), vectors, new MemorySemanticOptions { EmbeddingDimensions = 3 }, clock, metrics);
            while (await projector.ProcessNextAsync(default)) { }
            Assert.All(await db.MemoryProjectionJobs.AsNoTracking()
                .Where(x => x.ProjectionTarget == MemoryProjectionTarget.Semantic).ToListAsync(),
                x => Assert.Equal(MemoryProjectionStatus.Completed, x.Status));
            Assert.DoesNotContain(monitorOld.Id, vectors.Projected.Keys);
            Assert.Contains(ramOld.Id, vectors.Projected.Keys);
            Assert.Contains(ramNewFinal.Id, vectors.Projected.Keys);
            Assert.Contains((await db.MemoryRecords.AsNoTracking()
                .SingleAsync(x => x.Content.Contains("QHD"))).Id, vectors.Projected.Keys);
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP SCHEMA \"" + schema + "\" CASCADE"); await db.DisposeAsync(); }
    }

    [PostgresFact]
    public async Task RankedPreferenceDoesNotClosePrimaryFavoriteEvenWhenExtractorRequestsTransition()
    {
        var (db, schema) = await NewDbAsync();
        try
        {
            var clock = new Clock(); using var metrics = new AegisMetrics();
            var store = new MemoryStore(db);
            var semantic = new MemorySemanticSearch(store, new FakeEmbedding(),
                new FakeVectors { Offline = true },
                new MemorySemanticOptions { EmbeddingDimensions = 3 }, clock, metrics);
            var ingestion = new MemoryAutomaticIngestionService(store, semantic, clock, metrics);
            var conversation = new Conversation();
            var first = conversation.AddMessage("user", "Meu time favorito de R6 é a FaZe Clan.");
            var second = conversation.AddMessage("user", "Depois da FaZe, meu time de R6 favorito é a DarkZero.");
            db.Conversations.Add(conversation); await db.SaveChangesAsync();
            var firstSource = new MemoryExtractionSource(conversation.Id, first.Id, first.Content,
                first.CreatedAt, []);
            await ingestion.ApplyAsync(firstSource, new(first.Content, [], [], [], []),
                new([Candidate("O time favorito de Pedro em Rainbow Six Siege é a FaZe Clan.")]), default);
            var primary = await db.MemoryRecords.AsNoTracking().SingleAsync();
            var secondSource = new MemoryExtractionSource(conversation.Id, second.Id, second.Content,
                second.CreatedAt, [new("user", first.Content, first.Id)]);
            var input = await ingestion.BuildInputAsync(secondSource, default);
            var summary = await ingestion.ApplyAsync(secondSource, input,
                new([Candidate("O time favorito de Pedro em R6 é a DarkZero.", "transition", "m1")]), default);
            Assert.Equal(1, summary.Created);
            Assert.Equal(0, summary.Transitioned);
            var retained = await db.MemoryRecords.AsNoTracking().SingleAsync(x => x.Id == primary.Id);
            Assert.Equal(MemoryStatus.Active, retained.Status);
            Assert.Null(retained.ValidUntil);
            Assert.Contains(await db.MemoryRecords.AsNoTracking().ToListAsync(), x =>
                x.Status == MemoryStatus.Active && x.Content.Contains("segundo time favorito") &&
                x.Content.Contains("DarkZero"));
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP SCHEMA \"" + schema + "\" CASCADE"); await db.DisposeAsync(); }
    }

    [PostgresFact]
    public async Task HubFanoutDoesNotRetrieveUnrelatedSupportingMemories()
    {
        var (db, schema) = await NewDbAsync();
        try
        {
            var clock = new Clock(); using var metrics = new AegisMetrics();
            var store = new MemoryStore(db); var service = new MemoryService(store, clock, metrics);
            async Task<MemoryRecord> Remember(string content)
            {
                var source = await SourceAsync(db, content);
                return (await service.RememberAsync(content, null, null,
                    new(source.ConversationId, source.UserMessageId, source.Target), default)).Record;
            }
            var fazeMemory = await Remember("Pedro prefere FaZe Clan no Rainbow Six Siege.");
            var monitorMemory = await Remember("O monitor de Pedro é 4K 144 Hz.");
            var aegisMemory = await Remember("Pedro desenvolve a Aegis.");
            var pedro = await service.CreateEntityAsync("Pedro", "PERSON", default);
            var faze = await service.CreateEntityAsync("FaZe Clan", "TEAM", default);
            var monitor = await service.CreateEntityAsync("Monitor", "DEVICE", default);
            var aegis = await service.CreateEntityAsync("Aegis", "PROJECT", default);
            var prefers = await service.CreateRelationAsync(pedro.Id, "PREFERS", faze.Id, null, null, default);
            var uses = await service.CreateRelationAsync(pedro.Id, "USES", monitor.Id, null, null, default);
            var develops = await service.CreateRelationAsync(pedro.Id, "DEVELOPS", aegis.Id, null, null, default);
            await service.SupportRelationAsync(prefers.Id, fazeMemory.Id, default);
            await service.SupportRelationAsync(uses.Id, monitorMemory.Id, default);
            await service.SupportRelationAsync(develops.Id, aegisMemory.Id, default);
            var vectors = new FakeVectors(); vectors.Candidates.Add(new(fazeMemory.Id, 0.9));
            var graph = new FakeGraph();
            graph.Paths.Add(new([pedro.Id, monitor.Id], [uses.Id]));
            graph.Paths.Add(new([pedro.Id, aegis.Id], [develops.Id]));
            var options = new MemorySemanticOptions { EmbeddingDimensions = 3 };
            var graphOptions = new MemoryGraphOptions();
            var hybrid = new MemoryHybridRetriever(store,
                new MemorySemanticSearch(store, new FakeEmbedding(), vectors, options, clock, metrics),
                new MemoryGraphQuery(graph, new MemoryGraphProjectionStore(db, clock), graphOptions, clock, metrics, store),
                graphOptions, options, new MemoryAutoContextOptions(), clock, metrics);
            var result = await hybrid.SearchTracedAsync("Historicamente, qual time eu dizia gostar mais depois da FaZe?",
                10, null, false, default);
            Assert.DoesNotContain(pedro.Id, result.Trace!.TraversalSeeds);
            Assert.Contains(fazeMemory.Id, result.Memories.Select(x => x.Id));
            Assert.DoesNotContain(monitorMemory.Id, result.Memories.Select(x => x.Id));
            Assert.DoesNotContain(aegisMemory.Id, result.Memories.Select(x => x.Id));
            Assert.DoesNotContain(uses.Id, result.Paths.SelectMany(x => x.Relations).Select(x => x.Id));
            Assert.DoesNotContain(develops.Id, result.Paths.SelectMany(x => x.Relations).Select(x => x.Id));
            var named = await hybrid.SearchTracedAsync(
                "Historicamente, qual time Pedro dizia gostar mais depois da FaZe?",
                10, null, false, default);
            Assert.DoesNotContain(pedro.Id, named.Trace!.TraversalSeeds);
            Assert.DoesNotContain(monitorMemory.Id, named.Memories.Select(x => x.Id));
            Assert.DoesNotContain(aegisMemory.Id, named.Memories.Select(x => x.Id));
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP SCHEMA \"" + schema + "\" CASCADE"); await db.DisposeAsync(); }
    }

    [PostgresFact]
    public async Task HybridSeedsFromEvidenceAndExactMentionsWithoutInvisibleReferences()
    {
        var (db, schema) = await NewDbAsync();
        try
        {
            var clock = new Clock(); using var metrics = new AegisMetrics();
            var store = new MemoryStore(db); var service = new MemoryService(store, clock, metrics);
            var source = await SourceAsync(db, "Meu amigo Sakamoto.");
            var friend = (await service.RememberAsync("Pedro é amigo de Sakamoto.", null, null,
                new(source.ConversationId, source.UserMessageId, source.Target), default)).Record;
            var pedro = await service.CreateEntityAsync("Pedro", "PERSON", default);
            var sakamoto = await service.CreateEntityAsync("Sakamoto", "PERSON", default);
            var bisky = await service.CreateEntityAsync("Bisky", "PERSON", default);
            var friendRelation = await service.CreateRelationAsync(pedro.Id, "FRIEND_OF", sakamoto.Id, null, null, default);
            var datesRelation = await service.CreateRelationAsync(sakamoto.Id, "DATES", bisky.Id, null, null, default);
            await service.SupportRelationAsync(friendRelation.Id, friend.Id, default);
            var vectors = new FakeVectors(); vectors.Candidates.Add(new(friend.Id, 0.9));
            var graph = new FakeGraph();
            graph.Paths.Add(new([pedro.Id, sakamoto.Id, bisky.Id], [friendRelation.Id, datesRelation.Id]));
            graph.Paths.Add(new([sakamoto.Id, bisky.Id], [datesRelation.Id]));
            var semantic = new MemorySemanticSearch(store, new FakeEmbedding(), vectors,
                new MemorySemanticOptions { EmbeddingDimensions = 3 }, clock, metrics);
            var query = new MemoryGraphQuery(graph, new MemoryGraphProjectionStore(db, clock),
                new MemoryGraphOptions(), clock, metrics, store);
            var hybrid = new MemoryHybridRetriever(store, semantic, query, new MemoryGraphOptions(),
                new MemorySemanticOptions { EmbeddingDimensions = 3 }, new MemoryAutoContextOptions(), clock, metrics);
            var observedBefore = await db.ToolContextEntries.CountAsync();
            var indirect = await hybrid.SearchAsync("Quem é a namorada daquele meu amigo?", 5, null, true, default);
            Assert.Contains(indirect.Paths.SelectMany(x => x.Relations), x => x.Id == datesRelation.Id);
            var direct = await hybrid.SearchAsync("Com quem o Sakamoto namora?", 5, null, true, default);
            Assert.Contains(direct.Paths.SelectMany(x => x.Relations), x => x.Id == datesRelation.Id);
            var context = await hybrid.BuildAutomaticContextAsync("Quem é a namorada daquele meu amigo?", default);
            Assert.Contains("Bisky", context);
            Assert.Equal(observedBefore, await db.ToolContextEntries.CountAsync());
            vectors.Offline = true;
            Assert.Contains((await hybrid.SearchAsync("Com quem o Sakamoto namora?", 5, null, true, default)).Paths
                .SelectMany(x => x.Relations), x => x.Id == datesRelation.Id);
            graph.Offline = true;
            Assert.NotNull(await hybrid.SearchAsync("Pedro é amigo de Sakamoto.", 5, null, true, default));
            await service.ForgetRelationAsync(datesRelation.Id, default);
            graph.Offline = false;
            Assert.DoesNotContain((await hybrid.SearchAsync("Com quem o Sakamoto namora?", 5, null, true, default)).Paths
                .SelectMany(x => x.Relations), x => x.Id == datesRelation.Id);
            var slow = new MemoryHybridRetriever(store, new MemorySemanticSearch(store, new SlowEmbedding(), vectors,
                new MemorySemanticOptions { EmbeddingDimensions = 3 }, clock, metrics), query, new MemoryGraphOptions(),
                new MemorySemanticOptions { EmbeddingDimensions = 3 }, new MemoryAutoContextOptions { TimeoutMs = 100 },
                clock, metrics);
            Assert.Null(await slow.BuildAutomaticContextAsync("Uma pergunta demorada", default));
            await service.ForgetAsync(friend.Id, new ToolExecutionContext(source.ConversationId, source.UserMessageId, source.Target), default);
            Assert.Equal(MemoryStatus.Forgotten, (await db.MemoryRelations.AsNoTracking()
                .SingleAsync(x => x.Id == friendRelation.Id)).Status);
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP SCHEMA \"" + schema + "\" CASCADE"); await db.DisposeAsync(); }
    }

    [PostgresFact]
    public async Task RelationTransitionPreservesHistoryAndCorrectionRemovesFalseHistory()
    {
        var (db, schema) = await NewDbAsync();
        try
        {
            var clock = new Clock(); using var metrics = new AegisMetrics();
            var ingestion = Ingestion(db, clock, metrics);
            var first = await SourceAsync(db, "Sakamoto namora Carol.");
            await ingestion.ApplyAsync(first, new(first.Target, [], [], [], []), new([
                Candidate("Sakamoto namora Carol.", entities: [
                    new("s", "Sakamoto", "Sakamoto", "PERSON", []),
                    new("c", "Carol", "Carol", "PERSON", [])], relations: [
                        new("create", null, "s", "DATES", "c", null, null, null)])]), default);
            var oldMemory = await db.MemoryRecords.SingleAsync();
            var oldRelation = await db.MemoryRelations.SingleAsync();
            var at = clock.Now.AddMinutes(1);
            clock.Now = at;
            var change = await SourceAsync(db, "Sakamoto terminou com Carol e agora namora Bisky.");
            var input = new MemoryExtractionInput(change.Target, [], [new("m1", oldMemory)],
                [new("r1", oldRelation, "Sakamoto", "Carol")], []);
            var candidate = Candidate("Sakamoto namora Bisky.", "transition", "m1", at,
                entities: [new("s", "Sakamoto", "Sakamoto", "PERSON", []),
                    new("b", "Bisky", "Bisky", "PERSON", [])],
                relations: [new("close", "r1", null, null, null, null, null, at),
                    new("create", null, "s", "DATES", "b", at, null, null)]);
            await ingestion.ApplyAsync(change, input, new([candidate]), default);
            await ingestion.ApplyAsync(change, input, new([candidate]), default);
            var relations = await db.MemoryRelations.AsNoTracking().OrderBy(x => x.CreatedAt).ToListAsync();
            Assert.Equal(2, relations.Count);
            var carol = relations.Single(x => x.Id == oldRelation.Id);
            var bisky = relations.Single(x => x.Id != oldRelation.Id);
            Assert.Equal(MemoryStatus.Active, carol.Status);
            Assert.Equal(at, carol.ValidUntil);
            Assert.Equal(at, bisky.ValidFrom);
            var store = new MemoryStore(db);
            var sakamoto = await db.MemoryEntities.SingleAsync(x => x.CanonicalName == "Sakamoto");
            Assert.Equal(oldRelation.Id, Assert.Single(await store.FindRelationsByEntitiesAsync([sakamoto.Id], at.AddTicks(-1), 10, default)).Relation.Id);
            Assert.Equal(bisky.Id, Assert.Single(await store.FindRelationsByEntitiesAsync([sakamoto.Id], at, 10, default)).Relation.Id);
            var correction = await SourceAsync(db, "Sakamoto nunca namorou Carol; eu tinha falado errado.");
            await ingestion.ApplyAsync(correction, new(correction.Target, [], [new("m1", oldMemory)], [], []),
                new([Candidate("Sakamoto nunca namorou Carol.", "correct", "m1")]), default);
            Assert.Equal(MemoryStatus.Superseded, (await db.MemoryRecords.AsNoTracking().SingleAsync(x => x.Id == oldMemory.Id)).Status);
            Assert.Equal(MemoryStatus.Forgotten, (await db.MemoryRelations.AsNoTracking().SingleAsync(x => x.Id == oldRelation.Id)).Status);
            Assert.Empty(await store.FindRelationsByEntitiesAsync([sakamoto.Id], at.AddTicks(-1), 10, default));
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP SCHEMA \"" + schema + "\" CASCADE"); await db.DisposeAsync(); }
    }
}
