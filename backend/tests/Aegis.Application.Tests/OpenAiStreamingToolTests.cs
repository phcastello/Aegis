using System.Net;
using System.Text;
using System.Text.Json;
using Aegis.Application.Models;
using Aegis.Application.Observability;
using Aegis.Application.Tools;
using Aegis.Infrastructure.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class OpenAiStreamingToolTests
{
    [Fact]
    public async Task StreamingFunctionCallAndAnswerUseExactlyTwoRequests()
    {
        var handler = new SequenceHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/") };
        using var metrics = new AegisMetrics();
        var model = new OpenAIResponsesClient(http, Options.Create(new OpenAIOptions { ApiKey = "fake" }),
            metrics, NullLogger<OpenAIResponsesClient>.Instance);
        var loop = new AegisToolLoop(model, new AegisToolRegistry([new EchoTool()]),
            NullLogger<AegisToolLoop>.Instance, metrics);
        var chunks = new List<ModelStreamChunk>();

        await foreach (var chunk in loop.StreamAsync(new ModelRequest("prompt", "use a ferramenta"),
            new ToolExecutionContext(Guid.NewGuid(), Guid.NewGuid(), "use a ferramenta"))) chunks.Add(chunk);

        Assert.Equal(2, handler.Calls);
        Assert.Equal("Feito", string.Concat(chunks.Where(chunk => !chunk.IsDone).Select(chunk => chunk.Content)));
        Assert.Equal(new[] { "started", "completed" }, chunks.Where(chunk => chunk.ToolStatus is not null).Select(chunk => chunk.ToolStatus!.State));
        Assert.True(chunks[^1].IsDone);
        Assert.Contains("function_call_output", handler.Requests[1]);
    }

    private sealed class EchoTool : IAegisTool
    {
        public string Name => "echo";
        public string Description => "Echo";
        public JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new { type = "object" });
        public Task<AegisToolResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AegisToolResult(true, "{\"ok\":true}"));
    }

    private sealed class SequenceHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public List<string> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var body = Calls++ == 0
                ? "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"r1\",\"model\":\"gpt-5.6-luna\",\"output\":[{\"type\":\"function_call\",\"call_id\":\"c1\",\"name\":\"echo\",\"arguments\":\"{}\"}]}}\n\ndata: [DONE]\n\n"
                : "data: {\"type\":\"response.output_text.delta\",\"delta\":\"Feito\"}\n\ndata: {\"type\":\"response.completed\",\"response\":{\"id\":\"r2\",\"model\":\"gpt-5.6-luna\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"Feito\"}]}]}}\n\ndata: [DONE]\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
            };
        }
    }
}
