using System.Net;
using System.Text;
using System.Text.Json;
using Aegis.Application.Models;
using Aegis.Application.Observability;
using Aegis.Application.Voice;
using Aegis.Infrastructure.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class OpenAiWebSearchTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeSearchIncludesSourcesAndRetainsProviderEvidence(bool stream)
    {
        using var handler = new SearchHandler(stream);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/") };
        using var metrics = new AegisMetrics();
        var client = new OpenAIResponsesClient(http,
            Options.Create(new OpenAIOptions { ApiKey = "fake" }),
            metrics, NullLogger<OpenAIResponsesClient>.Instance);
        var localTool = new ModelToolDefinition("example", "Example",
            JsonSerializer.SerializeToElement(new { type = "object" }));
        var request = new ModelToolRequest(new ModelRequest("prompt", "Pesquise"), [localTool],
            NativeWebSearchCallLimit: 3);

        IReadOnlyList<JsonElement> output;
        string? audit;
        if (stream)
        {
            var chunks = new List<ModelToolStreamChunk>();
            await foreach (var chunk in client.RespondWithToolsStreamAsync(request))
                chunks.Add(chunk);
            Assert.Equal("Resultado.", string.Concat(chunks.Select(chunk => chunk.Content)));
            Assert.Equal(new[] { "started", "completed" },
                chunks.Where(chunk => chunk.ToolStatus is not null).Select(chunk => chunk.ToolStatus!.State));
            Assert.All(chunks.Where(chunk => chunk.ToolStatus is not null),
                chunk => Assert.Equal("web_search", chunk.ToolStatus!.Category));
            Assert.True(chunks.FindIndex(chunk => chunk.ToolStatus?.State == "started") <
                chunks.FindIndex(chunk => !string.IsNullOrEmpty(chunk.Content)));
            var completed = Assert.Single(chunks, chunk => chunk.IsDone);
            Assert.Empty(completed.ToolCalls);
            output = completed.OutputItems;
            audit = completed.AuditData!.ResponseBody;
        }
        else
        {
            var result = await client.RespondWithToolsAsync(request);
            Assert.Equal("Resultado.", result.Content);
            Assert.Empty(result.ToolCalls);
            output = result.OutputItems;
            audit = result.AuditData.ResponseBody;
        }

        using var payload = JsonDocument.Parse(handler.RequestBody!);
        var root = payload.RootElement;
        var tools = root.GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal(new[] { "function", "web_search" }, tools.Select(tool => tool.GetProperty("type").GetString()));
        Assert.Equal("example", tools[0].GetProperty("name").GetString());
        Assert.Equal("auto", root.GetProperty("tool_choice").GetString());
        Assert.Equal(3, root.GetProperty("max_tool_calls").GetInt32());
        Assert.Equal("web_search_call.action.sources", root.GetProperty("include")[0].GetString());
        Assert.Equal("https://example.org/source", output[0].GetProperty("action").GetProperty("sources")[0].GetProperty("url").GetString());
        Assert.Equal("url_citation", output[1].GetProperty("content")[0].GetProperty("annotations")[0].GetProperty("type").GetString());
        Assert.NotNull(audit);
        Assert.Contains("https://example.org/source", audit);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostedSearchIsAbsentByDefaultAndWhileVoiceAcceptanceIsPending(bool pending)
    {
        using var handler = new SearchHandler(false, searched: false);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/") };
        using var metrics = new AegisMetrics();
        var client = new OpenAIResponsesClient(http,
            Options.Create(new OpenAIOptions { ApiKey = "fake" }),
            metrics, NullLogger<OpenAIResponsesClient>.Instance);
        IReadOnlyList<ModelToolDefinition> tools = pending ? [HandsFreeDirection.IgnoreDefinition] : [];
        var request = new ModelToolRequest(new ModelRequest("prompt", "pedido"), tools,
            NativeWebSearchCallLimit: pending ? 3 : 0);

        await client.RespondWithToolsAsync(request);

        using var payload = JsonDocument.Parse(handler.RequestBody!);
        var root = payload.RootElement;
        Assert.DoesNotContain(root.GetProperty("tools").EnumerateArray().ToArray(),
            tool => tool.GetProperty("type").GetString() == "web_search");
        Assert.False(root.TryGetProperty("max_tool_calls", out _));
        Assert.False(root.TryGetProperty("include", out _));
    }

    private sealed class SearchHandler(bool stream, bool searched = true) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            var response = searched ? """
                {"id":"resp_test","model":"gpt-6.1-terra","status":"completed","output":[
                  {"type":"web_search_call","id":"ws_test","status":"completed","action":{"type":"search","query":"teste","sources":[{"type":"url","url":"https://example.org/source"}]}},
                  {"type":"message","role":"assistant","content":[{"type":"output_text","text":"Resultado.","annotations":[{"type":"url_citation","start_index":0,"end_index":10,"title":"Exemplo","url":"https://example.org/source"}]}]}]}
                """ : "{\"output_text\":\"Resultado.\"}";
            var body = stream
                ? "data: {\"type\":\"response.web_search_call.in_progress\",\"item_id\":\"ws_test\"}\n\n"
                    + "data: {\"type\":\"response.web_search_call.searching\",\"item_id\":\"ws_test\"}\n\n"
                    + "data: {\"type\":\"response.web_search_call.completed\",\"item_id\":\"ws_test\"}\n\n"
                    + "data: {\"type\":\"response.web_search_call.completed\",\"item_id\":\"ws_test\"}\n\n"
                    + "data: {\"type\":\"response.output_text.delta\",\"delta\":\"Resultado.\"}\n\n"
                    + "data: {\"type\":\"response.completed\",\"response\":" + response.Replace("\r", "").Replace("\n", "") + "}\n\ndata: [DONE]\n\n"
                : response;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, stream ? "text/event-stream" : "application/json")
            };
        }
    }
}
