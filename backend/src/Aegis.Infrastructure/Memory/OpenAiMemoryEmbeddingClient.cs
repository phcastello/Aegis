using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aegis.Application.Memory;
using Aegis.Application.Observability;

namespace Aegis.Infrastructure.Memory;

public sealed class OpenAiMemoryEmbeddingClient(HttpClient http, MemorySemanticOptions options, AegisMetrics metrics,
    MemoryEmbeddingCache? cache = null) : IMemoryEmbeddingClient
{
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        var setting = options;
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(setting.EmbeddingApiKey)) throw new MemorySemanticException("embedding_not_configured", false);
        var cacheKey = MemoryEmbeddingCache.CacheKey(setting, text);
        var dimensions = setting.EmbeddingDimensions;
        if (cache is not null && cache.TryGet(cacheKey, out var cached))
        {
            metrics.MemoryEmbeddingCacheHits.Add(1);
            TurnLatencyProfile.Mark("memory.embedding_cache_hit", 1);
            return cached;
        }
        TurnLatencyProfile.Mark("memory.embedding_cache_miss", 1);
        metrics.MemoryEmbeddingRequests.Add(1);
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/embeddings")
        {
            Content = JsonContent.Create(new { model = setting.EmbeddingModel, input = text, dimensions = setting.EmbeddingDimensions })
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", setting.EmbeddingApiKey);
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw new MemorySemanticException(response.StatusCode switch
                {
                    HttpStatusCode.TooManyRequests => "embedding_rate_limited",
                    >= HttpStatusCode.InternalServerError => "embedding_unavailable",
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "embedding_auth_failed",
                    _ => "embedding_rejected"
                }, response.StatusCode is HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500);
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (body.RootElement.TryGetProperty("usage", out var usage) && usage.TryGetProperty("prompt_tokens", out var count) && count.TryGetInt64(out var tokens))
                metrics.MemoryEmbeddingInputTokens.Add(tokens);
            var data = body.RootElement.GetProperty("data");
            var values = data[0].GetProperty("embedding").EnumerateArray().Select(x => x.GetSingle()).ToArray();
            MemoryVectorValidation.Validate(values, dimensions);
            ct.ThrowIfCancellationRequested();
            cache?.Store(cacheKey, values);
            return values;
        }
        catch (MemorySemanticException) { metrics.MemoryEmbeddingFailures.Add(1); throw; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { metrics.MemoryEmbeddingFailures.Add(1); throw new MemorySemanticException("embedding_timeout"); }
        catch (HttpRequestException)
        { metrics.MemoryEmbeddingFailures.Add(1); throw new MemorySemanticException("embedding_unavailable"); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException or FormatException)
        { metrics.MemoryEmbeddingFailures.Add(1); throw new MemorySemanticException("embedding_invalid_response"); }
    }
}
