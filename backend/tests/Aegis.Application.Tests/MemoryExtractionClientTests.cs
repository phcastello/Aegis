using System.Net;
using System.Text;
using System.Text.Json;
using Aegis.Application.Memory;
using Aegis.Application.Observability;
using Aegis.Infrastructure.Memory;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class MemoryExtractionClientTests
{
    [Fact]
    public async Task ResponsesRequestUsesStrictSchemaStablePolicyAndStoreFalse()
    {
        var handler = new CaptureHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/") };
        using var metrics = new AegisMetrics();
        var client = new OpenAiMemoryExtractionClient(http,
            new MemoryAutomaticOptions { ApiKey = "test_key", Model = "fake_extractor" }, metrics);
        var input = new MemoryExtractionInput("Prefiro backend.", [new("assistant", "Contexto anterior")], [], [], ["PREFERS"]);
        var output = await client.ExtractAsync(input, default);
        Assert.Empty(output.Candidates);
        Assert.Equal("/v1/responses", handler.Path);
        using var payload = JsonDocument.Parse(handler.Body!);
        var root = payload.RootElement;
        Assert.False(root.GetProperty("store").GetBoolean());
        Assert.Equal("fake_extractor", root.GetProperty("model").GetString());
        Assert.False(root.TryGetProperty("tools", out _));
        var format = root.GetProperty("text").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.True(format.GetProperty("strict").GetBoolean());
        Assert.Equal("explicit", root.GetProperty("input")[0].GetProperty("content")[0]
            .GetProperty("prompt_cache_breakpoint").GetProperty("mode").GetString());
        Assert.DoesNotContain("Prefiro backend", root.GetProperty("input")[0].GetRawText());
        Assert.Contains("Prefiro backend", root.GetProperty("input")[1].GetRawText());
    }

    [Fact]
    public async Task StructuredCandidateParsesTemporalAndRelationFields()
    {
        var structured = JsonSerializer.Serialize(new
        {
            candidates = new[] { new
            {
                content = "Sakamoto namora Bisky.", action = "transition", existingMemoryRef = "m1",
                transitionAt = "2026-09-28T12:00:00Z", validFrom = (string?)null, validUntil = (string?)null,
                entities = new[] { new { key = "s", mention = "Sakamoto", canonicalName = "Sakamoto",
                    entityType = "PERSON", aliases = Array.Empty<string>() } },
                relations = new[] { new { action = "create", existingRelationRef = (string?)null,
                    subjectKey = "s", predicate = "DATES", objectKey = "b", validFrom = (string?)null,
                    validUntil = (string?)null, closeAt = (string?)null } }
            } }
        });
        var handler = new CaptureHandler(structured);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/") };
        using var metrics = new AegisMetrics();
        var output = await new OpenAiMemoryExtractionClient(http, new MemoryAutomaticOptions { ApiKey = "test_key" }, metrics)
            .ExtractAsync(new("Sakamoto agora namora Bisky.", [], [], [], []), default);
        var candidate = Assert.Single(output.Candidates);
        Assert.Equal("transition", candidate.Action);
        Assert.Equal("m1", candidate.ExistingMemoryRef);
        Assert.Equal(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero), candidate.TransitionAt);
        Assert.Equal("DATES", Assert.Single(candidate.Relations).Predicate);
    }

    private sealed class CaptureHandler(string structuredOutput = "{\"candidates\":[]}") : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public string? Path { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = await request.Content!.ReadAsStringAsync(ct);
            Path = request.RequestUri?.AbsolutePath;
            var response = JsonSerializer.Serialize(new
            {
                status = "completed", output = new[] { new { content = new[] { new { type = "output_text", text = structuredOutput } } } },
                usage = new { input_tokens = 10, output_tokens = 2 }
            });
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
