using System.Net;
using System.Text;
using System.Text.Json;
using Aegis.Application.Models;
using Aegis.Application.Observability;
using Aegis.Infrastructure;
using Aegis.Infrastructure.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aegis.Application.Tests;

[Collection("LatencyProfileEnvironment")]
public sealed class OpenAiHttpVersionTests
{
    [Theory]
    [InlineData(false, false, 1, 1)]
    [InlineData(true, false, 2, 0)]
    [InlineData(true, false, 1, 1)]
    [InlineData(false, true, 1, 1)]
    [InlineData(true, true, 2, 0)]
    [InlineData(true, true, 1, 1)]
    public async Task OptInPrefersHttp2AndAcceptsHttp11FallbackInBothResponsePaths(
        bool preferHttp2, bool streaming, int responseMajor, int responseMinor)
    {
        var result = await Run(preferHttp2, streaming, new(responseMajor, responseMinor));
        Assert.Equal(preferHttp2 ? HttpVersion.Version20 : HttpVersion.Version11, result.Request.Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionOrLower, result.Request.Policy);
        Assert.Equal("Bearer fake-key", result.Request.Authorization);
        Assert.Equal("application/json; charset=utf-8", result.Request.ContentType);
        Assert.Equal("https://example.invalid/v1/responses", result.Request.Uri);
        Assert.Single(result.Marks, item => item.Stage == "model.http_version_major" && item.Count == responseMajor);
        Assert.Single(result.Marks, item => item.Stage == "model.http_version_minor" && item.Count == responseMinor);
        Assert.Contains("Resposta completa.", result.Response);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyTransportPreferenceChangesAndOffLeavesNoProfiling(bool streaming)
    {
        var baseline = await Run(false, streaming, HttpVersion.Version11, profile: false);
        var http2 = await Run(true, streaming, HttpVersion.Version20, profile: false);
        Assert.Equal(baseline.Request.Body, http2.Request.Body);
        Assert.Equal(baseline.Request.Authorization, http2.Request.Authorization);
        Assert.Equal(baseline.Request.ContentType, http2.Request.ContentType);
        Assert.Equal(baseline.Request.Uri, http2.Request.Uri);
        Assert.Equal(baseline.Response, http2.Response);
        Assert.Empty(baseline.Marks);
        Assert.Empty(http2.Marks);
    }

    [Theory]
    [InlineData(1, 0, false)]
    [InlineData(1, 1, true)]
    [InlineData(2, 0, true)]
    [InlineData(3, 0, true)]
    [InlineData(4, 9, false)]
    public async Task ProtocolProfilingAcceptsOnlyKnownEffectiveVersions(int major, int minor, bool recorded)
    {
        var result = await Run(true, true, new(major, minor));
        Assert.Equal(recorded ? 2 : 0, result.Marks.Count(item => item.Stage.StartsWith("model.http_version_")));
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("true", null, true)]
    [InlineData("false", "true", false)]
    [InlineData(null, "true", true)]
    [InlineData("invalid", null, false)]
    public void Http2UsesExistingBooleanConfigurationRulesAndDefaultsOff(string? environment, string? section, bool expected)
    {
        var values = new Dictionary<string, string?> { ["ConnectionStrings:AegisDatabase"] = "Host=unused;Database=unused",
            ["AEGIS_OPENAI_HTTP2"] = environment, ["Aegis:PreferHttp2"] = section };
        using var services = new ServiceCollection()
            .AddInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(values).Build()).BuildServiceProvider();
        Assert.Equal(expected, services.GetRequiredService<IOptions<OpenAIOptions>>().Value.PreferHttp2);
    }

    private static async Task<(CapturedRequest Request, string Response, List<Mark> Marks)> Run(
        bool preferHttp2, bool streaming, Version effectiveVersion, bool profile = true)
    {
        var previous = Environment.GetEnvironmentVariable("AEGIS_LATENCY_PROFILE");
        Environment.SetEnvironmentVariable("AEGIS_LATENCY_PROFILE", profile ? "1" : null);
        try
        {
            var handler = new Handler(streaming, effectiveVersion);
            using var http = new HttpClient(handler) { BaseAddress = new("https://example.invalid/") };
            using var metrics = new AegisMetrics();
            var logger = new Logger();
            var model = new OpenAIResponsesClient(http, Options.Create(new OpenAIOptions { ApiKey = "fake-key", PreferHttp2 = preferHttp2 }), metrics, logger);
            var request = new ModelToolRequest(new ModelRequest("Identidade preservada.", "Pedido preservado."), []);
            string result;
            using (TurnLatencyProfile.Begin(Guid.NewGuid(), logger))
            {
                if (streaming)
                {
                    var chunks = new List<ModelToolStreamChunk>();
                    await foreach (var chunk in model.RespondWithToolsStreamAsync(request))
                        chunks.Add(chunk with { AuditData = chunk.AuditData is { } audit ? audit with { DurationMilliseconds = 0 } : null });
                    result = JsonSerializer.Serialize(chunks);
                }
                else
                {
                    var response = await model.RespondWithToolsAsync(request);
                    result = JsonSerializer.Serialize(response with { AuditData = response.AuditData with { DurationMilliseconds = 0 } });
                }
            }
            return (handler.Captured!, result, logger.Marks);
        }
        finally { Environment.SetEnvironmentVariable("AEGIS_LATENCY_PROFILE", previous); }
    }

    private sealed record CapturedRequest(string Body, Version Version, HttpVersionPolicy Policy, string? Authorization, string? ContentType, string? Uri);
    private sealed class Handler(bool streaming, Version effectiveVersion) : HttpMessageHandler
    {
        public CapturedRequest? Captured;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Captured = new(await request.Content!.ReadAsStringAsync(cancellationToken), request.Version, request.VersionPolicy,
                request.Headers.Authorization?.ToString(), request.Content.Headers.ContentType?.ToString(), request.RequestUri?.ToString());
            const string response = "{\"id\":\"response-1\",\"model\":\"gpt-6-luna\",\"status\":\"completed\",\"output_text\":\"Resposta completa.\",\"output\":[]}";
            return new(HttpStatusCode.OK) { Version = effectiveVersion, Content = new StringContent(
                streaming ? "data: {\"type\":\"response.completed\",\"response\":" + response + "}\n\ndata: [DONE]\n\n" : response,
                Encoding.UTF8, streaming ? "text/event-stream" : "application/json") };
        }
    }

    private sealed record Mark(string Stage, long? Count);
    private sealed class Logger : ILogger<OpenAIResponsesClient>
    {
        public readonly List<Mark> Marks = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> values) return;
            var fields = values.ToDictionary(item => item.Key, item => item.Value);
            if (fields.TryGetValue("Stage", out var stage) && stage is string name)
                Marks.Add(new(name, fields["Count"] as long?));
        }
    }
}
