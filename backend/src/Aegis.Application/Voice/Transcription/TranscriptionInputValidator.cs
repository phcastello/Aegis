using System.Collections.Frozen;
using System.Buffers.Binary;

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

    // Only the exact, unambiguous native PCM16 mono 16kHz WAV format is
    // eligible. We cannot establish lack of signal by inspecting compressed
    // bytes or unfamiliar WAV layouts, nor by guessing an amplitude floor.
    internal static bool IsSilentNativeWav(TranscriptionRequest request)
    {
        var mime = request.ContentType.Split(';', 2, StringSplitOptions.TrimEntries)[0];
        if (!mime.Equals("audio/wav", StringComparison.OrdinalIgnoreCase) &&
            !mime.Equals("audio/x-wav", StringComparison.OrdinalIgnoreCase))
            return false;
        ReadOnlySpan<byte> bytes = request.Audio;
        if (bytes.Length < 46 || (bytes.Length - 44) % 2 != 0 ||
            !bytes[..4].SequenceEqual("RIFF"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != bytes.Length - 8 ||
            !bytes[8..16].SequenceEqual("WAVEfmt "u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]) != 16 ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[20..]) != 1 ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[22..]) != 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[24..]) != 16000 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[28..]) != 32000 ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[32..]) != 2 ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[34..]) != 16 ||
            !bytes[36..40].SequenceEqual("data"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[40..]) != bytes.Length - 44)
            return false;

        return bytes[44..].IndexOfAnyExcept((byte)0) < 0;
    }

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
