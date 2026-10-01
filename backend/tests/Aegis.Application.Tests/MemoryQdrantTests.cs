using Aegis.Application.Memory;
using Aegis.Infrastructure.Memory;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class MemoryQdrantTests
{
    public sealed class QdrantFactAttribute : FactAttribute
    {
        public QdrantFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEGIS_MEMORY_TEST_QDRANT_URL")))
                Skip = "Set AEGIS_MEMORY_TEST_QDRANT_URL to a disposable Qdrant instance.";
        }
    }

    [QdrantFact]
    public async Task CollectionPointSearchDeleteAndSchemaMismatchArePhysical()
    {
        var url = Environment.GetEnvironmentVariable("AEGIS_MEMORY_TEST_QDRANT_URL")!;
        var collection = "memory_test_" + Guid.NewGuid().ToString("N");
        using var http = new HttpClient { BaseAddress = new Uri(url.TrimEnd('/') + "/") };
        var options = new MemorySemanticOptions { QdrantBaseUrl = url, CollectionName = collection, EmbeddingDimensions = 4 };
        var store = new QdrantMemoryVectorStore(http, options);
        try
        {
            Assert.True(await store.EnsureCollectionAsync(default));
            Assert.False(await store.EnsureCollectionAsync(default));
            var id = Guid.NewGuid();
            var point = new MemoryVectorPoint(id, 1, "HASH", "test-model", 4);
            Assert.Null(await store.GetPointAsync(id, default));
            await store.UpsertAsync(point, [1, 0, 0, 0], default);
            await store.UpsertAsync(point, [1, 0, 0, 0], default);
            Assert.Equal(point, await store.GetPointAsync(id, default));
            Assert.Equal(id, Assert.Single(await store.SearchAsync([1, 0, 0, 0], 10, 0.5, default)).MemoryId);
            var mismatch = new QdrantMemoryVectorStore(http, new MemorySemanticOptions { CollectionName = collection, EmbeddingDimensions = 3 });
            Assert.Equal("qdrant_schema_mismatch", (await Assert.ThrowsAsync<MemorySemanticException>(() => mismatch.EnsureCollectionAsync(default))).Code);
            Assert.Equal(point, await store.GetPointAsync(id, default));
            await store.DeleteAsync(id, default);
            await store.DeleteAsync(id, default);
            Assert.Null(await store.GetPointAsync(id, default));
        }
        finally
        {
            using var response = await http.DeleteAsync("collections/" + collection);
            response.EnsureSuccessStatusCode();
        }
    }
}
