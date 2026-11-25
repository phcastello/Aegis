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

public sealed class MemoryAsOfToolContractTests
{
    [Fact]
    public async Task SeparateCurrentAndHistoricalQueriesPreserveCanonicalValidityWithSameCompoundQuestion()
    {
        using var db = new AegisDbContext(new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        using var metrics = new AegisMetrics();
        var clock = new Clock();
        var boundary = DateTimeOffset.Parse("2025-01-01T00:00:00Z");
        var old = new MemoryRecord("Talin sintético mora em Recife.", DateTimeOffset.Parse("2024-01-01T00:00:00Z"), boundary, clock.GetUtcNow());
        var current = new MemoryRecord("Talin sintético mora em Curitiba.", boundary, null, clock.GetUtcNow());
        var forgotten = new MemoryRecord("Talin sintético mora em Manaus.", null, null, clock.GetUtcNow());
        forgotten.Forget(clock.GetUtcNow());
        db.MemoryRecords.AddRange(old, current, forgotten);
        await db.SaveChangesAsync();
        var tool = new MemorySearchTool(new MemoryService(new MemoryStore(db), clock, metrics));
        var context = new ToolExecutionContext(Guid.NewGuid(), Guid.NewGuid(), "Em qual cidade Talin sintético mora hoje e onde morava em junho de 2024?");
        var present = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { query = "Talin sintético cidade atual" }), context);
        var past = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { query = "Talin sintético cidade junho 2024", asOf = "2024-06-15T12:00:00-03:00" }), context);
        Assert.True(present.Success);
        Assert.True(past.Success);
        Assert.Contains("Curitiba", present.Content);
        Assert.DoesNotContain("Recife", present.Content);
        Assert.Contains("Recife", past.Content);
        Assert.DoesNotContain("Curitiba", past.Content);
        Assert.DoesNotContain("Manaus", present.Content + past.Content);
        Assert.Contains("2025-01-01", past.Content);
    }

    [Fact]
    public async Task InvalidHistoricalInstantCannotSilentlyFallBackToCurrentKnowledge()
    {
        var tool = new MemorySearchTool(null!);
        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { query = "Talin sintético", asOf = "2024-06-15 sem fuso" }),
            new(Guid.NewGuid(), Guid.NewGuid(), "Onde morava antes?"));
        Assert.False(result.Success);
        Assert.Equal("invalid_tool_arguments", result.ErrorCode);
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-10-31T12:00:00Z");
    }
}
