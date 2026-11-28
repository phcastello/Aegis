using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Aegis.Application.Chat;

public sealed record AttachmentVideoFrame(double Seconds, byte[] Jpeg);

public sealed record AttachmentVideoMediaResult(
    double? DurationSeconds, double StartSeconds, double EndSeconds, bool HasAudio,
    IReadOnlyList<AttachmentVideoFrame> Frames, byte[]? AudioWav, double? AudioEndSeconds,
    string? Error);

public static class AttachmentVideoMedia
{
    private const int MaxImageBytes = 150 * 1024;
    private const int MaxAudioSeconds = 60;
    private const int AudioBytesPerSecond = 16_000 * 2;
    private const int WavHeaderBytes = 44;
    private const int MaxToolOutputChars = 32 * 1024;
    private static readonly SemaphoreSlim Slots = new(2, 2);
    private static readonly string[] InputLimits =
    [
        "-protocol_whitelist", "file", "-format_whitelist", "mov,matroska",
        "-probesize", "4194304", "-analyzeduration", "5000000"
    ];

    public static async Task<AttachmentVideoMediaResult> InspectAsync(
        Stream source, double? startSeconds, double? endSeconds, bool includeFrames, bool includeAudio,
        CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
            return Failed("A análise de vídeo está indisponível neste servidor.");
        if ((startSeconds is double start && (!double.IsFinite(start) || start < 0))
            || (endSeconds is double end && (!double.IsFinite(end) || end <= (startSeconds ?? 0))))
            return Failed("Informe um intervalo de vídeo válido.");
        if (source.CanSeek && source.Length > ChatUploadLimits.MaxFileBytes)
            return Failed("O vídeo excede 512 MiB.");

        await Slots.WaitAsync(ct);
        DirectoryInfo? directory = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(40));
            var token = timeout.Token;
            directory = Directory.CreateTempSubdirectory("aegis-video-");
            var input = Path.Combine(directory.FullName, "video");
            if (!await WriteInputAsync(source, input, token))
                return Failed("O vídeo excede 512 MiB.");

            var probe = await ProbeAsync(input, directory, token);
            if (probe is null)
                return Failed("Não consegui abrir a faixa de vídeo. Os contêineres aceitos são MP4, MOV, WebM e MKV.");
            var (duration, hasAudio) = probe.Value;
            if (duration is null)
                return Failed("Não consegui determinar a duração do vídeo.", hasAudio: hasAudio);

            var rangeStart = startSeconds ?? 0;
            var rangeEnd = Math.Min(endSeconds ?? duration.Value, duration.Value);
            if (rangeStart >= rangeEnd)
                return Failed("O intervalo começa após o fim do vídeo.", duration, hasAudio);

            IReadOnlyList<AttachmentVideoFrame> frames = [];
            if (includeFrames)
                frames = await ReadFramesAsync(input, directory, rangeStart, rangeEnd,
                    startSeconds is null && endSeconds is null ? 8 : 12, token);

            byte[]? audio = null;
            double? audioEnd = null;
            if (includeAudio && hasAudio)
            {
                audio = await ReadAudioAsync(input, directory, rangeStart, rangeEnd, token);
                if (audio is not null)
                    audioEnd = Math.Min(rangeEnd, rangeStart + (audio.Length - WavHeaderBytes) / (double)AudioBytesPerSecond);
            }

