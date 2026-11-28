using Aegis.Application.Chat;

namespace Aegis.Application.Voice.Transcription;

public sealed record VideoTranscriptSegment(double StartSeconds, double EndSeconds, string Text, bool Approximate);

public sealed record VideoAttachmentTranscript(IReadOnlyList<VideoTranscriptSegment> Segments, string? Error = null);

public interface IVideoAttachmentAudio
{
    Task<VideoAttachmentTranscript> TranscribeAsync(AttachmentVideoMediaResult clip, CancellationToken ct);
}

public sealed class VideoAttachmentAudio(ISpeechTranscriptionService transcription) : IVideoAttachmentAudio
{
    public async Task<VideoAttachmentTranscript> TranscribeAsync(AttachmentVideoMediaResult clip, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!clip.HasAudio)
            return new([], "O vídeo não tem faixa de áudio.");
        if (clip.AudioWav is not { Length: > 0 } audio || clip.AudioEndSeconds is not double end || end <= clip.StartSeconds)
            return new([], "Não consegui extrair o áudio neste intervalo.");

        var duration = end - clip.StartSeconds;
        var request = new TranscriptionRequest(
            Guid.NewGuid(), audio, "trecho.wav", "audio/wav", (long)Math.Ceiling(duration * 1000))
            { IsAttachmentAudio = true };

        try
        {
            var result = await transcription.TranscribeAsync(request, ct);
            if (string.IsNullOrWhiteSpace(result.Text))
                return new([]);
            if (result.TimedSegments is { Count: > 0 } timedSegments)
            {
                return new(timedSegments.Select(segment => new VideoTranscriptSegment(
                    clip.StartSeconds + segment.StartSeconds,
                    Math.Min(end, clip.StartSeconds + segment.EndSeconds),
                    segment.Text,
                    false)).ToArray());
            }
            return new([new(clip.StartSeconds, end, result.Text, true)]);
        }
        catch (TranscriptionRequestException exception)
        {
            var message = exception.StatusCode is 400 or 413 or 415
                ? "A transcrição não aceitou este trecho de áudio. Tente um intervalo mais curto."
                : "A transcrição do áudio está indisponível neste momento.";
            return new([], message);
        }
    }
}
