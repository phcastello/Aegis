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
        Assert.Single(Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories));
        Assert.Single(Directory.EnumerateFiles(directory, "*.meta", SearchOption.AllDirectories));
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

    [Fact]
    public async Task CentralReviewRequiresPairingAndKeepsDeletionAcrossReplaysAndRestarts()
    {
        var (app, client, registry) = await NodeApiTests.Host(directory);
        await using var host = app;
        const string review = "/api/nodes/settings/wake-dataset";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(review)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.DeleteAsync($"{review}/{node}/{id}")).StatusCode);
        var paired = await NodeApiTests.Pair(registry);
        client.DefaultRequestHeaders.Add("Authorization", "AegisNode " + paired.Secret);
        var sample = Sample() with { CommandDisposition = "ignored" };
        var ingest = $"/api/nodes/me/wake-samples/{id}";
        var entryPath = $"{review}/{paired.Node.Id}/{id}";
        var empty = await client.GetFromJsonAsync<WakeDatasetPage>(review);
        Assert.Empty(empty!.Samples);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(ingest, sample)).StatusCode);

        var page = await client.GetFromJsonAsync<WakeDatasetPage>(review + "?filter=ignored");
        var entry = Assert.Single(page!.Samples);
        Assert.Equal(id, entry.EventId);
        Assert.Equal(entry.Bytes, Assert.Single(page.Nodes).Bytes);
        Assert.Equal(page.Bytes, entry.Bytes);
        Assert.Null(entry.HumanLabel);
        Assert.DoesNotContain("wav", await client.GetStringAsync(review), StringComparison.OrdinalIgnoreCase);
        var audio = await client.GetFromJsonAsync<Dictionary<string, byte[]>>(entryPath + "/audio/trigger");
        Assert.Equal(sample.Wav, audio!["wav"]);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(entryPath + "/labels",
            new WakeDatasetLabels("made_up", null))).StatusCode);

        var response = await client.PutAsJsonAsync(entryPath + "/labels", new WakeDatasetLabels("correct", "not_directed"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var restarted = Store();
        var labeled = Assert.Single((await restarted.ReviewAsync(paired.Node.Id, "correct", 0, default)).Samples);
        Assert.Equal("not_directed", labeled.CommandLabel);
        Assert.Empty((await restarted.ReviewAsync(null, "unreviewed", 0, default)).Samples);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(entryPath)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(entryPath + "/audio/trigger")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(entryPath)).StatusCode);
        await Store().SaveAsync(paired.Node.Id, id, sample, default);
        Assert.Empty((await Store().ReviewAsync(null, null, 0, default)).Samples);
        Assert.Empty(Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories));

        await registry.SetEnabledAsync(null, paired.Node.Id, false);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(review)).StatusCode);
    }

    [Fact]
    public async Task ReviewPagesOldFilesAndReportsStorageBlockedUntilThereIsRoom()
    {
        var store = Store(10);
        await Assert.ThrowsAsync<NodeException>(() => store.SaveAsync(node, id, Sample(), default));
        var blocked = await store.ReviewAsync(null, null, 0, default);
        Assert.True(blocked.QuotaBlocked);
        Assert.True(Assert.Single(blocked.Nodes).QuotaBlocked);
        Assert.Equal(0, blocked.Bytes);
        Assert.True((await Store(10).ReviewAsync(null, null, 0, default)).QuotaBlocked);
        var sender = Store();
        var template = Sample();
        await sender.SaveAsync(node, id, template, default);
        File.Delete(Path.Combine(directory, node.ToString("D"), id.ToString("D") + ".meta"));
        for (var i = 1; i <= 24; ++i)
            await sender.SaveAsync(node, Guid.NewGuid(), template with { ActivatedAt = template.ActivatedAt.AddSeconds(i) }, default);
        var reader = Store();
        var first = await reader.ReviewAsync(node, null, 0, default);
        Assert.Equal(25, first.Total);
        Assert.Equal(24, first.NextOffset);
        var last = await reader.ReviewAsync(node, null, first.NextOffset!.Value, default);
        Assert.Null(last.NextOffset);
        Assert.Equal(id, Assert.Single(last.Samples).EventId);
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
