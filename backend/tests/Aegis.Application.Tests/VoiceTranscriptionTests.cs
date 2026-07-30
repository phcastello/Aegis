using System.Net;
using System.Net.Http.Headers;
using Aegis.Api.Controllers;
using Aegis.Application.Turns;
using Aegis.Application.Voice;
using Aegis.Application.Voice.Transcription;
using Aegis.Infrastructure.Voice.Transcription;
using Aegis.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class VoiceTranscriptionTests
{
    [Fact]
    public void KeytermsAreNormalizedDeduplicatedAndCappedAtTheOperationalLimit()
    {
        var configured = string.Join(';', Enumerable.Range(0, 140).Select(index => $"Termo {index}").Append("  Aegis   Voice  ").Append("aegis voice"));
        var provider = CreateKeyterms(configured, 999);

        Assert.Equal(100, provider.GetKeyterms().Count);
        Assert.Contains("Aegis Voice", CreateKeyterms("  Aegis   Voice ;aegis voice", 80).GetKeyterms());
        Assert.Single(CreateKeyterms("  Aegis   Voice ;aegis voice", 80).GetKeyterms());
    }

    [Theory]
    [InlineData("Aegis<Voice")]
    [InlineData("este termo possui exatamente seis palavras agora")]
    public void InvalidKeytermsAreIgnored(string invalid)
    {
        var provider = CreateKeyterms($"válido;{invalid}", 80);
        Assert.Equal(new[] { "válido" }, provider.GetKeyterms());
    }

    [Fact]
    public void KeytermWithFiftyCharactersIsIgnored()
    {
        var provider = CreateKeyterms(new string('a', 50), 80);
        Assert.Empty(provider.GetKeyterms());
    }

    [Fact]
    public void DefaultKeytermLimitIsEighty()
    {
        var provider = CreateKeyterms(string.Join(';', Enumerable.Range(0, 90).Select(index => $"termo{index}")), 80);
        Assert.Equal(80, provider.GetKeyterms().Count);
    }

    [Fact]
    public async Task ElevenLabsIsUsedAsPrimaryWithoutCallingFallback()
    {
        var primary = FakeProvider.Success("elevenlabs", "Scribe", "texto principal");
        var fallback = FakeProvider.Success("openai", "gpt-4o-transcribe", "texto fallback");
        var service = CreateService(primary, fallback);

        var result = await service.TranscribeAsync(ValidRequest());

        Assert.Equal("texto principal", result.Text);
        Assert.Equal(1, primary.Calls);
        Assert.Equal(0, fallback.Calls);
    }

    [Theory]
    [InlineData(TranscriptionFailureKind.Timeout, null)]
    [InlineData(TranscriptionFailureKind.Technical, 429)]
    [InlineData(TranscriptionFailureKind.Technical, 503)]
    public async Task TechnicalPrimaryFailuresUseOpenAiFallback(TranscriptionFailureKind kind, int? statusCode)
    {
        var primary = FakeProvider.Failure("elevenlabs", kind, statusCode);
        var fallback = FakeProvider.Success("openai", "gpt-4o-transcribe", "texto fallback");
        var service = CreateService(primary, fallback);

        var result = await service.TranscribeAsync(ValidRequest());

        Assert.Equal("texto fallback", result.Text);
        Assert.Equal(1, fallback.Calls);
    }

    [Fact]
    public async Task EmptyPrimaryResponseUsesFallbackAndReceivesTheSameBytes()
    {
        var primary = FakeProvider.Success("elevenlabs", "scribe_v2", string.Empty);
        var fallback = FakeProvider.Success("openai", "gpt-4o-transcribe", "fallback");
        var request = ValidRequest();

        var result = await CreateService(primary, fallback).TranscribeAsync(request);

        Assert.Equal("fallback", result.Text);
        Assert.Equal(request.Audio, fallback.LastAudio);
    }

    [Fact]
    public async Task CancellationNeverUsesFallback()
    {
        var primary = FakeProvider.Cancel("elevenlabs");
        var fallback = FakeProvider.Success("openai", "gpt-4o-transcribe", "fallback");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateService(primary, fallback).TranscribeAsync(ValidRequest(), cancellation.Token));
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task InvalidAudioAndProviderBadRequestNeverUseFallback()
    {
        var primary = FakeProvider.Failure("elevenlabs", TranscriptionFailureKind.InvalidInput, 400);
        var fallback = FakeProvider.Success("openai", "gpt-4o-transcribe", "fallback");
        var service = CreateService(primary, fallback);

        var providerInputError = await Assert.ThrowsAsync<TranscriptionRequestException>(() => service.TranscribeAsync(ValidRequest()));
        await Assert.ThrowsAsync<TranscriptionRequestException>(() => service.TranscribeAsync(ValidRequest(contentType: "audio/flac")));
        Assert.Equal(400, providerInputError.StatusCode);
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task ClientsUseOnlyTheirDedicatedKeysAndTheOfficialEndpoints()
    {
        var elevenHandler = new RecordingHandler("{\"text\":\"onze\"}");
        var openAiHandler = new RecordingHandler("{\"text\":\"quatro\"}");
        var stt = Options.Create(new SttOptions());
        var eleven = new ElevenLabsTranscriptionClient(
            new HttpClient(elevenHandler) { BaseAddress = new Uri("https://api.elevenlabs.io/") },
            Options.Create(new ElevenLabsSttOptions { ApiKey = "eleven-key" }),
            stt,
            CreateKeyterms("Aegis", 80));
        var openAi = new OpenAiTranscriptionClient(
            new HttpClient(openAiHandler) { BaseAddress = new Uri("https://api.openai.com/") },
            Options.Create(new OpenAiSttOptions { ApiKey = "stt-openai-key" }),
            stt);

        await eleven.TranscribeAsync(ValidRequest());
        await openAi.TranscribeAsync(ValidRequest());

        Assert.Equal("/v1/speech-to-text", elevenHandler.RequestUri!.AbsolutePath);
        Assert.Equal("eleven-key", elevenHandler.ElevenKey);
        Assert.Equal("/v1/audio/transcriptions", openAiHandler.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer", openAiHandler.Authorization?.Scheme);
        Assert.Equal("stt-openai-key", openAiHandler.Authorization?.Parameter);
    }

    [Fact]
    public void SttOptionsNeverReadTheLlmOpenAiKey()
    {
        var noSttKeyConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:AegisDatabase"] = "Host=localhost;Database=aegis",
                ["OPENAI_API_KEY"] = "llm-key"
            })
            .Build();
        var noSttKeyServices = new ServiceCollection();
        noSttKeyServices.AddInfrastructure(noSttKeyConfiguration);
        using var noSttKeyProvider = noSttKeyServices.BuildServiceProvider();
        Assert.Equal(string.Empty, noSttKeyProvider.GetRequiredService<IOptions<OpenAiSttOptions>>().Value.ApiKey);

        var sttKeyConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:AegisDatabase"] = "Host=localhost;Database=aegis",
                ["OPENAI_API_KEY"] = "llm-key",
                ["AEGIS_STT_OPENAI_API_KEY"] = "stt-key"
            })
            .Build();
        var sttKeyServices = new ServiceCollection();
        sttKeyServices.AddInfrastructure(sttKeyConfiguration);
        using var provider = sttKeyServices.BuildServiceProvider();

        Assert.Equal("stt-key", provider.GetRequiredService<IOptions<OpenAiSttOptions>>().Value.ApiKey);
    }

    [Fact]
    public void StatusDoesNotInvokeAProvider()
    {
        var primary = FakeProvider.Success("elevenlabs", "scribe_v2", "ignored");
        var fallback = FakeProvider.Success("openai", "gpt-4o-transcribe", "ignored");
        var status = CreateService(primary, fallback).GetStatus();

        Assert.True(status.Enabled);
        Assert.True(status.Configured);
        Assert.Equal(0, primary.Calls + fallback.Calls);
    }

    [Fact]
    public void StatusEndpointDoesNotPerformPaidTranscription()
    {
        var primary = FakeProvider.Success("elevenlabs", "scribe_v2", "ignored");
        var controller = CreateController(CreateService(primary, null), 100);

        var response = controller.GetTranscriptionStatus();

        Assert.IsType<OkObjectResult>(response.Result);
        Assert.Equal(0, primary.Calls);
    }

    [Fact]
    public async Task EndpointRejectsOversizedAndUnsupportedAudio()
    {
        var service = CreateService(FakeProvider.Success("elevenlabs", "scribe_v2", "ignored"), null, maxBytes: 5);
        var controller = CreateController(service, 5);
        var oversized = FormFile(new byte[6], "audio/webm", "clip.webm");

        var tooLarge = await controller.Transcribe(oversized, Guid.NewGuid().ToString(), 1000, CancellationToken.None);
        var invalidFormat = await CreateController(service, 100).Transcribe(FormFile([1, 2], "audio/flac", "clip.flac"), Guid.NewGuid().ToString(), 1000, CancellationToken.None);

        Assert.Equal(413, Assert.IsType<ObjectResult>(tooLarge.Result).StatusCode);
        Assert.Equal(415, Assert.IsType<ObjectResult>(invalidFormat.Result).StatusCode);
    }

    [Fact]
    public async Task EndpointClearsTheInMemoryAudioBufferAfterTranscription()
    {
        var primary = FakeProvider.Success("elevenlabs", "scribe_v2", "texto");
        var response = await CreateController(CreateService(primary, null), 100)
            .Transcribe(FormFile([7, 8, 9], "audio/webm", "clip.webm"), Guid.NewGuid().ToString(), 1000, CancellationToken.None);

        Assert.IsType<OkObjectResult>(response.Result);
        Assert.NotNull(primary.AudioReference);
        Assert.All(primary.AudioReference!, value => Assert.Equal((byte)0, value));
    }

    private static SpeechTranscriptionService CreateService(
        FakeProvider primary,
        FakeProvider? fallback,
        int maxBytes = 20 * 1024 * 1024) =>
        new([primary, .. (fallback is null ? [] : new[] { fallback })], new SttOptions { MaxAudioBytes = maxBytes }, NullLogger<SpeechTranscriptionService>.Instance);

    private static SttKeytermProvider CreateKeyterms(string terms, int maximum) =>
        new(Options.Create(new SttOptions { Keyterms = terms, MaxKeyterms = maximum }), NullLogger<SttKeytermProvider>.Instance);

    private static TranscriptionRequest ValidRequest(string contentType = "audio/webm") =>
        new(Guid.NewGuid(), [1, 2, 3, 4], "clip.webm", contentType, 1000);

    private static VoiceController CreateController(ISpeechTranscriptionService service, int maxBytes)
    {
        var controller = new VoiceController(new FakeVoiceService(), service, Options.Create(new SttOptions { MaxAudioBytes = maxBytes }));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private static IFormFile FormFile(byte[] data, string contentType, string name)
    {
        var file = new FormFile(new MemoryStream(data), 0, data.Length, "audio", name) { Headers = new HeaderDictionary() };
        file.Headers.ContentType = contentType;
        return file;
    }

    private sealed class FakeProvider(string providerName, string model, Func<TranscriptionRequest, CancellationToken, Task<ProviderTranscriptionResult>> action) : ISpeechTranscriptionProvider
    {
        public int Calls { get; private set; }
        public byte[]? LastAudio { get; private set; }
        public byte[]? AudioReference { get; private set; }
        public string ProviderName { get; } = providerName;
        public string Model { get; } = model;
        public int KeytermCount => 0;
        public bool IsConfigured => true;

        public async Task<ProviderTranscriptionResult> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            AudioReference = request.Audio;
            LastAudio = request.Audio.ToArray();
            return await action(request, cancellationToken);
        }

        public static FakeProvider Success(string provider, string model, string text) => new(provider, model, (_, _) => Task.FromResult(new ProviderTranscriptionResult(text, provider, model)));
        public static FakeProvider Failure(string provider, TranscriptionFailureKind kind, int? status) => new(provider, "scribe_v2", (_, _) => Task.FromException<ProviderTranscriptionResult>(new TranscriptionProviderException(kind, provider, "failure", status)));
        public static FakeProvider Cancel(string provider) => new(provider, "scribe_v2", (_, token) => Task.FromCanceled<ProviderTranscriptionResult>(token));
    }

    private sealed class RecordingHandler(string responseJson) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? ElevenKey { get; private set; }
        public AuthenticationHeaderValue? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            ElevenKey = request.Headers.TryGetValues("xi-api-key", out var values) ? values.Single() : null;
            Authorization = request.Headers.Authorization;
            _ = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseJson) };
        }
    }

    private sealed class FakeVoiceService : IVoiceService
    {
        public Task<ActiveTurn> RegisterTurnAsync(Guid turnId, Guid conversationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VoiceStream> StartSpeechAsync(StartSpeechRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CancelTurnResult> CancelSpeechAsync(Guid speechRequestId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CancelTurnResult> CancelTurnAsync(Guid turnId, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CancelAllTurnsAsync(string reason, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool TryCompleteTurnWithoutSpeech(Guid turnId) => false;
        public void CompleteSpeech(Guid speechRequestId) { }
        public void FailSpeech(Guid speechRequestId) { }
        public Task<SpeechServiceStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new SpeechServiceStatus(false, false, "", 0, 0));
    }
}
