using System.Net;
using System.Text.Json;
using Aegis.Application.Memory;
using Aegis.Application.Observability;
using Aegis.Infrastructure.Memory;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class MemoryEmbeddingCacheTests
{
    private sealed class Clock : TimeProvider
    {
        public long Timestamp { get; set; }
        public override long GetTimestamp() => Timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan elapsed) => Timestamp += elapsed.Ticks;
    }

    private sealed class Handler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public bool Cancel { get; set; }
        public TaskCompletionSource<bool>? Block { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (Block is { } blocker) await blocker.Task.WaitAsync(ct);
            if (Cancel) throw new OperationCanceledException(ct);
            if (Fail) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var dimensions = body.RootElement.GetProperty("dimensions").GetInt32();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    data = new[] { new { embedding = Enumerable.Range(0, dimensions).Select(i => i == 0 ? 1f : 0f).ToArray() } },
                    usage = new { prompt_tokens = 2 }
                }))
            };
        }
    }

    [Fact]
    public async Task ExactVectorsExpireAreBoundedAndCannotBeMutatedByCallers()
    {
        var clock = new Clock();
        var cache = new MemoryEmbeddingCache(clock);
        var handler = new Handler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
        using var metrics = new AegisMetrics();
        var options = new MemorySemanticOptions { EmbeddingApiKey = "test-only", EmbeddingDimensions = 3 };
        // Typed clients can be recreated per scope; the cache is shared, not their handlers/tokens.
        OpenAiMemoryEmbeddingClient Client() => new(http, options, metrics, cache);
        var first = await Client().EmbedAsync("que horas são?", default);
        first[0] = 99;
        var cached = await Client().EmbedAsync("que horas são?", default);
        Assert.Equal(1, cached[0]);
        cached[0] = 88;
        Assert.Equal(1, (await Client().EmbedAsync("que horas são?", default))[0]);
        Assert.Equal(1, handler.Calls);
        // Different text is a miss; no semantic or whitespace normalization is applied.
        await Client().EmbedAsync("Que horas são?", default);
        Assert.Equal(2, handler.Calls);
        clock.Advance(MemoryEmbeddingCache.Lifetime);
        await Client().EmbedAsync("que horas são?", default);
        Assert.Equal(3, handler.Calls);
        // Expiration uses insertion age, not a sliding lifetime.
        clock.Advance(TimeSpan.FromMinutes(14));
        await Client().EmbedAsync("que horas são?", default);
        Assert.Equal(3, handler.Calls);
        clock.Advance(TimeSpan.FromMinutes(1));
        await Client().EmbedAsync("que horas são?", default);
        Assert.Equal(4, handler.Calls);
        for (var i = 0; i < MemoryEmbeddingCache.Capacity; i++)
            await Client().EmbedAsync("bounded-" + i, default);
        await Client().EmbedAsync("que horas são?", default);
        Assert.Equal(5 + MemoryEmbeddingCache.Capacity, handler.Calls);
    }

    [Fact]
    public async Task ProviderModelAndDimensionsPartitionTheCache()
    {
        var cache = new MemoryEmbeddingCache(new Clock());
        var handler = new Handler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
        using var metrics = new AegisMetrics();
        var options = new MemorySemanticOptions { EmbeddingApiKey = "test-only", EmbeddingDimensions = 3 };
        var client = new OpenAiMemoryEmbeddingClient(http, options, metrics, cache);
        await client.EmbedAsync("pedido", default);
        options.EmbeddingModel = "different-model";
        await client.EmbedAsync("pedido", default);
        options.EmbeddingDimensions = 4;
        Assert.Equal(4, (await client.EmbedAsync("pedido", default)).Length);
        options.EmbeddingBaseUrl = "https://different-provider.test";
        await client.EmbedAsync("pedido", default);
        Assert.Equal(4, handler.Calls);
        await client.EmbedAsync("pedido", default);
        Assert.Equal(4, handler.Calls);
        options.EmbeddingApiKey = "";
        Assert.Equal("embedding_not_configured", (await Assert.ThrowsAsync<MemorySemanticException>(() =>
            client.EmbedAsync("pedido", default))).Code);
    }

    [Fact]
    public async Task FailuresAndCancellationAreNeverCachedAndHitsRespectCancellation()
    {
        var cache = new MemoryEmbeddingCache(new Clock());
        var handler = new Handler { Fail = true };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
        using var metrics = new AegisMetrics();
        var options = new MemorySemanticOptions { EmbeddingApiKey = "test-only", EmbeddingDimensions = 3 };
        var client = new OpenAiMemoryEmbeddingClient(http, options, metrics, cache);
        await Assert.ThrowsAsync<MemorySemanticException>(() => client.EmbedAsync("pedido", default));
        handler.Fail = false;
        handler.Cancel = true;
        Assert.Equal("embedding_timeout", (await Assert.ThrowsAsync<MemorySemanticException>(() => client.EmbedAsync("pedido", default))).Code);
        handler.Cancel = false;
        await client.EmbedAsync("pedido", default);
        Assert.Equal(3, handler.Calls);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.EmbedAsync("pedido", cancellation.Token));
        Assert.Equal(3, handler.Calls);
    }
    [Fact]
    public async Task ConcurrentRequestsDoNotShareCancellationTokensOrFailedTasks()
    {
        var cache = new MemoryEmbeddingCache(new Clock());
        var handler = new Handler { Block = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
        using var metrics = new AegisMetrics();
        var options = new MemorySemanticOptions { EmbeddingApiKey = "test-only", EmbeddingDimensions = 3 };
        var client = new OpenAiMemoryEmbeddingClient(http, options, metrics, cache);
        using var cancellation = new CancellationTokenSource();
        var canceledRequest = client.EmbedAsync("pedido concorrente", cancellation.Token);
        var survivingRequest = client.EmbedAsync("pedido concorrente", default);
        Assert.Equal(2, handler.Calls);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledRequest);
        handler.Block.SetResult(true);
        Assert.Equal(3, (await survivingRequest).Length);
        Assert.Equal(3, (await client.EmbedAsync("pedido concorrente", default)).Length);
        Assert.Equal(2, handler.Calls);
    }

}
