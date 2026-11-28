using System.Security.Cryptography;
using System.Net;
using System.Net.Http.Json;
using Aegis.Api.Nodes;
using Aegis.Application.Nodes;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class WakeDatasetTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly Guid node = Guid.NewGuid(), id = Guid.NewGuid();

    private WakeDatasetStore Store(long quota = 2000000) => new(new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["WakeDataset:Directory"] = directory, ["WakeDataset:MaximumBytes"] = quota.ToString() }).Build());

    private static WakeSample Sample()
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);
        writer.Write("RIFF"u8); writer.Write(38); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000);
        writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(2); writer.Write((short)42);
        var wav = buffer.ToArray();
        return new(1, DateTimeOffset.UtcNow, Guid.NewGuid(), "wake_word", "unstable", 20, "model", null,
            null, 0.7, null, Convert.ToHexString(SHA256.HashData(wav)), wav, "election_lost", null, null, "unknown");
    }

    [Fact]
    public async Task OfflineReplaysAreDurableAndScopedToAuthenticatedNode()
    {
        var store = Store();
        var sample = Sample();
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => store.SaveAsync(node, id, sample, default)));
        await Store().SaveAsync(node, id, sample, default);
        Assert.Single(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories));
        var conflict = await Assert.ThrowsAsync<NodeException>(() => store.SaveAsync(node, id, sample with { Disposition = "activated" }, default));
        Assert.Equal(409, conflict.Status);
        await store.SaveAsync(Guid.NewGuid(), id, sample, default);
        Assert.Equal(2, Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories).Count());
    }

    [Fact]
    public async Task RejectsMalformedOrOversizedAudioWithoutSaving()
    {
        var sample = Sample();
        var store = Store();
        foreach (var invalid in new[] { sample with { Wav = [1, 2] }, sample with { Sha256 = "bad" },
            sample with { PostWav = sample.Wav }, sample with { Disposition = "false_positive" },
            sample with { Wav = new byte[WakeDatasetStore.MaximumWav + 2] }, sample with { CommandDisposition = "false_positive" } })
            Assert.Equal(400, (await Assert.ThrowsAsync<NodeException>(() => store.SaveAsync(node, id, invalid, default))).Status);
        Assert.False(Directory.Exists(directory));
        await store.SaveAsync(node, id, sample with { PostWav = sample.Wav, PostSha256 = sample.Sha256 }, default);
        Assert.Equal(507, (await Assert.ThrowsAsync<NodeException>(() => Store(10).SaveAsync(node, Guid.NewGuid(), sample, default))).Status);
    }

    [Fact]
    public async Task IngestAcceptsOnlyEnabledNodeAndAcknowledgesReplaysWithoutOfferingDownload()
    {
        var (app, client, registry) = await NodeApiTests.Host(directory);
        await using var host = app;
        var path = "/api/nodes/me/wake-samples/" + id;
        var sample = Sample();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync(path, sample)).StatusCode);
        var paired = await NodeApiTests.Pair(registry);
        client.DefaultRequestHeaders.Add("Authorization", "AegisNode " + paired.Secret);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(path, sample)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(path, sample)).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(path, sample with { Disposition = "candidate" })).StatusCode);
        await registry.SetEnabledAsync(null, paired.Node.Id, false);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(path, sample)).StatusCode);
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
