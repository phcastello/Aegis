using Microsoft.Extensions.Logging;

namespace Aegis.Application.Voice.Transcription;

public sealed class SpeechTranscriptionService(
    IEnumerable<ISpeechTranscriptionProvider> providers,
    ITranscriptionSettings settings,
    ILogger<SpeechTranscriptionService> logger) : ISpeechTranscriptionService
{
    private readonly IReadOnlyDictionary<string, ISpeechTranscriptionProvider> providersByName = providers
        .ToDictionary(provider => provider.ProviderName, StringComparer.OrdinalIgnoreCase);

    public async Task<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!settings.Enabled)
        {
            throw new TranscriptionRequestException(503, "A transcrição por voz não está disponível.");
        }

        TranscriptionInputValidator.Validate(request, settings.MaxAudioBytes, settings.MaxRecordingSeconds);
        var primary = FindProvider(settings.PrimaryProvider);
        var fallback = settings.FallbackEnabled ? FindProvider(settings.FallbackProvider) : null;
        var fallbackUsed = false;

        if (primary is not null && primary.IsConfigured)
        {
            try
            {
                var result = await TryTranscribeAsync(primary, request, false, null, cancellationToken);
                return new TranscriptionResult(request.TranscriptionRequestId, result.Text);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TranscriptionProviderException exception) when (exception.Kind is TranscriptionFailureKind.Technical or TranscriptionFailureKind.Timeout)
            {
                if (fallback is null || !fallback.IsConfigured)
                {
                    throw ToPublicException(exception);
                }

                fallbackUsed = true;
                var fallbackResult = await TryFallbackAsync(fallback, request, exception, cancellationToken);
                return new TranscriptionResult(request.TranscriptionRequestId, fallbackResult.Text);
            }
            catch (TranscriptionProviderException exception)
            {
                throw ToPublicException(exception);
            }
        }

        if (fallback is not null && fallback.IsConfigured)
        {
            fallbackUsed = true;
            var fallbackResult = await TryFallbackAsync(
                fallback,
                request,
                new TranscriptionProviderException(TranscriptionFailureKind.Technical, "primary", "Primary provider is unavailable."),
                cancellationToken);
            return new TranscriptionResult(request.TranscriptionRequestId, fallbackResult.Text);
        }

        logger.LogInformation(
            "{Event} {TranscriptionRequestId} {AudioBytes} {ClientDurationMilliseconds} {FallbackUsed}",
            "aegis_stt_unavailable",
            request.TranscriptionRequestId,
            request.Audio.Length,
            request.ClientDurationMilliseconds,
            fallbackUsed);
        throw new TranscriptionRequestException(503, "A transcrição por voz não está disponível.");
    }

    public TranscriptionServiceStatus GetStatus()
    {
        if (!settings.Enabled)
        {
            return new TranscriptionServiceStatus(false, false, Math.Max(1, settings.MaxRecordingSeconds));
        }

        var primaryAvailable = FindProvider(settings.PrimaryProvider)?.IsConfigured == true;
        var fallbackAvailable = settings.FallbackEnabled && FindProvider(settings.FallbackProvider)?.IsConfigured == true;
        return new TranscriptionServiceStatus(true, primaryAvailable || fallbackAvailable, Math.Max(1, settings.MaxRecordingSeconds));
    }

    private async Task<ProviderTranscriptionResult> TryFallbackAsync(
        ISpeechTranscriptionProvider fallback,
        TranscriptionRequest request,
        TranscriptionProviderException? cause,
        CancellationToken cancellationToken)
    {
        try
        {
            return await TryTranscribeAsync(fallback, request, true, cause, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TranscriptionProviderException exception)
        {
            throw ToPublicException(exception);
        }
    }

    private async Task<ProviderTranscriptionResult> TryTranscribeAsync(
        ISpeechTranscriptionProvider provider,
        TranscriptionRequest request,
        bool fallbackUsed,
        TranscriptionProviderException? fallbackCause,
        CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await provider.TranscribeAsync(request, cancellationToken);
            if (string.IsNullOrWhiteSpace(result.Text))
            {
                throw new TranscriptionProviderException(
                    TranscriptionFailureKind.Technical,
                    provider.ProviderName,
                    "The transcription provider returned no text.");
            }

            logger.LogInformation(
                "{Event} {TranscriptionRequestId} {Provider} {Model} {Success} {LatencyMilliseconds} {AudioBytes} {ClientDurationMilliseconds} {KeytermCount} {FallbackUsed} {FallbackReason} {ExternalStatusCode}",
                "aegis_stt_attempt",
                request.TranscriptionRequestId,
                result.Provider,
                result.Model,
                true,
                stopwatch.ElapsedMilliseconds,
                request.Audio.Length,
                request.ClientDurationMilliseconds,
                provider.KeytermCount,
                fallbackUsed,
                fallbackCause?.Kind.ToString(),
                null);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TranscriptionProviderException exception)
        {
            logger.LogWarning(
                "{Event} {TranscriptionRequestId} {Provider} {Model} {Success} {LatencyMilliseconds} {AudioBytes} {ClientDurationMilliseconds} {KeytermCount} {FallbackUsed} {FallbackReason} {ExternalStatusCode}",
                "aegis_stt_attempt",
                request.TranscriptionRequestId,
                provider.ProviderName,
                provider.Model,
                false,
                stopwatch.ElapsedMilliseconds,
                request.Audio.Length,
                request.ClientDurationMilliseconds,
                provider.KeytermCount,
                fallbackUsed,
                fallbackCause?.Kind.ToString(),
                exception.ExternalStatusCode);
            throw;
        }
    }

    private ISpeechTranscriptionProvider? FindProvider(string providerName) =>
        !string.IsNullOrWhiteSpace(providerName) && providersByName.TryGetValue(providerName, out var provider)
            ? provider
            : null;

    private static TranscriptionRequestException ToPublicException(TranscriptionProviderException exception) =>
        exception.Kind switch
        {
            TranscriptionFailureKind.InvalidInput => new TranscriptionRequestException(400, "A gravação de voz é inválida."),
            TranscriptionFailureKind.Timeout => new TranscriptionRequestException(504, "A transcrição excedeu o tempo disponível."),
            _ => new TranscriptionRequestException(503, "A transcrição por voz não está disponível.")
        };
}

public interface ITranscriptionSettings
{
    bool Enabled { get; }
    string PrimaryProvider { get; }
    bool FallbackEnabled { get; }
    string FallbackProvider { get; }
    int MaxAudioBytes { get; }
    int MaxRecordingSeconds { get; }
}
