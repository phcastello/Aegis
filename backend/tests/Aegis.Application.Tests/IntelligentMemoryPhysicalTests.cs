using Aegis.Application.Memory;
using Aegis.Application.Observability;
using Aegis.Application.Prompts;
using Aegis.Application.Runtime;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Memory;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.Driver;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class IntelligentMemoryPhysicalTests
{
    public sealed class PhysicalFactAttribute : FactAttribute
    {
        public PhysicalFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEGIS_MEMORY_TEST_DATABASE")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEGIS_MEMORY_TEST_QDRANT_URL")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEGIS_MEMORY_TEST_NEO4J_URI")) ||
                Environment.GetEnvironmentVariable("AEGIS_MEMORY_TEST_NEO4J_DISPOSABLE") != "YES_DELETE_AEGIS_PROJECTION")
                Skip = "Requires disposable PostgreSQL, Qdrant and Neo4j; set AEGIS_MEMORY_TEST_NEO4J_DISPOSABLE.";
        }
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow;
    }

    private sealed class Embedding : IMemoryEmbeddingClient
    {
        public Task<float[]> EmbedAsync(string text, CancellationToken ct) => Task.FromResult<float[]>([1, 0, 0]);
    }

    private sealed class Extractor : IMemoryExtractionClient
    {
        public int Calls { get; private set; }
        public Task<MemoryExtractionOutput> ExtractAsync(MemoryExtractionInput input, CancellationToken ct)
        {
            Calls++;
            Assert.Contains("Sakamoto", input.Target);
            var pedro = new MemoryExtractionEntity("p", "meu", "Pedro", "PERSON", []);
            var sakamoto = new MemoryExtractionEntity("s", "Sakamoto", "Sakamoto", "PERSON", []);
            var bisky = new MemoryExtractionEntity("b", "Bisky", "Bisky", "PERSON", []);
            return Task.FromResult(new MemoryExtractionOutput([
                new("Pedro é amigo de Sakamoto.", "create", null, null, null, null,
                    [pedro, sakamoto], [new("create", null, "p", "FRIEND_OF", "s", null, null, null)]),
                new("Sakamoto namora Bisky.", "create", null, null, null, null,
                    [sakamoto, bisky], [new("create", null, "s", "DATES", "b", null, null, null)])
            ]));
        }
    }

    private sealed class Runtime : IRuntimeContextProvider
    {
        public Task<string> GetRuntimeContextAsync(CancellationToken cancellationToken = default) => Task.FromResult("Hora atual.");
    }

    [PhysicalFact]
    public async Task DurableExtractionProjectsBothStoresAndBuildsNewConversationContext()
    {
        var schema = "e2e_" + Guid.NewGuid().ToString("N");
        var collection = "aegis_e2e_" + Guid.NewGuid().ToString("N");
        var connection = Environment.GetEnvironmentVariable("AEGIS_MEMORY_TEST_DATABASE")!;
        var dbOptions = new DbContextOptionsBuilder<AegisDbContext>().UseNpgsql(connection + ";Search Path=" + schema,
            x => x.MigrationsHistoryTable("__EFMigrationsHistory", schema)).Options;
        await using var db = new AegisDbContext(dbOptions);
        await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA \"" + schema + "\"");
        var semanticOptions = new MemorySemanticOptions { EmbeddingDimensions = 3, EmbeddingModel = "fake",
            CollectionName = collection };
        using var qdrantHttp = new HttpClient { BaseAddress = new Uri(Environment.GetEnvironmentVariable("AEGIS_MEMORY_TEST_QDRANT_URL")!.TrimEnd('/') + "/") };
        var vectors = new QdrantMemoryVectorStore(qdrantHttp, semanticOptions);
        var graphOptions = new MemoryGraphOptions
        {
            Neo4jUri = Environment.GetEnvironmentVariable("AEGIS_MEMORY_TEST_NEO4J_URI")!,
            Neo4jUsername = Environment.GetEnvironmentVariable("AEGIS_MEMORY_TEST_NEO4J_USERNAME") ?? "neo4j",
            Neo4jPassword = Environment.GetEnvironmentVariable("AEGIS_MEMORY_TEST_NEO4J_PASSWORD")!,
            Neo4jDatabase = Environment.GetEnvironmentVariable("AEGIS_MEMORY_TEST_NEO4J_DATABASE") ?? "neo4j"
        };
        using var driver = GraphDatabase.Driver(graphOptions.Neo4jUri,
            AuthTokens.Basic(graphOptions.Neo4jUsername, graphOptions.Neo4jPassword));
        var graph = new Neo4jMemoryGraphStore(driver, graphOptions);
        using var metrics = new AegisMetrics();
        var clock = new Clock(); var embedding = new Embedding(); var extractor = new Extractor();
        try
        {
            await db.Database.MigrateAsync();
            await vectors.EnsureCollectionAsync(default);
            await graph.EnsureSchemaAsync(default);
            await graph.DeleteManagedProjectionAsync(default);
            var conversation = new Conversation();
            var user = conversation.AddMessage("user", "O Sakamoto é meu amigo e namora Bisky.");
            db.Conversations.Add(conversation);
            db.MemoryExtractionJobs.Add(new MemoryExtractionJob(conversation.Id, user.Id, clock.GetUtcNow()));
            await db.SaveChangesAsync(); // user message and durable intention commit together
            Assert.Equal(MemoryExtractionStatus.Pending, (await db.MemoryExtractionJobs.AsNoTracking().SingleAsync()).Status);

            using var services = new ServiceCollection()
                .AddLogging()
                .AddSingleton<TimeProvider>(clock)
                .AddSingleton(metrics)
                .AddSingleton(semanticOptions)
                .AddSingleton<IMemoryEmbeddingClient>(embedding)
                .AddSingleton<IMemoryVectorStore>(vectors)
                .AddSingleton<IMemoryExtractionClient>(extractor)
                .AddDbContext<AegisDbContext>(x => x.UseNpgsql(connection + ";Search Path=" + schema,
                    o => o.MigrationsHistoryTable("__EFMigrationsHistory", schema)))
                .AddScoped<IMemoryStore, MemoryStore>()
                .AddScoped<IMemoryExtractionJobStore, MemoryExtractionJobStore>()
                .AddScoped<MemorySemanticSearch>()
                .AddScoped<MemoryAutomaticIngestionService>()
                .BuildServiceProvider();
            var worker = new MemoryExtractionWorker(services.GetRequiredService<IServiceScopeFactory>(),
                new MemoryAutomaticOptions(), clock, metrics, NullLogger<MemoryExtractionWorker>.Instance);
            Assert.True(await worker.ProcessNextAsync(default));
            Assert.Equal(1, extractor.Calls);
            Assert.Equal(MemoryExtractionStatus.Completed,
                (await db.MemoryExtractionJobs.AsNoTracking().SingleAsync()).Status);
            Assert.Equal(2, await db.MemoryRecords.CountAsync());
            Assert.Equal(2, await db.MemoryRelations.CountAsync());
            Assert.Equal(2, await db.MemoryRelationEvidences.CountAsync());

            var semanticJobs = new MemorySemanticProjectionStore(db, clock);
            var semanticProcessor = new MemorySemanticProjectionProcessor(semanticJobs, embedding, vectors,
                semanticOptions, clock, metrics);
            while (await semanticProcessor.ProcessNextAsync(default)) { }
            var graphJobs = new MemoryGraphProjectionStore(db, clock);
            var graphProcessor = new MemoryGraphProjectionProcessor(graphJobs, graph, clock, metrics);
            while (await graphProcessor.ProcessNextAsync(default)) { }
            Assert.Equal(2, await db.MemoryProjectionJobs.CountAsync(x => x.ProjectionTarget == MemoryProjectionTarget.Semantic &&
                x.Status == MemoryProjectionStatus.Completed));
            Assert.Equal(5, await db.MemoryProjectionJobs.CountAsync(x => x.ProjectionTarget == MemoryProjectionTarget.Graph &&
                x.Status == MemoryProjectionStatus.Completed));
            var records = await db.MemoryRecords.AsNoTracking().ToListAsync();
            Assert.All(records, record => Assert.NotNull(vectors.GetPointAsync(record.Id, default).GetAwaiter().GetResult()));

            await using var retrievalDb = new AegisDbContext(dbOptions);
            var store = new MemoryStore(retrievalDb);
            var semantic = new MemorySemanticSearch(store, embedding, vectors, semanticOptions, clock, metrics);
            var graphQuery = new MemoryGraphQuery(graph, new MemoryGraphProjectionStore(retrievalDb, clock),
                graphOptions, clock, metrics, store);
            var hybrid = new MemoryHybridRetriever(store, semantic, graphQuery, graphOptions, semanticOptions,
                new MemoryAutoContextOptions(), clock, metrics);
            var question = "Como chama a namorada daquele meu amigo Sakamoto?";
            var result = await hybrid.SearchAsync(question, 5, null, true, default);
            Assert.Contains(result.Paths.SelectMany(x => x.Relations), x => x.Predicate == "DATES");
            var context = await hybrid.BuildAutomaticContextAsync(question, default);
            Assert.Contains("Bisky", context);
            var prompt = await new PromptBuilder(new Runtime()).BuildPromptAsync([], question,
                cancellationToken: default, automaticMemoryContext: context);
            Assert.Contains(prompt.InputItems, item => item.GetProperty("role").GetString() == "user" &&
                item.GetRawText().Contains("Bisky", StringComparison.Ordinal));
            Assert.DoesNotContain("Bisky", string.Join("\n", prompt.AuditInputItems!.Select(x => x.GetRawText())));
            Assert.Empty(await retrievalDb.ToolContextEntries.ToListAsync());

            // Live drift repair reuses current jobs; no restart or canonical rewrite is needed.
            var point = records[0];
            await vectors.DeleteAsync(point.Id, default);
            var relation = await db.MemoryRelations.AsNoTracking().FirstAsync();
            await graph.DeleteRelationAsync(relation.Id, default);
            await semanticJobs.RequeueCurrentStateAsync(clock.GetUtcNow(), default);
            await graphJobs.RequeueCurrentStateAsync(clock.GetUtcNow(), default);
            while (await semanticProcessor.ProcessNextAsync(default)) { }
            while (await graphProcessor.ProcessNextAsync(default)) { }
            Assert.NotNull(await vectors.GetPointAsync(point.Id, default));
            Assert.NotNull(await graph.GetRelationProjectionAsync(relation.Id, default));
        }
        finally
        {
            await graph.DeleteManagedProjectionAsync(default);
            await qdrantHttp.DeleteAsync("collections/" + collection);
            await db.Database.ExecuteSqlRawAsync("DROP SCHEMA \"" + schema + "\" CASCADE");
        }
    }

    [PhysicalFact]
    public async Task CorrectionAndTransitionCommitBeforePhysicalQdrantProjection()
    {
        var schema = "lag_" + Guid.NewGuid().ToString("N");
        var collection = "aegis_lag_" + Guid.NewGuid().ToString("N");
        var connection = Environment.GetEnvironmentVariable("AEGIS_MEMORY_TEST_DATABASE")!;
        var dbOptions = new DbContextOptionsBuilder<AegisDbContext>().UseNpgsql(connection + ";Search Path=" + schema,
            x => x.MigrationsHistoryTable("__EFMigrationsHistory", schema)).Options;
        await using var db = new AegisDbContext(dbOptions);
        await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA \"" + schema + "\"");
        var options = new MemorySemanticOptions { EmbeddingDimensions = 3, EmbeddingModel = "fake",
            CollectionName = collection };
        using var http = new HttpClient { BaseAddress = new Uri(
            Environment.GetEnvironmentVariable("AEGIS_MEMORY_TEST_QDRANT_URL")!.TrimEnd('/') + "/") };
        var vectors = new QdrantMemoryVectorStore(http, options);
        using var metrics = new AegisMetrics();
        var clock = new Clock(); var embedding = new Embedding();
        try
        {
            await db.Database.MigrateAsync();
            await vectors.EnsureCollectionAsync(default);
            var store = new MemoryStore(db);
            var semantic = new MemorySemanticSearch(store, embedding, vectors, options, clock, metrics);
            var ingestion = new MemoryAutomaticIngestionService(store, semantic, clock, metrics);

            async Task<(MemoryExtractionSource First, MemoryExtractionSource Second)> Sources(string first, string second)
            {
                var conversation = new Conversation();
                var firstMessage = conversation.AddMessage("user", first);
                var secondMessage = conversation.AddMessage("user", second);
                db.Conversations.Add(conversation); await db.SaveChangesAsync();
                return (new(conversation.Id, firstMessage.Id, first, firstMessage.CreatedAt, []),
                    new(conversation.Id, secondMessage.Id, second, secondMessage.CreatedAt,
                        [new("user", first, firstMessage.Id)]));
            }
            static MemoryExtractionCandidate Candidate(string content, string action = "create", string? old = null) =>
                new(content, action, old, null, null, null, [], []);

            var monitor = await Sources("Meu monitor é 4K 144 Hz.",
                "Não, eu falei errado. Meu monitor é QHD 180 Hz. Ele nunca foi 4K 144 Hz.");
            await ingestion.ApplyAsync(monitor.First, new(monitor.First.Target, [], [], [], []),
                new([Candidate("O monitor de Pedro é 4K 144 Hz.")]), default);
            var falseMonitor = await db.MemoryRecords.AsNoTracking().SingleAsync();
            Assert.Null(await vectors.GetPointAsync(falseMonitor.Id, default));
            var monitorInput = await ingestion.BuildInputAsync(monitor.Second, default);
            Assert.Equal(falseMonitor.Id, Assert.Single(monitorInput.ExistingMemories).Record.Id);
            Assert.Equal("recent_conversation", monitorInput.ExistingMemories[0].Source);
            await ingestion.ApplyAsync(monitor.Second, monitorInput,
                new([Candidate("O monitor de Pedro é QHD 180 Hz.", "correct", "m1")]), default);

            var ram = await Sources("Meu PC tinha 16 GB de RAM.",
                "Troquei a memória do PC e agora ele tem 32 GB de RAM.");
            await ingestion.ApplyAsync(ram.First, new(ram.First.Target, [], [], [], []),
                new([Candidate("O PC de Pedro tinha 16 GB de RAM.")]), default);
            var priorRam = await db.MemoryRecords.AsNoTracking().SingleAsync(x => x.Content.Contains("16 GB"));
            Assert.Null(await vectors.GetPointAsync(priorRam.Id, default));
            var ramInput = await ingestion.BuildInputAsync(ram.Second, default);
            Assert.Equal(priorRam.Id, Assert.Single(ramInput.ExistingMemories).Record.Id);
            Assert.Equal("recent_conversation", ramInput.ExistingMemories[0].Source);
            await ingestion.ApplyAsync(ram.Second, ramInput,
                new([Candidate("O PC de Pedro tem 32 GB de RAM.", "transition", "m1")]), default);

            var oldMonitor = await db.MemoryRecords.AsNoTracking().SingleAsync(x => x.Id == falseMonitor.Id);
            var newMonitor = await db.MemoryRecords.AsNoTracking().SingleAsync(x => x.Content.Contains("QHD"));
            var oldRam = await db.MemoryRecords.AsNoTracking().SingleAsync(x => x.Id == priorRam.Id);
            var newRam = await db.MemoryRecords.AsNoTracking().SingleAsync(x => x.Content.Contains("32 GB"));
            Assert.Equal(MemoryStatus.Superseded, oldMonitor.Status);
            Assert.Equal(newMonitor.Id, oldMonitor.SupersededById);
            Assert.Equal(MemoryStatus.Active, newMonitor.Status);
            Assert.Equal(MemoryStatus.Active, oldRam.Status);
            Assert.Equal(oldRam.ValidUntil, newRam.ValidFrom);
            Assert.NotNull(oldRam.ValidUntil);
            Assert.Equal(MemoryStatus.Active, newRam.Status);
            Assert.Null(await vectors.GetPointAsync(newMonitor.Id, default));
            Assert.Null(await vectors.GetPointAsync(newRam.Id, default));

            var processor = new MemorySemanticProjectionProcessor(new MemorySemanticProjectionStore(db, clock),
                embedding, vectors, options, clock, metrics);
            while (await processor.ProcessNextAsync(default)) { }
            Assert.All(await db.MemoryProjectionJobs.AsNoTracking().Where(x =>
                x.ProjectionTarget == MemoryProjectionTarget.Semantic).ToListAsync(),
                x => Assert.Equal(MemoryProjectionStatus.Completed, x.Status));
            Assert.Null(await vectors.GetPointAsync(falseMonitor.Id, default));
            Assert.NotNull(await vectors.GetPointAsync(newMonitor.Id, default));
            Assert.NotNull(await vectors.GetPointAsync(priorRam.Id, default));
            Assert.NotNull(await vectors.GetPointAsync(newRam.Id, default));
        }
        finally
        {
            await http.DeleteAsync("collections/" + collection);
            await db.Database.ExecuteSqlRawAsync("DROP SCHEMA \"" + schema + "\" CASCADE");
        }
    }
}
