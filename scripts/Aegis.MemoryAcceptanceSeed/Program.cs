using System.Text.Json;
using Aegis.Application.Memory;
using Aegis.Application.Observability;
using Aegis.Application.Tools;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Memory;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var connection = Environment.GetEnvironmentVariable("AEGIS_MEMORY_ACCEPTANCE_DATABASE")
    ?? throw new InvalidOperationException("AEGIS_MEMORY_ACCEPTANCE_DATABASE is required.");
var parsed = new NpgsqlConnectionStringBuilder(connection);
if (parsed.Database?.StartsWith("aegis_memory_v060_", StringComparison.Ordinal) != true)
    throw new InvalidOperationException("Seed requires a disposable aegis_memory_v060_* database.");
var seed = "O time favorito de Rainbow Six do Pedro é a FaZe Clan.";
await using var db = new AegisDbContext(new DbContextOptionsBuilder<AegisDbContext>()
    .UseNpgsql(connection).Options);
await db.Database.MigrateAsync();
var conversation = new Conversation();
var message = conversation.AddMessage("user", seed);
db.Conversations.Add(conversation);
await db.SaveChangesAsync();
using var metrics = new AegisMetrics();
var memory = new MemoryService(new MemoryStore(db), TimeProvider.System, metrics);
var result = await memory.RememberAsync(seed, null, null,
    new ToolExecutionContext(conversation.Id, message.Id, seed), default);
Console.WriteLine(JsonSerializer.Serialize(new { conversationId = conversation.Id,
    userMessageId = message.Id, memoryId = result.Record.Id }));
