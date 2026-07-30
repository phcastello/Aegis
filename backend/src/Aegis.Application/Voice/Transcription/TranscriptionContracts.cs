namespace Aegis.Application.Voice.Transcription;

public sealed record TranscriptionRequest(
    Guid TranscriptionRequestId,
    byte[] Audio,
    string FileName,
    string ContentType,
    long ClientDurationMilliseconds);

public sealed record TranscriptionResult(Guid TranscriptionRequestId, string Text);

public sealed record TranscriptionServiceStatus(bool Enabled, bool Configured, int MaxRecordingSeconds);

public sealed record ProviderTranscriptionResult(string Text, string Provider, string Model);

public enum TranscriptionFailureKind
{
    Technical,
    InvalidInput,
    Timeout
}

public sealed class TranscriptionProviderException(
    TranscriptionFailureKind kind,
    string provider,
    string message,
    int? externalStatusCode = null,
    Exception? innerException = null) : Exception(message, innerException)
{
    public TranscriptionFailureKind Kind { get; } = kind;
    public string Provider { get; } = provider;
    public int? ExternalStatusCode { get; } = externalStatusCode;
}

public sealed class TranscriptionRequestException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public interface ISpeechTranscriptionProvider
{
    string ProviderName { get; }
    string Model { get; }
    int KeytermCount { get; }
    bool IsConfigured { get; }
    Task<ProviderTranscriptionResult> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken = default);
}

public interface ISpeechTranscriptionService
{
    Task<TranscriptionResult> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken = default);
    TranscriptionServiceStatus GetStatus();
}
