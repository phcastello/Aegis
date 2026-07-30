using System.Collections.Frozen;

namespace Aegis.Application.Voice.Transcription;

public static class TranscriptionInputValidator
{
    private static readonly FrozenDictionary<string, FrozenSet<string>> ExtensionsByMime =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["audio/webm"] = [".webm"],
            ["video/webm"] = [".webm"],
            ["audio/mp4"] = [".mp4", ".m4a"],
            ["video/mp4"] = [".mp4", ".m4a"],
            ["audio/x-m4a"] = [".m4a"],
            ["audio/m4a"] = [".m4a"],
            ["audio/aac"] = [".aac"],
            ["audio/wav"] = [".wav"],
            ["audio/x-wav"] = [".wav"],
            ["audio/ogg"] = [".ogg"],
            ["audio/mpeg"] = [".mp3", ".mpeg", ".mpga"],
            ["audio/mp3"] = [".mp3"]
        }.ToFrozenDictionary(
            pair => pair.Key,
            pair => pair.Value.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

    public static void Validate(TranscriptionRequest request, int maxAudioBytes, int maxRecordingSeconds)
    {
        if (request.TranscriptionRequestId == Guid.Empty)
        {
            throw new TranscriptionRequestException(400, "O identificador da transcrição é inválido.");
        }

        if (request.Audio.Length == 0)
        {
            throw new TranscriptionRequestException(400, "O áudio está vazio.");
        }

        if (request.Audio.Length > Math.Max(1, maxAudioBytes))
        {
            throw new TranscriptionRequestException(413, "O áudio excede o limite permitido.");
        }

        var mimeType = request.ContentType.Split(';', 2, StringSplitOptions.TrimEntries)[0];
        if (!ExtensionsByMime.TryGetValue(mimeType, out var allowedExtensions))
        {
            throw new TranscriptionRequestException(415, "O formato de áudio não é compatível.");
        }

        var extension = Path.GetExtension(request.FileName);
        if (!string.IsNullOrWhiteSpace(extension) && !allowedExtensions.Contains(extension))
        {
            throw new TranscriptionRequestException(415, "O formato de áudio não é compatível.");
        }

        var maxDurationMilliseconds = checked((long)Math.Max(1, maxRecordingSeconds) * 1000);
        if (request.ClientDurationMilliseconds <= 0 || request.ClientDurationMilliseconds > maxDurationMilliseconds)
        {
            throw new TranscriptionRequestException(400, "A duração da gravação é inválida.");
        }
    }
}
