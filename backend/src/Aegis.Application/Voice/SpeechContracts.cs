namespace Aegis.Application.Voice;

public sealed record SpeechRequest(
    Guid TurnId,
    Guid SpeechRequestId,
    string NativeRequestId,
    string Text,
    Guid ConversationId);

public sealed record SpeechAudioFormat(string Format, int SampleRate, int Channels, string VoiceProfile);

public class SpeechStreamResponse(Stream stream, SpeechAudioFormat format) : IAsyncDisposable
{
    public Stream Stream { get; } = stream;
    public SpeechAudioFormat Format { get; } = format;
    public virtual ValueTask DisposeAsync() => Stream.DisposeAsync();
}

public sealed record SpeechServiceStatus(bool Enabled, bool Available, string Profile, int SampleRate, int Channels);

public sealed record StartSpeechRequest(Guid TurnId, Guid SpeechRequestId, Guid AssistantMessageId);

public sealed class VoiceStream : IAsyncDisposable
{
    public VoiceStream(Guid turnId, Guid speechRequestId, CancellationToken turnCancellationToken, SpeechStreamResponse upstream)
    {
        TurnId = turnId;
        SpeechRequestId = speechRequestId;
        TurnCancellationToken = turnCancellationToken;
        Upstream = upstream;
    }

    public Guid TurnId { get; }
    public Guid SpeechRequestId { get; }
    public CancellationToken TurnCancellationToken { get; }
    public SpeechStreamResponse Upstream { get; }
    public ValueTask DisposeAsync() => Upstream.DisposeAsync();
}
