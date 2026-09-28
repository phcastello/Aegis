using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aegis.Application.Memory;

namespace Aegis.Infrastructure.Memory;

public sealed class QdrantMemoryVectorStore(HttpClient http, MemorySemanticOptions options) : IMemoryVectorStore
{
    private string Collection => Uri.EscapeDataString(options.CollectionName);

    public async Task<bool> EnsureCollectionAsync(CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"collections/{Collection}", null, ct, allowNotFound: true);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            using var created = await SendAsync(HttpMethod.Put, $"collections/{Collection}", new { vectors = new { size = options.EmbeddingDimensions, distance = "Cosine" } }, ct);
            return true;
        }
        using var body = await ReadJsonAsync(response, ct);
        try
        {
            var vectors = body.RootElement.GetProperty("result").GetProperty("config").GetProperty("params").GetProperty("vectors");
            if (vectors.GetProperty("size").GetInt32() != options.EmbeddingDimensions ||
                !string.Equals(vectors.GetProperty("distance").GetString(), "Cosine", StringComparison.OrdinalIgnoreCase))
                throw new MemorySemanticException("qdrant_schema_mismatch", false);
        }
        catch (KeyNotFoundException) { throw new MemorySemanticException("qdrant_schema_mismatch", false); }
        return false;
    }

    public async Task<MemoryVectorPoint?> GetPointAsync(Guid id, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, $"collections/{Collection}/points",
            new { ids = new[] { id.ToString() }, with_payload = true, with_vector = false }, ct);
        using var body = await ReadJsonAsync(response, ct);
        var result = body.RootElement.GetProperty("result");
        if (result.GetArrayLength() == 0) return null;
        var payload = result[0].GetProperty("payload");
        try
        {
            if (!Guid.TryParse(payload.GetProperty("memoryId").GetString(), out var payloadId) || payloadId != id)
                return null;
            return new MemoryVectorPoint(id, payload.GetProperty("revision").GetInt32(), payload.GetProperty("contentHash").GetString()!,
                payload.GetProperty("model").GetString()!, payload.GetProperty("dimensions").GetInt32());
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException)
        { return null; }
    }

    public async Task UpsertAsync(MemoryVectorPoint point, float[] vector, CancellationToken ct)
    {
        MemoryVectorValidation.Validate(vector, options.EmbeddingDimensions);
        using var response = await SendAsync(HttpMethod.Put, $"collections/{Collection}/points?wait=true",
            new { points = new[] { new { id = point.MemoryId.ToString(), vector, payload = new {
                memoryId = point.MemoryId.ToString(), revision = point.Revision, contentHash = point.ContentHash,
                model = point.Model, dimensions = point.Dimensions } } } }, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, $"collections/{Collection}/points/delete?wait=true",
            new { points = new[] { id.ToString() } }, ct);
    }

    public async Task<IReadOnlyList<MemoryVectorCandidate>> SearchAsync(float[] vector, int limit, double threshold, CancellationToken ct)
    {
        MemoryVectorValidation.Validate(vector, options.EmbeddingDimensions);
        using var response = await SendAsync(HttpMethod.Post, $"collections/{Collection}/points/search",
            new { vector, limit, score_threshold = threshold, with_payload = false, with_vector = false }, ct);
        using var body = await ReadJsonAsync(response, ct);
        try
        {
            return body.RootElement.GetProperty("result").EnumerateArray()
                .Where(x => x.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && Guid.TryParse(id.GetString(), out _))
                .Select(x => new MemoryVectorCandidate(Guid.Parse(x.GetProperty("id").GetString()!), x.GetProperty("score").GetDouble())).ToArray();
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new MemorySemanticException("qdrant_invalid_response"); }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? payload, CancellationToken ct, bool allowNotFound = false)
    {
        using var request = new HttpRequestMessage(method, path);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        try
        {
            var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode || allowNotFound && response.StatusCode == HttpStatusCode.NotFound) return response;
            var code = response.StatusCode switch
            {
                HttpStatusCode.NotFound => "qdrant_collection_missing",
                HttpStatusCode.TooManyRequests => "qdrant_rate_limited",
                _ when (int)response.StatusCode >= 500 => "qdrant_unavailable",
                _ => "qdrant_rejected"
            };
            var retryable = response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
            response.Dispose();
            throw new MemorySemanticException(code, retryable);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new MemorySemanticException("qdrant_timeout"); }
        catch (HttpRequestException) { throw new MemorySemanticException("qdrant_unavailable"); }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct); }
        catch (JsonException) { throw new MemorySemanticException("qdrant_invalid_response"); }
    }
}
