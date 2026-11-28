using System.Text.Json;
using Aegis.Application.Nodes;

namespace Aegis.Api.Nodes;

public sealed record WakeDatasetEntry(Guid NodeId, Guid EventId, DateTimeOffset ActivatedAt, Guid GroupId,
    string Origin, string? ModelVersion, double? DecisionScore, double? VerifierScore, string Disposition,
    string? CommandDisposition, bool HasPostAudio, long Bytes, string? HumanLabel = null, string? CommandLabel = null,
    DateTimeOffset? ReviewedAt = null)
{
    public static WakeDatasetEntry From(Guid node, Guid id, WakeSample source, long bytes) =>
        new(node, id, source.ActivatedAt, source.GroupId, source.Origin, source.ModelVersion, source.DecisionScore,
            source.VerifierScore, source.Disposition, source.CommandDisposition, source.PostWav is not null, bytes);
}

public sealed record WakeDatasetLabels(string? HumanLabel, string? CommandLabel);
public sealed record WakeDatasetNode(Guid NodeId, long Bytes, int Samples, bool QuotaBlocked);
public sealed record WakeDatasetPage(IReadOnlyList<WakeDatasetEntry> Samples, int Total, int? NextOffset, long Bytes,
    long MaximumBytes, bool QuotaBlocked, IReadOnlyList<WakeDatasetNode> Nodes);

public sealed partial class WakeDatasetStore
{
    private const int PageSize = 24;
    private Dictionary<(Guid Node, Guid Event), WakeDatasetEntry>? index;
    public async Task<WakeDatasetPage> ReviewAsync(Guid? nodeId, string? filter, int offset, CancellationToken ct)
    {
        if (offset < 0 || nodeId == Guid.Empty ||
            filter is not (null or "unreviewed" or "ignored" or "election_lost" or "verifier_rejected" or "false_positive" or "correct" or "not_directed"))
            throw new NodeException("invalid_wake_review_filter", "Selecione um filtro válido de ativações.");

        await writer.WaitAsync(ct);
        try
        {
            index ??= await ReadIndexAsync(ct);
            var quotaBlocked = ReadQuotaMarkers();
            var nodes = index.Values.GroupBy(item => item.NodeId).ToDictionary(
                group => group.Key,
                group => new WakeDatasetNode(group.Key, group.Sum(item => item.Bytes), group.Count(), quotaBlocked.Contains(group.Key)));
            foreach (var blocked in quotaBlocked)
                nodes.TryAdd(blocked, new WakeDatasetNode(blocked, 0, 0, true));
            var matching = index.Values.Where(item => (nodeId is null || item.NodeId == nodeId) && Matches(item, filter))
                .OrderByDescending(item => item.ActivatedAt).ThenBy(item => item.NodeId).ThenBy(item => item.EventId).ToArray();
            var page = matching.Skip(offset).Take(PageSize).ToArray();
            var nextOffset = (long)offset + page.Length < matching.Length ? offset + page.Length : (int?)null;
            return new(page, matching.Length, nextOffset, nodes.Values.Sum(node => node.Bytes), quota,
                quotaBlocked.Count > 0, nodes.Values.OrderByDescending(node => node.Bytes).ThenBy(node => node.NodeId).ToArray());
        }
        finally { writer.Release(); }
    }

    public async Task<WakeDatasetEntry> LabelAsync(Guid node, Guid id, WakeDatasetLabels labels, CancellationToken ct)
    {
        if (labels.HumanLabel is not (null or "correct" or "false_positive" or "uncertain") ||
            labels.CommandLabel is not (null or "directed" or "not_directed" or "uncertain"))
            throw new NodeException("invalid_wake_labels", "Informe rótulos válidos para a amostra.");
        await writer.WaitAsync(ct);
        try
        {
            var entry = await ReadEntryAsync(node, id, ct) ?? throw MissingSample();
            var updated = entry with
            {
                HumanLabel = labels.HumanLabel,
                CommandLabel = labels.CommandLabel,
                ReviewedAt = labels.HumanLabel is not null || labels.CommandLabel is not null ? DateTimeOffset.UtcNow : null
            };
            await WriteMetadataAsync(updated, ct);
            if (index is not null) index[(node, id)] = updated;
            return updated;
        }
        finally { writer.Release(); }
    }

    public async Task<byte[]> AudioAsync(Guid node, Guid id, bool command, CancellationToken ct)
    {
        await writer.WaitAsync(ct);
        try
        {
            if (File.Exists(SamplePath(node, id, ".deleted"))) throw MissingSample();
            var sample = await ReadSampleAsync(node, id, ct) ?? throw MissingSample();
            return (command ? sample.PostWav : sample.Wav) ?? throw MissingSample();
        }
        finally { writer.Release(); }
    }

