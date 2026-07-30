using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Aegis.Application.Voice.Transcription;
using Microsoft.Extensions.Options;

namespace Aegis.Infrastructure.Voice.Transcription;

public sealed class OpenAiTranscriptionClient(
    HttpClient httpClient,
    IOptions<OpenAiSttOptions> options,
    IOptions<SttOptions> sttOptions) : ISpeechTranscriptionProvider
{
    private const string FidelityPrompt = "Transcreva literalmente em português brasileiro. Preserve hesitações, repetições, falsos começos, autocorreções, negações, números, datas, horários e termos técnicos. Não resuma, não reescreva e não complete frases.";

    public string ProviderName => "openai";
    public string Model => options.Value.Model;
    public int KeytermCount => 0;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.Value.ApiKey);

    public async Task<ProviderTranscriptionResult> TranscribeAsync(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            throw new TranscriptionProviderException(TranscriptionFailureKind.Technical, ProviderName, "OpenAI STT is not configured.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, sttOptions.Value.TimeoutSeconds)));
        using var form = new MultipartFormDataContent();
        var audio = new ByteArrayContent(request.Audio);
        audio.Headers.ContentType = MediaTypeHeaderValue.Parse(request.ContentType);
        form.Add(audio, "file", request.FileName);
        form.Add(new StringContent(Model), "model");
        form.Add(new StringContent(ToOpenAiLanguageCode(sttOptions.Value.Language)), "language");
        form.Add(new StringContent(FidelityPrompt), "prompt");
        form.Add(new StringContent("json"), "response_format");

        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/audio/transcriptions") { Content = form };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.ApiKey);
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
                    throw new TranscriptionProviderException(TranscriptionFailureKind.Technical, ProviderName, "OpenAI returned an empty transcript.", (int)response.StatusCode);
                }

                return new ProviderTranscriptionResult(text.Trim(), ProviderName, Model);
            }
            catch (JsonException exception)
            {
                throw new TranscriptionProviderException(TranscriptionFailureKind.Technical, ProviderName, "OpenAI returned invalid JSON.", (int)response.StatusCode, exception);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new TranscriptionProviderException(TranscriptionFailureKind.Timeout, ProviderName, "OpenAI transcription timed out.", null, exception);
        }
        catch (HttpRequestException exception)
        {
            throw new TranscriptionProviderException(TranscriptionFailureKind.Technical, ProviderName, "OpenAI transcription failed.", null, exception);
        }
    }

    private static TranscriptionProviderException CreateFailure(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.BadRequest
            ? new TranscriptionProviderException(TranscriptionFailureKind.InvalidInput, "openai", "OpenAI rejected the audio.", (int)statusCode)
            : new TranscriptionProviderException(TranscriptionFailureKind.Technical, "openai", "OpenAI transcription failed.", (int)statusCode);

    private static string ToOpenAiLanguageCode(string language) =>
        language.Trim().ToLowerInvariant() switch
        {
            "por" or "pt-br" or "pt" => "pt",
            _ => language.Trim()
        };
}
