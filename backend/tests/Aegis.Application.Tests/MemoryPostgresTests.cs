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

    private static AegisDbContext CreateDb(string schema) => new(new DbContextOptionsBuilder<AegisDbContext>()
        .UseNpgsql(Environment.GetEnvironmentVariable("AEGIS_MEMORY_TEST_DATABASE") + ";Search Path=" + schema,
            options => options.MigrationsHistoryTable("__EFMigrationsHistory", schema)).Options);

    private sealed class FixedClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
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

            var forgotten = await service.ForgetAsync(replacement.Id, conversation.Id, default);
            var timestamp = forgotten.ForgottenAt;
            await service.ForgetAsync(replacement.Id, conversation.Id, default);
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
            await Assert.ThrowsAsync<MemoryException>(() => service.RememberAsync(temporary.Record.Content, null, null, thirdContext, default));
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
            Assert.Equal(relation.Id, (await service.CreateRelationAsync(sakamoto.Id, "DATES", bisky.Id, null, null, default)).Id);
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