    public async Task DeleteAsync(Guid node, Guid id, CancellationToken ct)
    {
        await writer.WaitAsync(ct);
        try
        {
            var tombstone = SamplePath(node, id, ".deleted");
            var sample = SamplePath(node, id, ".json");
            if (!File.Exists(tombstone))
            {
                if (!File.Exists(sample)) throw MissingSample();
                // Acknowledgement lost in transit must never re-create deleted audio.
                await WriteAtomicAsync(tombstone, [], ct);
            }
            var recoversSpace = File.Exists(sample);
            File.Delete(sample);
            File.Delete(SamplePath(node, id, ".meta"));
            index?.Remove((node, id));
            used = null;
            if (recoversSpace)
                foreach (var blocked in ReadQuotaMarkers()) File.Delete(QuotaMarker(blocked));
        }
        finally { writer.Release(); }
    }

    private async Task<Dictionary<(Guid Node, Guid Event), WakeDatasetEntry>> ReadIndexAsync(CancellationToken ct)
    {
        var entries = new Dictionary<(Guid, Guid), WakeDatasetEntry>();
        if (!Directory.Exists(root)) return entries;
        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            if (!Guid.TryParseExact(Path.GetFileName(folder), "D", out var node)) continue;
            foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
            {
                ct.ThrowIfCancellationRequested();
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "D", out var id)) continue;
                if (File.Exists(SamplePath(node, id, ".deleted")))
                {
                    File.Delete(file);
                    File.Delete(SamplePath(node, id, ".meta"));
                    used = null;
                    continue;
                }
                if (await ReadEntryAsync(node, id, ct) is { } entry) entries[(node, id)] = entry;
            }
        }
        return entries;
    }

    private async Task<WakeDatasetEntry?> ReadEntryAsync(Guid node, Guid id, CancellationToken ct)
    {
        var samplePath = SamplePath(node, id, ".json");
        if (!File.Exists(samplePath) || File.Exists(SamplePath(node, id, ".deleted"))) return null;
        if (index?.TryGetValue((node, id), out var indexed) == true) return indexed;
        var sidecar = SamplePath(node, id, ".meta");
        if (File.Exists(sidecar))
        {
            await using var file = File.OpenRead(sidecar);
            return await JsonSerializer.DeserializeAsync<WakeDatasetEntry>(file, Json, ct);
        }
        var legacy = await ReadSampleAsync(node, id, ct);
        return legacy is null ? null : WakeDatasetEntry.From(node, id, legacy, new FileInfo(samplePath).Length);
    }

    private async Task<WakeSample?> ReadSampleAsync(Guid node, Guid id, CancellationToken ct)
    {
        var path = SamplePath(node, id, ".json");
        if (!File.Exists(path)) return null;
        await using var file = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<WakeSample>(file, Json, ct);
    }

    private static bool Matches(WakeDatasetEntry item, string? filter) => filter switch
    {
        "unreviewed" => item.HumanLabel is null && item.CommandLabel is null,
        "ignored" => item.CommandDisposition == "ignored",
        "election_lost" or "verifier_rejected" => item.Disposition == filter,
        "false_positive" or "correct" => item.HumanLabel == filter,
        "not_directed" => item.CommandLabel == filter,
        _ => true
    };

    private Task WriteMetadataAsync(WakeDatasetEntry entry, CancellationToken ct) =>
        WriteAtomicAsync(SamplePath(entry.NodeId, entry.EventId, ".meta"), JsonSerializer.SerializeToUtf8Bytes(entry, Json), ct);

    private static async Task WriteAtomicAsync(string path, byte[] data, CancellationToken ct)
    {
        var staging = Path.Combine(Path.GetDirectoryName(path)!, "." + Guid.NewGuid().ToString("N") + ".part");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var file = new FileStream(staging, options))
            {
                await file.WriteAsync(data, ct);
                file.Flush(flushToDisk: true);
            }
            File.Move(staging, path, overwrite: true);
        }
        finally { File.Delete(staging); }
    }

    private static void EnsurePrivateDirectory(string directory)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private string NodeFolder(Guid node) => Path.Combine(root, node.ToString("D"));
    private string SamplePath(Guid node, Guid id, string extension) => Path.Combine(NodeFolder(node), id.ToString("D") + extension);
    private string QuotaMarker(Guid node) => Path.Combine(root, node.ToString("D") + ".quota");
    private HashSet<Guid> ReadQuotaMarkers()
    {
        var nodes = new HashSet<Guid>();
        if (!Directory.Exists(root)) return nodes;
        foreach (var path in Directory.EnumerateFiles(root, "*.quota"))
            if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "D", out var node)) nodes.Add(node);
        return nodes;
    }
    private static NodeException MissingSample() =>
        new("wake_sample_missing", "Esta amostra de ativação não existe mais.", 404);
}