            var error = includeFrames && frames.Count == 0 ? "Nenhum quadro pôde ser lido no intervalo." : null;
            if (includeAudio && hasAudio && audio is null)
                error = string.IsNullOrEmpty(error) ? "Nenhum áudio pôde ser lido no intervalo."
                    : error + " Nenhum áudio pôde ser lido no intervalo.";
            return new(duration, rangeStart, rangeEnd, hasAudio, frames, audio, audioEnd, error);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failed("A análise de vídeo excedeu o tempo disponível.");
        }
        catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException or JsonException)
        {
            return Failed("Não foi possível ler o vídeo.");
        }
        finally
        {
            try { directory?.Delete(recursive: true); }
            finally { Slots.Release(); }
        }
    }

    private static AttachmentVideoMediaResult Failed(string error, double? duration = null, bool hasAudio = false) =>
        new(duration, 0, 0, hasAudio, [], null, null, error);

    private static async Task<bool> WriteInputAsync(Stream source, string input, CancellationToken ct)
    {
        if (source.CanSeek) source.Position = 0;
        await using var file = new FileStream(input, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[65536];
        long size = 0;
        int length;
        while ((length = await source.ReadAsync(buffer, ct)) > 0)
        {
            size += length;
            if (size > ChatUploadLimits.MaxFileBytes) return false;
            await file.WriteAsync(buffer.AsMemory(0, length), ct);
        }
        return true;
    }

    private static async Task<(double? Duration, bool HasAudio)?> ProbeAsync(
        string input, DirectoryInfo directory, CancellationToken ct)
    {
        var (ok, json) = await RunAsync("ffprobe", [
            "-v", "error", "-max_alloc", "67108864", .. InputLimits,
            "-show_entries", "format=duration:stream=codec_type:stream_disposition=attached_pic",
            "-of", "json", input
        ], directory, 1024 * 1024, ct);
        if (!ok) return null;
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("streams", out var streams)) return null;
        var video = false;
        var audio = false;
        foreach (var stream in streams.EnumerateArray())
        {
            if (!stream.TryGetProperty("codec_type", out var codecType)) continue;
            if (codecType.ValueEquals("audio")) audio = true;
            if (codecType.ValueEquals("video") && (!stream.TryGetProperty("disposition", out var disposition)
                || !disposition.TryGetProperty("attached_pic", out var attached) || attached.GetInt32() == 0))
                video = true;
        }
        if (!video) return null;

        return (root.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var value)
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration)
            && double.IsFinite(duration) && duration > 0 ? duration : null, audio);
    }

    private static async Task<IReadOnlyList<AttachmentVideoFrame>> ReadFramesAsync(
        string input, DirectoryInfo directory, double start, double end, int limit, CancellationToken ct)
    {
        var span = end - start;
        var count = (int)Math.Min(limit, Math.Ceiling(span * 2));
        var frames = new List<AttachmentVideoFrame>(count);
        for (var index = 0; index < count; index++)
        {
            var target = start + span * index / count;
            var prefix = $"frame-{index}-";
            var output = Path.Combine(directory.FullName, prefix + "%d.jpg");
            // The image2 muxer uses the frame PTS in the file name. The requested seek can land
            // on a later frame, so only the decoded frame's normalized millisecond PTS is reported.
            var (ok, _) = await RunAsync("ffmpeg", [
                "-nostdin", "-hide_banner", "-loglevel", "error", "-max_alloc", "67108864",
                "-copyts", "-start_at_zero", "-ss", FormatSeconds(target), .. InputLimits, "-threads", "1", "-i", input,
                "-map", "0:V:0", "-an", "-sn", "-dn", "-filter_threads", "1",
                "-vf", "scale='min(960,iw)':'min(960,ih)':force_original_aspect_ratio=decrease:force_divisible_by=2,setsar=1",
                "-frames:v", "1", "-fps_mode", "passthrough", "-enc_time_base", "1:1000",
                "-q:v", "7", "-threads", "1", "-f", "image2", "-frame_pts", "1", output
            ], directory, MaxImageBytes, ct);
            if (!ok) continue;
            var image = directory.EnumerateFiles(prefix + "*.jpg").SingleOrDefault();
            if (image is null || image.Length is <= 0 or > MaxImageBytes) continue;
            var name = Path.GetFileNameWithoutExtension(image.Name);
            if (!long.TryParse(name.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture,
                    out var milliseconds))
                continue;
            var seconds = milliseconds / 1000d;
            if (seconds < start - .001 || seconds > end || (frames.Count > 0 && seconds <= frames[^1].Seconds))
                continue;
            frames.Add(new(seconds, await File.ReadAllBytesAsync(image.FullName, ct)));
        }
        return frames;
    }

    private static async Task<byte[]?> ReadAudioAsync(
        string input, DirectoryInfo directory, double start, double end, CancellationToken ct)
    {
        var output = Path.Combine(directory.FullName, "speech.wav");
        // FFmpeg rebases audio after the input seek. Pad late starts so transcript offsets still
        // match the video's clock even when its first audio packet starts later than its picture.
        var (ok, _) = await RunAsync("ffmpeg", [
            "-nostdin", "-hide_banner", "-loglevel", "error", "-max_alloc", "67108864",
            "-ss", FormatSeconds(start), .. InputLimits, "-threads", "1", "-i", input,
            "-t", FormatSeconds(Math.Min(end - start, MaxAudioSeconds)), "-map", "0:a:0", "-vn", "-sn", "-dn",
            "-filter_threads", "1", "-af", "aresample=16000:async=1:first_pts=0",
            "-ac", "1", "-c:a", "pcm_s16le", "-bitexact", "-f", "wav", output
        ], directory, WavHeaderBytes + AudioBytesPerSecond * MaxAudioSeconds, ct);
        var audio = new FileInfo(output);
        return ok && audio.Exists && audio.Length is > WavHeaderBytes and <= WavHeaderBytes + AudioBytesPerSecond * MaxAudioSeconds
            ? await File.ReadAllBytesAsync(output, ct)
            : null;
    }

    private static string FormatSeconds(double seconds) => seconds.ToString("0.######", CultureInfo.InvariantCulture);

    private static async Task<(bool Ok, string Output)> RunAsync(
        string tool, string[] arguments, DirectoryInfo directory, int maxFileBytes, CancellationToken ct)
    {
        var start = new ProcessStartInfo("/usr/bin/prlimit")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = directory.FullName
        };
        start.Environment.Clear();
        start.Environment["PATH"] = "/usr/bin:/bin";
        start.Environment["LC_ALL"] = "C";
        start.Environment["TMPDIR"] = directory.FullName;
        foreach (var arg in new[] { "--as=1610612736", "--cpu=6", $"--fsize={maxFileBytes}", "--core=0", "--", "/usr/bin/" + tool })
            start.ArgumentList.Add(arg);
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        ct.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new IOException("Conversor indisponível.");
        using var cancel = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        var discard = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        var buffer = new char[4096];
        var output = new StringBuilder();
        var truncated = false;
        int read;
        while ((read = await process.StandardOutput.ReadAsync(buffer, CancellationToken.None)) != 0)
        {
            var take = Math.Min(read, MaxToolOutputChars - output.Length);
            output.Append(buffer, 0, take);
            truncated |= take < read;
        }
        await Task.WhenAll(discard, process.WaitForExitAsync());
        ct.ThrowIfCancellationRequested();
        return (process.ExitCode == 0 && !truncated, output.ToString());
    }
}
