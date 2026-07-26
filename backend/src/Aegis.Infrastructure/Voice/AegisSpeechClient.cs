using Aegis.Application.Voice;
using Aegis.Application.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Aegis.Infrastructure.Voice;

public sealed class AegisSpeechClient(
    HttpClient httpClient,
    IOptions<AegisTtsOptions> options,
    ILogger<AegisSpeechClient> logger,
    AegisMetrics metrics) : IAegisSpeechClient
{
    private const int SampleRate = 24000;
    private const int Channels = 1;
    private const string PcmFormat = "pcm_s16le";

    public async Task<SpeechStreamResponse> StreamSpeechAsync(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        metrics.TtsRequests.Add(1);
        if (!settings.Enabled)
        {
            throw new InvalidOperationException("Speech is disabled.");
        }

        var payload = new
        {
            request_id = request.NativeRequestId,
            text = request.Text,
            priority = Math.Clamp(settings.DefaultPriority, 0, 100),
            interrupt_policy = "enqueue",
            stream = true,
            response_format = "pcm",
            voice_profile = settings.Profile,
            metadata = new
            {
                conversation_id = request.ConversationId,
                source = "aegis",
                trace_id = request.TurnId
            }
        };

        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, settings.ConnectTimeoutSeconds)));
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/aegis/speech")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        ApplyToken(message, settings);
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, connectTimeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            metrics.TtsFailures.Add(1);
            throw new TimeoutException("Timed out while connecting to the speech service.", exception);
        }
        catch (Exception)
        {
            metrics.TtsFailures.Add(1);
            throw;
        }

        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            metrics.TtsFailures.Add(1);
            throw new HttpRequestException("The speech service did not accept the request.", null, response.StatusCode);
        }

        try
        {
            var format = ValidateHeaders(response, settings.Profile);
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            logger.LogInformation("{Event} {TurnId} {SpeechRequestId}", "aegis_speech_started", request.TurnId, request.SpeechRequestId);
            return new ResponseOwnedSpeechStreamResponse(
                response,
                new TimedReadStream(
                    stream,
                    TimeSpan.FromSeconds(Math.Max(1, settings.FirstAudioTimeoutSeconds)),
                    TimeSpan.FromSeconds(Math.Max(1, settings.IdleStreamTimeoutSeconds))),
                format);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public async Task CancelAsync(string nativeSpeechRequestId, CancellationToken cancellationToken = default)
    {
        if (!options.Value.Enabled || string.IsNullOrWhiteSpace(nativeSpeechRequestId)) return;
        metrics.TtsCancellations.Add(1);
        using var message = new HttpRequestMessage(HttpMethod.Delete, $"v1/aegis/speech/{Uri.EscapeDataString(nativeSpeechRequestId)}");
        ApplyToken(message, options.Value);
        try
        {
            using var response = await httpClient.SendAsync(message, cancellationToken);
            if (response.StatusCode is not HttpStatusCode.NotFound && !response.IsSuccessStatusCode)
            {
                logger.LogWarning("{Event} {StatusCode}", "aegis_tts_cancel_failed", (int)response.StatusCode);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "{Event}", "aegis_tts_cancel_unavailable");
        }
    }

    public async Task<SpeechServiceStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (!settings.Enabled) return new SpeechServiceStatus(false, false, settings.Profile, SampleRate, Channels);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, settings.ConnectTimeoutSeconds)));
        using var message = new HttpRequestMessage(HttpMethod.Get, "ready");
        ApplyToken(message, settings);
        try
        {
            using var response = await httpClient.SendAsync(message, timeout.Token);
            return new SpeechServiceStatus(true, response.IsSuccessStatusCode, settings.Profile, SampleRate, Channels);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new SpeechServiceStatus(true, false, settings.Profile, SampleRate, Channels);
        }
    }

    private static SpeechAudioFormat ValidateHeaders(HttpResponseMessage response, string profile)
    {
        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(contentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase) ||
            Header(response, "X-Aegis-Audio-Format") != PcmFormat ||
            Header(response, "X-Aegis-Sample-Rate") != SampleRate.ToString() ||
            Header(response, "X-Aegis-Channels") != Channels.ToString())
        {
            throw new InvalidOperationException("The speech service returned an incompatible audio stream.");
        }

        var actualProfile = Header(response, "X-Aegis-Voice-Profile");
        if (string.IsNullOrWhiteSpace(actualProfile) || !actualProfile.StartsWith(profile, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The speech service returned an unexpected voice profile.");
        }

        return new SpeechAudioFormat(PcmFormat, SampleRate, Channels, actualProfile);
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static void ApplyToken(HttpRequestMessage message, AegisTtsOptions settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ApiToken))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiToken);
        }
    }

    private sealed class ResponseOwnedSpeechStreamResponse(HttpResponseMessage response, Stream stream, SpeechAudioFormat format)
        : SpeechStreamResponse(stream, format)
    {
        public override async ValueTask DisposeAsync()
        {
            await Stream.DisposeAsync();
            response.Dispose();
        }
    }

    private sealed class TimedReadStream(Stream inner, TimeSpan firstAudioTimeout, TimeSpan idleTimeout) : Stream
    {
        private bool receivedAudio;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(receivedAudio ? idleTimeout : firstAudioTimeout);
            try
            {
                var read = await inner.ReadAsync(buffer, timeout.Token);
                if (read > 0) receivedAudio = true;
                return read;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(receivedAudio
                    ? "Speech stream became idle."
                    : "Speech stream produced no audio.");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
