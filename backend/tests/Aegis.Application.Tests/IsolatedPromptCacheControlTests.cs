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
public sealed class IsolatedPromptCacheControlTests
{
    private const string InputJson = "[{\"id\":\"developer-1\",\"role\":\"developer\",\"content\":[{\"type\":\"input_text\",\"text\":\"Identidade àé e números 42.\",\"prompt_cache_breakpoint\":{\"mode\":\"explicit\"}}]},{\"role\":\"developer\",\"content\":[{\"type\":\"input_text\",\"text\":\"Segunda instrução.\",\"prompt_cache_breakpoint\":{\"mode\":\"explicit\"}}]},{\"id\":\"user-1\",\"role\":\"user\",\"content\":\"pedido real: {\\\"prompt_cache_breakpoint\\\":123}\"},{\"type\":\"function_call\",\"id\":\"function-1\",\"call_id\":\"call-1\",\"name\":\"echo\",\"arguments\":\"{\\\"prompt_cache_breakpoint\\\":7,\\\"number\\\":1.23456789}\"},{\"type\":\"function_call_output\",\"id\":\"output-1\",\"call_id\":\"call-1\",\"output\":\"{\\\"prompt_cache_breakpoint\\\":true,\\\"value\\\":null}\"}]";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IsolatedControlOnlyRemovesCacheHintsAndNeverMutatesInput(bool streaming)
    {
        using var document = JsonDocument.Parse(InputJson);
        var items = document.RootElement.EnumerateArray().ToArray();
        var original = JsonSerializer.Serialize(items);
        var auditItems = JsonDocument.Parse(InputJson.Replace("pedido real", "pedido redigido")).RootElement.Clone().EnumerateArray().ToArray();
        var result = await Run(true, "hands_free", streaming, items, auditItems);
        using var body = JsonDocument.Parse(result.Body);
        using var audit = JsonDocument.Parse(result.Audit);
        var expectedJson = InputJson.Replace(",\"prompt_cache_breakpoint\":{\"mode\":\"explicit\"}", "");
        using var expected = JsonDocument.Parse(expectedJson);
        Assert.Equal("explicit", body.RootElement.GetProperty("prompt_cache_options").GetProperty("mode").GetString());
        Assert.Equal(JsonSerializer.Serialize(expected.RootElement), JsonSerializer.Serialize(body.RootElement.GetProperty("input")));
        Assert.Equal(original, JsonSerializer.Serialize(items));
        Assert.Equal("pedido real: {\"prompt_cache_breakpoint\":123}", body.RootElement.GetProperty("input")[2].GetProperty("content").GetString());
        Assert.Equal("{\"prompt_cache_breakpoint\":7,\"number\":1.23456789}", body.RootElement.GetProperty("input")[3].GetProperty("arguments").GetString());
        Assert.Contains("pedido redigido", audit.RootElement.GetProperty("input")[2].GetProperty("content").GetString());
        Assert.DoesNotContain("pedido real", result.Audit);
        Assert.Equal("ok", result.Content);
        Assert.Equal(1, result.EffectiveCount);
        Assert.Equal("default", body.RootElement.GetProperty("service_tier").GetString());
        Assert.Equal("low", body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OffAndNonHandsFreeKeepWireAndHintsIdentical(bool streaming)
    {
        using var document = JsonDocument.Parse(InputJson);
        var items = document.RootElement.EnumerateArray().ToArray();
        foreach (var origin in new string?[] { null, "ordinary" })
        {
            var off = await Run(false, origin, streaming, items);
            var on = await Run(true, origin, streaming, items);
            Assert.Equal(off.Body, on.Body);
            Assert.Equal(off.Audit, on.Audit);
            Assert.Equal(0, on.EffectiveCount);
        }
        var hf = await Run(false, "hands_free", streaming, items);
        using var body = JsonDocument.Parse(hf.Body);
        Assert.Equal("implicit", body.RootElement.GetProperty("prompt_cache_options").GetProperty("mode").GetString());
        Assert.Equal(JsonSerializer.Serialize(items), JsonSerializer.Serialize(body.RootElement.GetProperty("input")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductionGuardKeepsIdenticalPayloadEvenWithBothFlagsAndSectionOverride(bool streaming)
    {
        using var document = JsonDocument.Parse(InputJson);
        var items = document.RootElement.EnumerateArray().ToArray();
        var off = await Run(false, "hands_free", streaming, items, environment: "Production");
        var on = await Run(true, "hands_free", streaming, items, environment: "Production");
        Assert.Equal(off.Body, on.Body);
        Assert.Equal(0, on.EffectiveCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlainStringInputPreservesTypeAndDisabledProfilingEmitsNoControl(bool streaming)
    {
        var result = await Run(true, "hands_free", streaming, profile: false);
        using var body = JsonDocument.Parse(result.Body);
        Assert.Equal(JsonValueKind.String, body.RootElement.GetProperty("input").ValueKind);
        Assert.Equal("pedido", body.RootElement.GetProperty("input").GetString());
        Assert.Null(result.EffectiveCount);
    }

    [Theory]
    [InlineData(true, true, "Development", true)]
    [InlineData(true, false, "Development", false)]
    [InlineData(false, true, "Development", false)]
    [InlineData(true, true, "Production", false)]
    [InlineData(true, true, "Staging", false)]
    [InlineData(true, true, null, false)]
    public void TestControlRequiresExplicitFlagIsolatedAndDevelopmentDespiteSectionOverride(
        bool flag, bool isolated, string? environment, bool expected) =>
        Assert.Equal(expected, Settings(flag, isolated, environment).LatencyNoPromptCache);

    private static OpenAIOptions Settings(bool flag, bool isolated = true, string? environment = "Development")
    {
        var values = new Dictionary<string, string?> { ["ConnectionStrings:AegisDatabase"] = "Host=unused;Database=unused",
            ["OPENAI_API_KEY"] = "fake-key", ["AEGIS_OPENAI_SERVICE_TIER"] = "default",
            ["Aegis:LatencyNoPromptCache"] = "true", ["AEGIS_LATENCY_NO_PROMPT_CACHE"] = flag.ToString(),
            ["AEGIS_MEMORY_EVAL_ISOLATED"] = isolated.ToString(), ["ASPNETCORE_ENVIRONMENT"] = environment };
        using var services = new ServiceCollection().AddInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(values).Build()).BuildServiceProvider();
        return services.GetRequiredService<IOptions<OpenAIOptions>>().Value;
    }

    private static async Task<(string Body, string Audit, string Content, long? EffectiveCount)> Run(
        bool flag, string? origin, bool streaming, JsonElement[]? input = null, JsonElement[]? auditInput = null,
        string environment = "Development", bool profile = true)
    {
        var previous = Environment.GetEnvironmentVariable("AEGIS_LATENCY_PROFILE");
        Environment.SetEnvironmentVariable("AEGIS_LATENCY_PROFILE", profile ? "1" : null);
        try
        {
            var handler = new Handler();
            using var http = new HttpClient(handler) { BaseAddress = new("https://example.invalid/") };
            using var metrics = new AegisMetrics();
            var logger = new Logger();
            var model = new OpenAIResponsesClient(http, Options.Create(Settings(flag, environment: environment)), metrics, logger);
            var request = new ModelToolRequest(new ModelRequest("policy", "pedido", Metadata: origin is null ? null :
                new Dictionary<string, string> { ["origin"] = origin }, InputItems: input, AuditInputItems: auditInput), []);
            string content = "", audit = "";
            using (TurnLatencyProfile.Begin(Guid.NewGuid(), logger))
            {
                if (streaming)
                {
                    await foreach (var chunk in model.RespondWithToolsStreamAsync(request))
                    { content += chunk.Content; if (chunk.AuditData is { } data) audit = data.RequestPayloadJson; }
                }
                else
                {
                    var response = await model.RespondWithToolsAsync(request);
                    content = response.Content; audit = response.AuditData.RequestPayloadJson;
                }
            }
            return (handler.Body!, audit, content, logger.EffectiveCount);
        }
        finally { Environment.SetEnvironmentVariable("AEGIS_LATENCY_PROFILE", previous); }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(Body);
            var streaming = document.RootElement.GetProperty("stream").GetBoolean();
            const string response = "{\"id\":\"response-1\",\"model\":\"gpt-6-luna\",\"output_text\":\"ok\",\"output\":[]}";
            return new(HttpStatusCode.OK) { Content = new StringContent(streaming
                ? "data: {\"type\":\"response.completed\",\"response\":" + response + "}\n\ndata: [DONE]\n\n" : response,
                Encoding.UTF8, streaming ? "text/event-stream" : "application/json") };
        }
    }

    private sealed class Logger : ILogger<OpenAIResponsesClient>
    {
        public long? EffectiveCount;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> values) return;
            var fields = values.ToDictionary(item => item.Key, item => item.Value);
            if (fields.TryGetValue("Stage", out var stage) && stage is "model.isolated_no_prompt_cache") EffectiveCount = fields["Count"] as long?;
        }
    }
}
