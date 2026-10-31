using System.Security.Cryptography;
using System.Text;
using Aegis.Application.Memory;

namespace Aegis.Infrastructure.Memory;

// Only the deterministic query vector is cached. Every retrieval still validates
// current Qdrant/PostgreSQL/graph state. Vectors can encode personal query data;
// they stay only in bounded process RAM, with hashed keys and no raw query text.
// Retrieval results and operational state are never cached here.
public sealed class MemoryEmbeddingCache(TimeProvider clock)
{
    public const int Capacity = 128;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    internal sealed record Key(string Provider, string Model, int Dimensions, string InputHash);
    private sealed record Entry(Key Key, float[] Vector, long Created);
    private readonly object gate = new();
    private readonly Dictionary<Key, LinkedListNode<Entry>> entries = [];
    private readonly LinkedList<Entry> recency = new();

    internal bool TryGet(Key key, out float[] vector)
    {
        lock (gate)
        {
            if (entries.TryGetValue(key, out var node))
            {
                recency.Remove(node);
                if (clock.GetElapsedTime(node.Value.Created) < Lifetime)
                {
                    recency.AddFirst(node);
                    vector = (float[])node.Value.Vector.Clone();
                    return true;
                }
                entries.Remove(key);
            }
        }
        vector = [];
        return false;
    }

    internal void Store(Key key, float[] vector)
    {
        lock (gate)
        {
            if (entries.Remove(key, out var previous)) recency.Remove(previous);
            var node = recency.AddFirst(new Entry(key, (float[])vector.Clone(), clock.GetTimestamp()));
            entries[key] = node;
            while (entries.Count > Capacity)
            {
                var last = recency.Last!;
                entries.Remove(last.Value.Key);
                recency.RemoveLast();
            }
        }
    }

    internal static Key CacheKey(MemorySemanticOptions options, string text) => new(
        options.EmbeddingBaseUrl.TrimEnd('/'), options.EmbeddingModel, options.EmbeddingDimensions,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
}
