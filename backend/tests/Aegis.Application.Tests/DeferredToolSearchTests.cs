using System.Net;
using System.Text;
using System.Text.Json;
using Aegis.Application.Models;
using Aegis.Application.Observability;
using Aegis.Application.Tools;
using Aegis.Application.Voice;
using Aegis.Infrastructure;
using Aegis.Infrastructure.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aegis.Application.Tests;

[Collection("LatencyProfileEnvironment")]
public sealed class DeferredToolSearchTests
{
    [Fact]
    public async Task DefaultOffKeepsOriginalWireBytes()
    {
        var body = await Payload(false, false, null, [EchoDefinition]);
        Assert.Equal("{\"model\":\"gpt-6-luna\",\"input\":\"pedido\",\"reasoning\":{\"effort\":\"low\"},\"prompt_cache_options\":{\"mode\":\"implicit\"},\"stream\":false,\"max_output_tokens\":4000,\"store\":false,\"service_tier\":\"default\",\"metadata\":{\"aegis_version\":\"0.6.1\",\"purpose\":\"Chat\"},\"tools\":[{\"type\":\"function\",\"name\":\"echo\",\"description\":\"Echo\",\"parameters\":{\"type\":\"object\"},\"strict\":false}],\"parallel_tool_calls\":false,\"tool_choice\":\"auto\"}", body);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "ordinary")]
    [InlineData(true, "ordinary")]
    public async Task OrdinaryChatWireIsIdenticalEvenWhenFlagIsOn(bool streaming, string? origin)
    {
        var tools = new[] { EchoDefinition, FinalSpeechSegments.Definition };
        Assert.Equal(await Payload(false, streaming, origin, tools), await Payload(true, streaming, origin, tools));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyHandsFreeFunctionSchemasAreDeferredAndTerminalStaysEager(bool streaming)
    {
        var definitions = new[] { EchoDefinition, HandsFreeReply.Definition, FinalSpeechSegments.Definition };
        using var before = JsonDocument.Parse(await Payload(false, streaming, "hands_free", definitions));
        using var after = JsonDocument.Parse(await Payload(true, streaming, "hands_free", definitions));
        var oldTools = before.RootElement.GetProperty("tools").EnumerateArray().ToArray();
        var newTools = after.RootElement.GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal(oldTools.Length + 1, newTools.Length);
        foreach (var oldTool in oldTools)
        {
            var name = oldTool.GetProperty("name").GetString();
            var tool = Assert.Single(newTools, item => item.TryGetProperty("name", out var n) && n.GetString() == name);
            foreach (var property in new[] { "name", "description", "parameters", "strict" })
                Assert.Equal(oldTool.GetProperty(property).GetRawText(), tool.GetProperty(property).GetRawText());
            if (name == "echo") Assert.True(tool.GetProperty("defer_loading").GetBoolean());
            else Assert.False(tool.TryGetProperty("defer_loading", out _));
        }
        var search = Assert.Single(newTools, item => item.GetProperty("type").GetString() == "tool_search");
        Assert.Equal("server", search.GetProperty("execution").GetString());
        Assert.Equal(2, search.EnumerateObject().Count());
        Assert.Equal("default", after.RootElement.GetProperty("service_tier").GetString());
        Assert.False(after.RootElement.GetProperty("parallel_tool_calls").GetBoolean());
        Assert.Equal("auto", after.RootElement.GetProperty("tool_choice").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalOnlyFallbackHasNothingToDiscover(bool streaming)
    {
        using var payload = JsonDocument.Parse(await Payload(true, streaming, "hands_free", [FinalSpeechSegments.Definition]));
        var terminal = Assert.Single(payload.RootElement.GetProperty("tools").EnumerateArray());
        Assert.Equal(FinalSpeechSegments.ToolName, terminal.GetProperty("name").GetString());
        Assert.False(terminal.TryGetProperty("defer_loading", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostedItemsArePreservedAcrossTwoRoundsAndNeverBecomeExecutableCalls(bool streaming)
    {
        var result = await Loop(true, streaming);
        Assert.Equal("Resposta final preservada.", result.Speech);
        Assert.Equal("close", result.Continuation);
        Assert.Equal(2, result.Requests.Count);
        Assert.Equal(1, result.ToolAttempts);
        Assert.Equal(1, result.ToolEffects);
        using var second = JsonDocument.Parse(result.Requests[1]);
        var items = second.RootElement.GetProperty("input").EnumerateArray().ToArray();
        var types = items.Where(item => item.TryGetProperty("type", out _)).Select(item => item.GetProperty("type").GetString()).ToArray();
        Assert.Equal(new[] { "tool_search_call", "tool_search_output", "function_call", "function_call_output" }, types);
        var loaded = Assert.Single(items, item => item.TryGetProperty("type", out var t) && t.GetString() == "tool_search_output");
        Assert.Equal("server", loaded.GetProperty("execution").GetString());
        Assert.True(loaded.GetProperty("call_id").ValueKind == JsonValueKind.Null);
        Assert.Equal("echo", loaded.GetProperty("tools")[0].GetProperty("name").GetString());
        Assert.Equal(new long?[] { 1, 0 }, result.SearchCounts);
        Assert.All(result.Requests, request =>
        {
            using var document = JsonDocument.Parse(request);
            Assert.Equal("default", document.RootElement.GetProperty("service_tier").GetString());
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeferredDiscoveryDoesNotBypassRegistryOrToolAuthorization(bool streaming)
    {
        var denied = await Loop(true, streaming, authorized: false);
        Assert.Equal(1, denied.ToolAttempts);
        Assert.Equal(0, denied.ToolEffects);
        var unknown = await Loop(true, streaming, unknown: true);
        Assert.Equal(0, unknown.ToolAttempts);
        Assert.Equal(0, unknown.ToolEffects);
        using var request = JsonDocument.Parse(unknown.Requests[1]);
        Assert.Contains(request.RootElement.GetProperty("input").EnumerateArray(), item =>
            item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output" &&
            item.GetProperty("output").GetString()!.Contains("unknown_tool"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultAndDeferredPathsKeepFinalSpeechAndNoProfileWhenDisabled(bool streaming)
    {
        var eager = await Loop(false, streaming, profile: false);
        var deferred = await Loop(true, streaming, profile: false);
        Assert.Equal(eager.Speech, deferred.Speech);
        Assert.Equal(eager.Continuation, deferred.Continuation);
        Assert.Empty(eager.SearchCounts);
        Assert.Empty(deferred.SearchCounts);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("invalid", false)]
    public void DeferredToolsUsesExistingBooleanConfigurationAndDefaultsOff(string? value, bool expected)
    {
        var values = new Dictionary<string, string?> { ["ConnectionStrings:AegisDatabase"] = "Host=unused;Database=unused",
            ["AEGIS_OPENAI_DEFER_TOOLS"] = value, ["AEGIS_OPENAI_SERVICE_TIER"] = "default" };
        using var services = new ServiceCollection().AddInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(values).Build()).BuildServiceProvider();
        var options = services.GetRequiredService<IOptions<OpenAIOptions>>().Value;
        Assert.Equal(expected, options.DeferTools);
        Assert.Equal("default", options.ServiceTier);
    }

    private static readonly ModelToolDefinition EchoDefinition = new("echo", "Echo", JsonSerializer.SerializeToElement(new { type = "object" }));
    private static ModelRequest Request(string? origin) => new("policy", "pedido", Metadata: origin is null ? null : new Dictionary<string, string> { ["origin"] = origin });
    private static OpenAIOptions Settings(bool defer) => new() { ApiKey = "fake-key", DeferTools = defer, ServiceTier = "default" };

    private static async Task<string> Payload(bool defer, bool streaming, string? origin, ModelToolDefinition[] tools)
    {
        var handler = new PayloadHandler();
        using var http = new HttpClient(handler) { BaseAddress = new("https://example.invalid/") };
        using var metrics = new AegisMetrics();
        var model = new OpenAIResponsesClient(http, Options.Create(Settings(defer)), metrics, NullLogger<OpenAIResponsesClient>.Instance);
        var request = new ModelToolRequest(Request(origin), tools);
        if (streaming) { await foreach (var chunk in model.RespondWithToolsStreamAsync(request)) { } }
        else await model.RespondWithToolsAsync(request);
        return handler.Body!;
    }

    private static async Task<(string Speech, string? Continuation, List<string> Requests, int ToolAttempts, int ToolEffects, List<long?> SearchCounts)> Loop(
        bool defer, bool streaming, bool authorized = true, bool unknown = false, bool profile = true)
    {
        var previous = Environment.GetEnvironmentVariable("AEGIS_LATENCY_PROFILE");
        Environment.SetEnvironmentVariable("AEGIS_LATENCY_PROFILE", profile ? "1" : null);
        try
        {
            var tool = new EchoTool(authorized);
            var handler = new LoopHandler(defer, streaming, unknown);
            using var http = new HttpClient(handler) { BaseAddress = new("https://example.invalid/") };
            using var metrics = new AegisMetrics();
            var logger = new Logger();
            var model = new OpenAIResponsesClient(http, Options.Create(Settings(defer)), metrics, logger);
            var loop = new AegisToolLoop(model, new AegisToolRegistry([tool]), NullLogger<AegisToolLoop>.Instance, metrics);
            var context = new ToolExecutionContext(Guid.NewGuid(), Guid.NewGuid(), "pedido", "hands_free", Guid.NewGuid(), streaming);
            var speech = new StringBuilder();
            string? continuation;
            using (TurnLatencyProfile.Begin(Guid.NewGuid(), logger))
            {
                if (streaming)
                {
                    continuation = null;
                    await foreach (var chunk in loop.StreamAsync(Request("hands_free"), context))
                    {
                        if (chunk.IsDone) continuation = chunk.VoiceContinuation;
                        else speech.Append(chunk.FinalSpeechSegment ?? chunk.Content);
                    }
                }
                else
                {
                    var result = await loop.RunAsync(Request("hands_free"), context);
                    speech.Append(result.Content); continuation = result.VoiceContinuation;
                }
            }
            Assert.True(tool.LastContext is null || tool.LastContext == context);
            return (speech.ToString(), continuation, handler.Requests, tool.Attempts, tool.Effects, logger.SearchCounts);
        }
        finally { Environment.SetEnvironmentVariable("AEGIS_LATENCY_PROFILE", previous); }
    }

    private static HttpResponseMessage Response(bool streaming, object response, IEnumerable<object>? preceding = null)
    {
        var json = JsonSerializer.Serialize(response);
        var events = preceding?.ToList() ?? [];
        events.Add(new { type = "response.completed", response });
        return new(HttpStatusCode.OK) { Content = new StringContent(streaming
            ? string.Concat(events.Select(item => "data: " + JsonSerializer.Serialize(item) + "\n\n")) + "data: [DONE]\n\n" : json,
            Encoding.UTF8, streaming ? "text/event-stream" : "application/json") };
    }

    private sealed class PayloadHandler : HttpMessageHandler
    {
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(Body);
            return Response(document.RootElement.GetProperty("stream").GetBoolean(), new { model = "gpt-6-luna", output_text = "ok", output = Array.Empty<object>() });
        }
    }

    private sealed class LoopHandler(bool search, bool streaming, bool unknown) : HttpMessageHandler
    {
        public readonly List<string> Requests = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (Requests.Count == 1)
            {
                var name = unknown ? "not_registered" : "echo";
                var output = new List<object>();
                if (search)
                {
                    output.Add(new { type = "tool_search_call", id = "search-1", execution = "server", call_id = (string?)null, status = "completed", arguments = new { paths = new[] { name } } });
                    output.Add(new { type = "tool_search_output", id = "loaded-1", execution = "server", call_id = (string?)null, status = "completed",
                        tools = new[] { new { type = "function", name, description = "Echo", parameters = EchoDefinition.ParametersSchema, strict = false, defer_loading = true } } });
                }
                output.Add(new { type = "function_call", id = "function-1", call_id = "call-1", name, arguments = "{}" });
                return Response(streaming, new { id = "response-1", model = "gpt-6-luna", output, usage = new { input_tokens = 100, output_tokens = 10 } },
                    output.Select(item => (object)new { type = "response.output_item.added", item }));
            }
            var terminal = streaming ? FinalSpeechSegments.ToolName : HandsFreeReply.ToolName;
            var arguments = streaming ? JsonSerializer.Serialize(new { segments = new[] { "Resposta final preservada." }, continuation = "close" })
                : JsonSerializer.Serialize(new { speech = "Resposta final preservada.", continuation = "close" });
            var item = new { type = "function_call", id = "terminal-1", call_id = "call-final", name = terminal, arguments };
            var events = new object[] { new { type = "response.output_item.added", item },
                new { type = "response.function_call_arguments.delta", item_id = "terminal-1", delta = arguments } };
            return Response(streaming, new { id = "response-2", model = "gpt-6-luna", output = new[] { item }, usage = new { input_tokens = 110, output_tokens = 20 } }, events);
        }
    }

    private sealed class EchoTool(bool authorized) : IAegisTool
    {
        public int Attempts, Effects;
        public ToolExecutionContext? LastContext;
        public string Name => EchoDefinition.Name;
        public string Description => EchoDefinition.Description;
        public JsonElement ParametersSchema => EchoDefinition.ParametersSchema;
        public Task<AegisToolResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken = default)
        {
            Attempts++; LastContext = context;
            if (!authorized) return Task.FromResult(new AegisToolResult(false, "authorization_required", "authorization_required"));
            Effects++; return Task.FromResult(new AegisToolResult(true, "ok"));
        }
    }

    private sealed class Logger : ILogger<OpenAIResponsesClient>
    {
        public readonly List<long?> SearchCounts = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> values) return;
            var fields = values.ToDictionary(item => item.Key, item => item.Value);
            if (fields.TryGetValue("Stage", out var stage) && stage is "model.tool_search_calls") SearchCounts.Add(fields["Count"] as long?);
        }
    }
}
