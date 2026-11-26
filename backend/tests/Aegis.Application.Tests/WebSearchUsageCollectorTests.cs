using Aegis.Application.Models;
using Aegis.Application.Observability;
using Aegis.Infrastructure.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class WebSearchUsageCollectorTests
{
    [Fact]
    public async Task SyncRecordsOnlyCanonicalActionsAndNoQueriesOrSources()
    {
        const string payload = """
            {"id":"resp_sync","model":"test","output":[
              {"type":"web_search_call","id":"ws_search","status":"completed","action":{"type":"search","queries":["private query"]}},
              {"type":"web_search_call","id":"ws_open","status":"completed","action":{"type":"open_page","url":"https://example.org"}},
              {"type":"web_search_call","id":"ws_find","status":"failed","action":{"type":"find_in_page","pattern":"private pattern"}},
              {"type":"web_search_call","id":"ws_other","status":"incomplete","action":{"type":"future_action"}},
              {"type":"web_search_call","id":"ws_unknown","status":"in_progress"},
              {"type":"message","role":"assistant","content":[{"type":"output_text","text":"Resposta."}]}
            ]}
            """;
        var recorder = new CaptureRecorder();
        using var client = Client(payload, "application/json");
        using var metrics = new AegisMetrics();
        var responses = new OpenAIResponsesClient(client, Options.Create(new OpenAIOptions { ApiKey = "fake" }),
            metrics, NullLogger<OpenAIResponsesClient>.Instance, recorder);
        await responses.RespondWithToolsAsync(new ModelToolRequest(new ModelRequest("teste", "teste"), []));

        Assert.Equal(new[] { "search", "open_page", "find_in_page", "other", "unknown" },
            recorder.Observations.Select(item => item.ActionType).ToArray());
        Assert.All(recorder.Observations, item => Assert.Equal("resp_sync", item.ResponseId));
        Assert.Equal("failed", recorder.Observations[2].Status);
        Assert.DoesNotContain("private query", System.Text.Json.JsonSerializer.Serialize(recorder.Observations));
        Assert.DoesNotContain("https://example.org", System.Text.Json.JsonSerializer.Serialize(recorder.Observations));
    }

    [Fact]
    public async Task StreamRefinesSameCallAndDeduplicatesRepeatedTerminalEvidence()
    {
        var recorder = new CaptureRecorder();
        var payload = Frames(
            """{"type":"response.created","response":{"id":"resp_sse"}}""",
            """{"type":"response.web_search_call.in_progress","item_id":"ws_1"}""",
            """{"type":"response.web_search_call.in_progress","item_id":"ws_1"}""",
            """{"type":"response.web_search_call.searching","item_id":"ws_1"}""",
            """{"type":"response.output_item.done","item":{"type":"web_search_call","id":"ws_1","status":"completed","action":{"type":"open_page"}}}""",
            """{"type":"response.web_search_call.completed","item_id":"ws_1"}""",
            """{"type":"response.completed","response":{"id":"resp_sse","model":"test","output":[{"type":"web_search_call","id":"ws_1","status":"completed","action":{"type":"open_page"}}]}}""");
        using var client = Client(payload, "text/event-stream");
        using var metrics = new AegisMetrics();
        var responses = new OpenAIResponsesClient(client, Options.Create(new OpenAIOptions { ApiKey = "fake" }),
            metrics, NullLogger<OpenAIResponsesClient>.Instance, recorder);
        await foreach (var _ in responses.RespondWithToolsStreamAsync(
            new ModelToolRequest(new ModelRequest("teste", "teste"), []))) { }

        Assert.Equal(3, recorder.Observations.Count);
        Assert.Equal("unknown", recorder.Observations[0].ActionType);
        Assert.Equal("searching", recorder.Observations[1].Status);
        Assert.Equal("open_page", recorder.Observations[2].ActionType);
        Assert.All(recorder.Observations, item =>
        {
            Assert.Equal("ws_1", item.CallId);
            Assert.Equal("resp_sse", item.ResponseId);
            Assert.Equal(recorder.Observations[0].FirstObservedAt, item.FirstObservedAt);
        });
    }

    [Fact]
    public async Task EarlyStreamObservationIsRetainedWhenCallerStopsBeforeFinalBody()
    {
        var recorder = new CaptureRecorder();
        var payload = Frames(
            """{"type":"response.created","response":{"id":"resp_interrupted"}}""",
            """{"type":"response.web_search_call.in_progress","item_id":"ws_pending"}""",
            """{"type":"response.output_text.delta","delta":"Trecho."}""");
        using var client = Client(payload, "text/event-stream");
        using var metrics = new AegisMetrics();
        using var cancellation = new CancellationTokenSource();
        var responses = new OpenAIResponsesClient(client, Options.Create(new OpenAIOptions { ApiKey = "fake" }),
            metrics, NullLogger<OpenAIResponsesClient>.Instance, recorder);
        await using (var stream = responses.RespondWithToolsStreamAsync(
            new ModelToolRequest(new ModelRequest("teste", "teste"), []), cancellation.Token).GetAsyncEnumerator())
        {
            Assert.True(await stream.MoveNextAsync());
            cancellation.Cancel();
        }

        var observed = Assert.Single(recorder.Observations);
        Assert.Equal("ws_pending", observed.CallId);
        Assert.Equal("unknown", observed.ActionType);
        Assert.Equal("in_progress", observed.Status);
    }

    [Fact]
    public async Task FailedEarlyWriteCanRetryWithoutMovingTheFirstObservationDate()
    {
        var recorder = new CaptureRecorder { FailFirst = true };
        var payload = Frames(
            """{"type":"response.web_search_call.in_progress","item_id":"ws_retry"}""",
            """{"type":"response.web_search_call.in_progress","item_id":"ws_retry"}""",
            """{"type":"response.completed","response":{"id":"resp_retry","model":"test","output":[{"type":"web_search_call","id":"ws_retry","status":"completed","action":{"type":"search"}}]}}""");
        using var client = Client(payload, "text/event-stream");
        using var metrics = new AegisMetrics();
        var responses = new OpenAIResponsesClient(client, Options.Create(new OpenAIOptions { ApiKey = "fake" }),
            metrics, NullLogger<OpenAIResponsesClient>.Instance, recorder);
        await foreach (var _ in responses.RespondWithToolsStreamAsync(
            new ModelToolRequest(new ModelRequest("teste", "teste"), []))) { }
        Assert.Equal(3, recorder.Observations.Count);
        Assert.All(recorder.Observations, item => Assert.Equal(recorder.Observations[0].FirstObservedAt, item.FirstObservedAt));
        Assert.Equal("resp_retry", recorder.Observations[^1].ResponseId);
    }

    private static string Frames(params string[] frames) =>
        string.Concat(frames.Select(frame => $"data: {frame}\n\n"));

    private static HttpClient Client(string payload, string contentType) =>
        new(new Handler(payload, contentType)) { BaseAddress = new Uri("https://api.openai.com/") };

    private sealed class Handler(string payload, string contentType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, contentType) });
    }

    private sealed class CaptureRecorder : IWebSearchUsageRecorder
    {
        public List<WebSearchUsageObservation> Observations { get; } = [];
        public bool FailFirst { get; init; }
        public Task<bool> TryRecordAsync(WebSearchUsageObservation observation)
        {
            Observations.Add(observation);
            return Task.FromResult(!FailFirst || Observations.Count > 1);
        }
    }
}
