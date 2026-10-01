using Aegis.Application.Memory;
using Aegis.Application.Observability;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Memory;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class MemorySemanticTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class FakeEmbedding : IMemoryEmbeddingClient
    {
        public bool Fail { get; set; }
        public Task<float[]> EmbedAsync(string text, CancellationToken ct)
        {
            if (Fail) throw new MemorySemanticException("embedding_timeout");
            if (text.Contains("backend", StringComparison.OrdinalIgnoreCase) || text.Contains("lado do desenvolvimento", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<float[]>([1, 0, 0]);
            if (text.Contains("MCP", StringComparison.OrdinalIgnoreCase) || text.Contains("protocolo de integração", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<float[]>([0, 1, 0]);
            if (text.Contains("arquitetura do banco", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<float[]>([0, 0, -1]);
            return Task.FromResult<float[]>([0, 0, 1]);
        }
    }
    private sealed class FakeVectors : IMemoryVectorStore
    {
        public readonly Dictionary<Guid, (MemoryVectorPoint Point, float[] Vector)> Points = [];
        public bool Fail { get; set; }
        public Task<bool> EnsureCollectionAsync(CancellationToken ct) => Task.FromResult(false);
        public Task<MemoryVectorPoint?> GetPointAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(Points.TryGetValue(id, out var item) ? item.Point : null);
        public Task UpsertAsync(MemoryVectorPoint point, float[] vector, CancellationToken ct)
        { if (Fail) throw new MemorySemanticException("qdrant_unavailable"); Points[point.MemoryId] = (point, vector); return Task.CompletedTask; }
        public Task DeleteAsync(Guid id, CancellationToken ct) { Points.Remove(id); return Task.CompletedTask; }
        public Task<IReadOnlyList<MemoryVectorCandidate>> SearchAsync(float[] vector, int limit, double threshold, CancellationToken ct)
        {
            if (Fail) throw new MemorySemanticException("qdrant_unavailable");
            return Task.FromResult<IReadOnlyList<MemoryVectorCandidate>>(Points.Select(x => new MemoryVectorCandidate(x.Key,
                x.Value.Vector.Zip(vector, (a, b) => a * b).Sum())).OrderByDescending(x => x.Score).Take(limit).ToArray());
        }
        public void Put(MemoryRecord record, float[] vector) => Points[record.Id] =
            (new MemoryVectorPoint(record.Id, record.Revision, record.ContentHash, "fake", 3), vector);
    }

    private sealed class StubHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    [Fact]
    public async Task EmbeddingClientSendsOnlyTextAndRejectsInvalidVectors()
    {
        using var metrics = new AegisMetrics();
        var options = new MemorySemanticOptions { EmbeddingApiKey = "test-only-key", EmbeddingDimensions = 3 };
        using var http = new HttpClient(new StubHttp(request =>
        {
            Assert.Equal("/v1/embeddings", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-only-key", request.Headers.Authorization.Parameter);
            using var payload = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("Pedro prefere backend.", payload.RootElement.GetProperty("input").GetString());
            Assert.Equal("text-embedding-3-large", payload.RootElement.GetProperty("model").GetString());
            Assert.Equal(3, payload.RootElement.GetProperty("dimensions").GetInt32());
            Assert.Equal(3, payload.RootElement.EnumerateObject().Count());
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"data\":[{\"embedding\":[1,0,0]}],\"usage\":{\"prompt_tokens\":5}}") };
        })) { BaseAddress = new Uri("https://example.test/") };
        var client = new OpenAiMemoryEmbeddingClient(http, options, metrics);
        Assert.Equal(new float[] { 1, 0, 0 }, await client.EmbedAsync("Pedro prefere backend.", default));
        using var badHttp = new HttpClient(new StubHttp(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{\"data\":[{\"embedding\":[1,0]}]}") })) { BaseAddress = new Uri("https://example.test/") };
        Assert.Equal("embedding_invalid_vector", (await Assert.ThrowsAsync<MemorySemanticException>(() =>
            new OpenAiMemoryEmbeddingClient(badHttp, options, metrics).EmbedAsync("x", default))).Code);
        using var limitedHttp = new HttpClient(new StubHttp(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        { Content = new StringContent("sensitive-response-body") })) { BaseAddress = new Uri("https://example.test/") };
        Assert.Equal("embedding_rate_limited", (await Assert.ThrowsAsync<MemorySemanticException>(() =>
            new OpenAiMemoryEmbeddingClient(limitedHttp, options, metrics).EmbedAsync("x", default))).Code);
    }

    [Fact]
    public async Task SemanticSynonymsRankingThresholdCanonicalValidationAndFallback()
    {
        await using var db = new AegisDbContext(new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var backend = new MemoryRecord("Pedro prefere trabalhar no backend.", null, null, Now);
        var secondBackend = new MemoryRecord("Sakamoto prefere APIs de servidor.", null, null, Now);
        var mcp = new MemoryRecord("Aegis não utiliza MCP porque sua infraestrutura é fechada.", null, null, Now);
        var beer = new MemoryRecord("Pedro gosta de Heineken.", null, null, Now);
        var forgotten = new MemoryRecord("Protocolo obsoleto de integração.", null, null, Now);
        forgotten.Forget(Now);
        var superseded = new MemoryRecord("Antiga decisão sobre protocolo de integração.", null, null, Now);
        superseded.Supersede(mcp.Id, Now);
        var future = new MemoryRecord("Futura decisão de integração.", Now.AddDays(1), null, Now);
        var expired = new MemoryRecord("Decisão de integração expirada.", null, Now.AddHours(-1), Now);
        db.MemoryRecords.AddRange(backend, secondBackend, mcp, beer, forgotten, superseded, future, expired);
        await db.SaveChangesAsync();
        var vector = new FakeVectors();
        vector.Put(backend, [1, 0, 0]); vector.Put(secondBackend, [0.8f, 0.6f, 0]);
        vector.Put(forgotten, [0, 1, 0]); vector.Put(superseded, [0, 1, 0]); vector.Put(future, [0, 1, 0]); vector.Put(expired, [0, 1, 0]);
        vector.Put(mcp, [0, 1, 0]); vector.Put(beer, [0, 0, 1]);
        using var metrics = new AegisMetrics();
        var embedding = new FakeEmbedding();
        var search = new MemorySemanticSearch(new MemoryStore(db), embedding, vector,
            new MemorySemanticOptions { EmbeddingDimensions = 3, SearchScoreThreshold = 0.5 }, new FixedClock(), metrics);
        var preference = await search.SearchAsync("qual lado do desenvolvimento eu prefiro?", 10, default);
        Assert.Equal("semantic", preference.Mode);
        Assert.Equal(new[] { backend.Id, secondBackend.Id }, preference.Results.Select(x => x.Id));
        var decision = await search.SearchAsync("por que descartei aquele protocolo de integração?", 1, default);
        Assert.Equal(mcp.Id, Assert.Single(decision.Results).Id);
        Assert.Empty((await search.SearchAsync("arquitetura do banco da Aegis", 10, default)).Results);
        vector.Fail = true;
        var fallback = await search.SearchAsync("backend", 10, default);
        Assert.Equal("canonical_text_fallback", fallback.Mode);
        Assert.Equal(backend.Id, Assert.Single(fallback.Results).Id);
        vector.Fail = false; embedding.Fail = true;
        Assert.Equal("canonical_text_fallback", (await search.SearchAsync("backend", 10, default)).Mode);
        Assert.Throws<MemorySemanticException>(() => MemoryVectorValidation.Validate([float.NaN, 0, 0], 3));
        Assert.Throws<MemorySemanticException>(() => MemoryVectorValidation.Validate([1, 0], 3));
    }
}
