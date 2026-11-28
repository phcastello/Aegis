using System.Buffers.Binary;
using System.Diagnostics;
using Aegis.Application.Chat;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class AttachmentVideoMediaTests
{
    public sealed class FfmpegFactAttribute : FactAttribute
    {
        public FfmpegFactAttribute()
        {
            if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/ffmpeg") || !File.Exists("/usr/bin/ffprobe"))
                Skip = "Requires Linux with the packaged FFmpeg and FFprobe.";
        }
    }

    [FfmpegFact]
    public async Task MediaInspectionReturnsDecodedTimestampsRotatedFramesAndAlignedAudio()
    {
        var directory = Directory.CreateTempSubdirectory("aegis-video-test-");
        try
        {
            var source = Path.Combine(directory.FullName, "source.mp4");
            await RunAsync("ffmpeg", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=12:duration=4",
                "-itsoffset", "1", "-f", "lavfi", "-i", "sine=duration=2", "-c:v", "libx264", "-threads", "1",
                "-c:a", "aac", "-movflags", "+faststart", source);
            using var file = new MemoryStream(WithQuarterTurn(await File.ReadAllBytesAsync(source)));
            var result = await AttachmentVideoMedia.InspectAsync(file, .45, 2.75, true, true, default);

            Assert.Null(result.Error);
            Assert.Equal(4, result.DurationSeconds);
            Assert.Equal(.45, result.StartSeconds);
            Assert.Equal(2.75, result.EndSeconds);
            Assert.True(result.HasAudio);
            Assert.Equal(5, result.Frames.Count);
            Assert.Equal(.5, result.Frames[0].Seconds, 3);
            Assert.All(result.Frames, frame => Assert.InRange(frame.Seconds, .45, 2.75));

            var jpeg = Path.Combine(directory.FullName, "frame.jpg");
            await File.WriteAllBytesAsync(jpeg, result.Frames[0].Jpeg);
            var size = await RunAsync("ffprobe", "-show_entries", "stream=width,height", "-of", "csv=p=0", jpeg);
            Assert.Equal("180,320", size.Trim());

            Assert.NotNull(result.AudioWav);
            Assert.NotNull(result.AudioEndSeconds);
            Assert.InRange(result.AudioEndSeconds.Value, 2.74, 2.75);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(result.AudioWav, 0, 4));
            Assert.Equal(16_000, BinaryPrimitives.ReadInt32LittleEndian(result.AudioWav.AsSpan(24, 4)));
            Assert.Equal("data", System.Text.Encoding.ASCII.GetString(result.AudioWav, 36, 4));
            var firstSound = Enumerable.Range(0, (result.AudioWav.Length - 44) / 2).First(i =>
                Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(result.AudioWav.AsSpan(44 + 2 * i, 2))) > 100);
            Assert.InRange(firstSound / 16000d, .53, .57);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static byte[] WithQuarterTurn(byte[] mp4)
    {
        // FFmpeg versions differ in how to set an MP4 display matrix from the CLI.
        // The first track in this fixture is video and its short duration ensures a version 0 header.
        var tkhd = mp4.AsSpan().IndexOf("tkhd"u8);
        Assert.True(tkhd >= 0);
        Assert.Equal(0, mp4[tkhd + 4]);
        var displayMatrix = new[] { 0, -65536, 0, 65536, 0, 0, 0, 0, 1 << 30 };
        for (var i = 0; i < displayMatrix.Length; i++)
            BinaryPrimitives.WriteInt32BigEndian(mp4.AsSpan(tkhd + 44 + i * sizeof(int), sizeof(int)),
                displayMatrix[i]);
        return mp4;
    }

    [FfmpegFact]
    public async Task AudioOnlyInspectionStopsAtTheExtractionBudget()
    {
        var directory = Directory.CreateTempSubdirectory("aegis-video-test-");
        try
        {
            var source = Path.Combine(directory.FullName, "source.mkv");
            await RunAsync("ffmpeg", "-f", "lavfi", "-i", "color=size=64x32:rate=1:duration=62",
                "-f", "lavfi", "-i", "sine=duration=62", "-c:v", "ffv1", "-c:a", "pcm_s16le", source);
            await using var file = File.OpenRead(source);
            var result = await AttachmentVideoMedia.InspectAsync(file, null, null, false, true, default);

            Assert.Null(result.Error);
            Assert.Equal(62, result.DurationSeconds);
            Assert.Empty(result.Frames);
            Assert.Equal(60, result.AudioEndSeconds);
            Assert.Equal(44 + 60 * 16_000 * 2, result.AudioWav?.Length);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [FfmpegFact]
    public async Task UnreadableOrEmptyVideoHasAnExplicitFailure()
    {
        using var source = new MemoryStream("not a video"u8.ToArray());
        var result = await AttachmentVideoMedia.InspectAsync(source, null, null, true, true, default);
        Assert.NotNull(result.Error);
        Assert.Null(result.DurationSeconds);
        Assert.Empty(result.Frames);
        Assert.Null(result.AudioWav);
    }

    private static async Task<string> RunAsync(string tool, params string[] arguments)
    {
        var start = new ProcessStartInfo("/usr/bin/" + tool)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        start.ArgumentList.Add("-v");
        start.ArgumentList.Add("error");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException($"Cannot run {tool}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await errors);
        return await output;
    }
}
