using System.Text;
using System.Text.Json;
using Aegis.Application.Voice.Transcription;

namespace Aegis.Infrastructure.Voice.Transcription;

internal static class ElevenLabsTimedTranscript
{
    public static IReadOnlyList<TranscriptionSegment>? Read(JsonElement root, double duration)
    {
        if (!root.TryGetProperty("words", out var words) || words.ValueKind != JsonValueKind.Array)
            return null;

        var segments = new List<TranscriptionSegment>();
        var text = new StringBuilder();
        double? start = null;
        double end = 0;
        foreach (var word in words.EnumerateArray())
        {
            var type = ReadString(word, "type");
            var value = ReadString(word, "text");
            if (type == "spacing")
            {
                text.Append(value);
                continue;
            }
            if (type == "audio_event")
                continue;
            if (type != "word" || string.IsNullOrWhiteSpace(value) ||
                !TryReadTime(word, "start", out var wordStart) || !TryReadTime(word, "end", out var wordEnd) ||
                wordStart >= duration || wordEnd <= wordStart || wordEnd > duration + 0.25)
                return null;

            if (start is double segmentStart && (wordStart - segmentStart >= 8 || wordStart - end >= 1))
            {
                segments.Add(new(segmentStart, end, text.ToString().Trim()));
                text.Clear();
                start = null;
                end = 0;
            }
            start ??= wordStart;
            text.Append(value);
            end = Math.Max(end, Math.Min(duration, wordEnd));
        }
        if (start is double lastStart)
            segments.Add(new(lastStart, end, text.ToString().Trim()));
        return segments;
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryReadTime(JsonElement element, string property, out double seconds)
    {
        seconds = 0;
        return element.TryGetProperty(property, out var value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetDouble(out seconds) && double.IsFinite(seconds) && seconds >= 0;
    }
}
