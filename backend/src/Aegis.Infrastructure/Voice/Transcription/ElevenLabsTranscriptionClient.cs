using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Aegis.Application.Voice.Transcription;
using Microsoft.Extensions.Options;

namespace Aegis.Infrastructure.Voice.Transcription;

public sealed class ElevenLabsTranscriptionClient(
    HttpClient httpClient,
    IOptions<ElevenLabsSttOptions> options,
    IOptions<SttOptions> sttOptions,
    ISttKeytermProvider keytermProvider) : ISpeechTranscriptionProvider
{
    public string ProviderName => "elevenlabs";
    public string Model => options.Value.Model;
    public int KeytermCount => keytermProvider.GetKeyterms().Count;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.Value.ApiKey);

    public async Task<ProviderTranscriptionResult> TranscribeAsync(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            throw new TranscriptionProviderException(TranscriptionFailureKind.Technical, ProviderName, "ElevenLabs STT is not configured.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, sttOptions.Value.TimeoutSeconds)));
        using var form = new MultipartFormDataContent();
        var audio = new ByteArrayContent(request.Audio);
        audio.Headers.ContentType = MediaTypeHeaderValue.Parse(request.ContentType);
        form.Add(audio, "file", request.FileName);
        form.Add(new StringContent(Model), "model_id");
        form.Add(new StringContent(ToElevenLabsLanguageCode(sttOptions.Value.Language)), "language_code");
        form.Add(new StringContent("false"), "no_verbatim");
        form.Add(new StringContent("false"), "tag_audio_events");
        form.Add(new StringContent("none"), "timestamps_granularity");
        foreach (var keyterm in keytermProvider.GetKeyterms())
        {
            form.Add(new StringContent(keyterm), "keyterms");
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/speech-to-text") { Content = form };
        message.Headers.Add("xi-api-key", options.Value.ApiKey);
        try
        {
            using var response = await httpClient.SendAsync(message, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw CreateFailure(response.StatusCode);
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                var text = document.RootElement.TryGetProperty("text", out var textElement) ? textElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(text))
                {
                    throw new TranscriptionProviderException(TranscriptionFailureKind.Technical, ProviderName, "ElevenLabs returned an empty transcript.", (int)response.StatusCode);
                }

                return new ProviderTranscriptionResult(text.Trim(), ProviderName, Model);
            }
            catch (JsonException exception)
            {
                throw new TranscriptionProviderException(TranscriptionFailureKind.Technical, ProviderName, "ElevenLabs returned invalid JSON.", (int)response.StatusCode, exception);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new TranscriptionProviderException(TranscriptionFailureKind.Timeout, ProviderName, "ElevenLabs transcription timed out.", null, exception);
        }
        catch (HttpRequestException exception)
        {
            throw new TranscriptionProviderException(TranscriptionFailureKind.Technical, ProviderName, "ElevenLabs transcription failed.", null, exception);
        }
    }

    private static TranscriptionProviderException CreateFailure(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.BadRequest
            ? new TranscriptionProviderException(TranscriptionFailureKind.InvalidInput, "elevenlabs", "ElevenLabs rejected the audio.", (int)statusCode)
            : new TranscriptionProviderException(TranscriptionFailureKind.Technical, "elevenlabs", "ElevenLabs transcription failed.", (int)statusCode);

    private static string ToElevenLabsLanguageCode(string language) =>
        language.Trim().ToLowerInvariant() switch
        {
            "pt" or "pt-br" or "por" => "por",
            _ => language.Trim()
        };
}
