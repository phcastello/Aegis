using Aegis.Application.Memory;
using Aegis.Application.Observability;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Memory;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
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

    private sealed class FakeVectors : IMemoryVectorStore
    {
        public List<MemoryVectorCandidate> Candidates { get; } = [];
        public bool Offline { get; set; }
        public Task<bool> EnsureCollectionAsync(CancellationToken ct) => Task.FromResult(false);
        public Task<MemoryVectorPoint?> GetPointAsync(Guid id, CancellationToken ct) => Task.FromResult<MemoryVectorPoint?>(null);
        public Task UpsertAsync(MemoryVectorPoint point, float[] vector, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken ct) => Task.CompletedTask;
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
            await service.ForgetAsync(friend.Id, source.ConversationId, default);
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
