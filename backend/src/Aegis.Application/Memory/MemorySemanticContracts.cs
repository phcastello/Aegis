using Aegis.Domain.Entities;

namespace Aegis.Application.Memory;

public sealed class MemorySemanticOptions
{
    public bool Enabled { get; set; } = true;
    public string EmbeddingModel { get; set; } = "text-embedding-3-large";
    public int EmbeddingDimensions { get; set; } = 1536;
    public string EmbeddingBaseUrl { get; set; } = "https://api.openai.com";
    public string EmbeddingApiKey { get; set; } = "";
    public string QdrantBaseUrl { get; set; } = "http://qdrant:6333";
    public string CollectionName { get; set; } = "aegis_memory_semantic_large_v1";
    public double SearchScoreThreshold { get; set; } = 0.45;
    public int WorkerPollSeconds { get; set; } = 5;
}

public sealed record MemoryVectorPoint(Guid MemoryId, int Revision, string ContentHash, string Model, int Dimensions);
public sealed record MemoryVectorCandidate(Guid MemoryId, double Score);

public interface IMemoryEmbeddingClient
{
    Task<float[]> EmbedAsync(string text, CancellationToken ct);
}

public interface IMemoryVectorStore
{
    // True means a missing collection was created. Existing incompatible collections must never be deleted.
    Task<bool> EnsureCollectionAsync(CancellationToken ct);
    Task<MemoryVectorPoint?> GetPointAsync(Guid id, CancellationToken ct);
    Task UpsertAsync(MemoryVectorPoint point, float[] vector, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<MemoryVectorCandidate>> SearchAsync(float[] vector, int limit, double threshold, CancellationToken ct);
}

public sealed class MemorySemanticException(string code, bool retryable = true) : Exception(code)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}

public static class MemoryVectorValidation
{
    public static void Validate(float[] vector, int dimensions)
    {
        if (vector.Length != dimensions || vector.Any(x => !float.IsFinite(x)))
            throw new MemorySemanticException("embedding_invalid_vector", false);
    }
}

public interface IMemorySemanticProjectionStore
{
    Task<MemoryProjectionJob?> ClaimAsync(DateTimeOffset now, TimeSpan lease, CancellationToken ct);
    Task<bool> ReconcileClaimAsync(MemoryProjectionJob claim, Func<MemoryRecord?, CancellationToken, Task> reconcile, CancellationToken ct);
    Task<bool> FailAsync(MemoryProjectionJob claim, string code, DateTimeOffset now, TimeSpan? retryDelay, CancellationToken ct);
    Task<int> RequeueCurrentStateAsync(DateTimeOffset now, CancellationToken ct);
}
