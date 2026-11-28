using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aegis.Application.Nodes;

namespace Aegis.Api.Nodes;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WakeSample(int SchemaVersion, DateTimeOffset ActivatedAt, Guid GroupId, string Origin,
    string? AppVersion, long? Build, string? ModelVersion, string? ModelSha256,
    double? InstantaneousScore, double? DecisionScore, double? VerifierScore, string Sha256, byte[] Wav,
    string Disposition = "candidate", string? PostSha256 = null, byte[]? PostWav = null, string? CommandDisposition = null);

public sealed class WakeDatasetStore(IConfiguration configuration)
{
    public const int MaximumWav = 44 + 35200 * 2;
    private readonly string root = configuration["WakeDataset:Directory"] ?? "/var/lib/aegis/wake-dataset";
    private readonly long quota = configuration.GetValue<long>("WakeDataset:MaximumBytes", 1024L * 1024 * 1024);
    private readonly SemaphoreSlim writer = new(1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private long? used;

    public async Task SaveAsync(Guid node, Guid eventId, WakeSample sample, CancellationToken ct)
    {
        if (node == Guid.Empty || eventId == Guid.Empty || sample.SchemaVersion != 1 ||
            sample.GroupId == Guid.Empty || sample.ActivatedAt == default || sample.Origin is not ("wake_word" or "diagnostic") ||
            sample.AppVersion?.Length > 96 || sample.ModelVersion?.Length > 160 || sample.Build is < 0 ||
            sample.ModelSha256 is { } hash && (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) ||
            !Score(sample.InstantaneousScore) || !Score(sample.DecisionScore) || !Score(sample.VerifierScore) ||
            sample.Disposition is not ("activated" or "election_lost" or "verifier_rejected" or "unavailable" or "candidate") ||
            sample.CommandDisposition is not (null or "accepted" or "ignored" or "no_speech" or "cancelled" or "unknown") ||
            !ValidAudio(sample.Wav, sample.Sha256, MaximumWav) ||
            (sample.PostWav is null ? sample.PostSha256 is not null : !ValidAudio(sample.PostWav, sample.PostSha256, 44 + 320000 * 2)))
            throw new NodeException("invalid_wake_sample", "A amostra de ativação é inválida.");

        var payload = JsonSerializer.SerializeToUtf8Bytes(sample, Json);
        var folder = Path.Combine(root, node.ToString("D"));
        var destination = Path.Combine(folder, eventId.ToString("D") + ".json");
        await writer.WaitAsync(ct);
        try
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(folder);
            else Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (File.Exists(destination))
            {
                var previous = await File.ReadAllBytesAsync(destination, ct);
                if (!previous.AsSpan().SequenceEqual(payload))
                    throw new NodeException("wake_sample_conflict", "Este identificador pertence a outra amostra.", 409);
                return;
            }

            if (used is null || used + payload.Length > quota)
                used = Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
            if (used + payload.Length > quota)
                throw new NodeException("wake_dataset_full", "A coleta central atingiu o limite de armazenamento.", 507);

            var staging = Path.Combine(folder, "." + Guid.NewGuid().ToString("N") + ".part");
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using (var file = new FileStream(staging, options))
                {
                    await file.WriteAsync(payload, ct);
                    file.Flush(flushToDisk: true);
                }
                File.Move(staging, destination);
                used += payload.Length;
            }
            finally { File.Delete(staging); }
        }
        finally { writer.Release(); }
    }

    private static bool Score(double? value) => value is null or (>= 0 and <= 1);

    private static bool ValidAudio(byte[]? data, string? hash, int maximum)
    {
        if (data is not { Length: >= 46 } || data.Length > maximum || data.Length % 2 != 0 ||
            !string.Equals(hash, Convert.ToHexString(SHA256.HashData(data)), StringComparison.OrdinalIgnoreCase)) return false;
        var b = data.AsSpan();
        return b[..4].SequenceEqual("RIFF"u8) && BinaryPrimitives.ReadInt32LittleEndian(b[4..]) == b.Length - 8 &&
            b[8..16].SequenceEqual("WAVEfmt "u8) && BinaryPrimitives.ReadInt32LittleEndian(b[16..]) == 16 &&
            BinaryPrimitives.ReadInt16LittleEndian(b[20..]) == 1 && BinaryPrimitives.ReadInt16LittleEndian(b[22..]) == 1 &&
            BinaryPrimitives.ReadInt32LittleEndian(b[24..]) == 16000 && BinaryPrimitives.ReadInt32LittleEndian(b[28..]) == 32000 &&
            BinaryPrimitives.ReadInt16LittleEndian(b[32..]) == 2 && BinaryPrimitives.ReadInt16LittleEndian(b[34..]) == 16 &&
            b[36..40].SequenceEqual("data"u8) && BinaryPrimitives.ReadInt32LittleEndian(b[40..]) == b.Length - 44;
    }
}
