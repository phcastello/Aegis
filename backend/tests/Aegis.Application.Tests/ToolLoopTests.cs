using System.Runtime.CompilerServices;
using System.Text.Json;
using Aegis.Application.Llm;
using Aegis.Application.Models;
using Aegis.Application.Observability;
using Aegis.Application.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class ToolLoopTests
{
    [Fact]
    public async Task DirectAnswerStreamsWithOneModelCallAndNoToolExecution()
    {
        var model = new FakeModelClient([Reply("Resposta direta")]);
        var tool = new FakeTool();
        var chunks = await CollectAsync(CreateLoop(model, tool));

        Assert.Equal(1, model.StreamCalls);
        Assert.Equal(0, tool.Calls);
        Assert.Equal("Resposta direta", string.Concat(chunks.Where(chunk => !chunk.IsDone).Select(chunk => chunk.Content)));
        Assert.True(chunks[^1].IsDone);
    }

    [Fact]
    public async Task OneToolEmitsStatusReturnsOutputAndStreamsFinalAnswer()
    {
        var model = new FakeModelClient([Call("fake_tool"), Reply("Pronto")]);
        var tool = new FakeTool();
        var chunks = await CollectAsync(CreateLoop(model, tool));

        Assert.Equal(2, model.StreamCalls);
        Assert.Equal(1, tool.Calls);
        Assert.Equal(new[] { "started", "completed" }, chunks.Where(chunk => chunk.ToolStatus is not null).Select(chunk => chunk.ToolStatus!.State));
        Assert.Contains(model.Requests[1].InputItems!, item => item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output");
        Assert.Equal("Pronto", string.Concat(chunks.Where(chunk => !chunk.IsDone).Select(chunk => chunk.Content)));
    }

    [Fact]
    public async Task ToolFailureIsSafeAndModelCanExplainIt()
    {
        var model = new FakeModelClient([Call("fake_tool"), Reply("Não consegui consultar agora.")]);
        var tool = new FakeTool(throwError: true);
        var chunks = await CollectAsync(CreateLoop(model, tool));

        Assert.Contains(chunks, chunk => chunk.ToolStatus?.State == "failed");
        Assert.DoesNotContain("secret internal error", JsonSerializer.Serialize(chunks));
        Assert.Equal("Não consegui consultar agora.", string.Concat(chunks.Where(chunk => !chunk.IsDone).Select(chunk => chunk.Content)));
        Assert.Contains(model.Requests[1].InputItems!, item => item.TryGetProperty("output", out var output) && output.GetString()!.Contains("tool_execution_failed"));
    }

    [Fact]
    public async Task IterationLimitTerminatesSafely()
    {
        var model = new FakeModelClient(Enumerable.Range(0, 5).Select(_ => Call("fake_tool")).ToArray());
        var chunks = await CollectAsync(CreateLoop(model, new FakeTool()));

        Assert.Equal(5, model.StreamCalls);
        Assert.Contains(chunks, chunk => chunk.Content?.Contains("limite de etapas") == true);
        Assert.True(chunks[^1].IsDone);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolContinuationReassertsTrustedIdentityWithoutChangingNativeHistory(bool streaming)
    {
        const string identity = "Identidade confiável: ofereça ajuda útil, sem executar novas ações.";
        var prefix = new[]
        {
            JsonSerializer.SerializeToElement(new { role = "developer", content = new[] {
                new { type = "input_text", text = identity, prompt_cache_breakpoint = new { mode = "explicit" } }
            } }),
            JsonSerializer.SerializeToElement(new { role = "user", content = "Leia o conteúdo solicitado." }),
            JsonSerializer.SerializeToElement(new { role = "developer", content = "Contexto operacional atual" })
        };
        var request = new ModelRequest(identity, "Leia o conteúdo solicitado.", InputItems: prefix);
        var model = new FakeModelClient([Call("fake_tool"), Call("fake_tool"), Reply("Pronto")]);
        var loop = CreateLoop(model, new FakeTool());
        var context = new ToolExecutionContext(Guid.NewGuid(), Guid.NewGuid(), request.Input);
        if (streaming)
        {
            await foreach (var _ in loop.StreamAsync(request, context)) { }
        }
        else
        {
            await loop.RunAsync(request, context);
        }

        Assert.Equal(3, model.Requests.Count);
        Assert.Equal(prefix.Select(item => item.GetRawText()), model.Requests[0].InputItems!.Select(item => item.GetRawText()));
        for (var iteration = 1; iteration < model.Requests.Count; iteration++)
        {
            var items = model.Requests[iteration].InputItems!;
            Assert.Equal(prefix.Select(item => item.GetRawText()), items.Take(prefix.Length).Select(item => item.GetRawText()));
            Assert.Equal("developer", items[^1].GetProperty("role").GetString());
            Assert.Equal(identity, items[^1].GetProperty("content").GetString());
            Assert.Equal(3, items.Count(item => item.TryGetProperty("role", out var role) && role.GetString() == "developer"));
            Assert.Equal(iteration, items.Count(item => item.TryGetProperty("type", out var type) && type.GetString() == "function_call"));
            Assert.Equal(iteration, items.Count(item => item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output"));
        }
    }

    private static AegisToolLoop CreateLoop(FakeModelClient model, FakeTool tool) =>
        new(model, new AegisToolRegistry([tool]), NullLogger<AegisToolLoop>.Instance, new AegisMetrics());

    private static async Task<List<ModelStreamChunk>> CollectAsync(AegisToolLoop loop)
    {
        var chunks = new List<ModelStreamChunk>();
        await foreach (var chunk in loop.StreamAsync(
            new ModelRequest("prompt", "pedido"), new ToolExecutionContext(Guid.NewGuid(), Guid.NewGuid(), "pedido")))
        {
            chunks.Add(chunk);
        }
        return chunks;
    }

    private static ModelToolStreamChunk Reply(string text) => Done([]) with { Content = text };

    private static ModelToolStreamChunk Call(string name) => Done([
        new ModelToolCall("call_1", name, JsonSerializer.SerializeToElement(new { }))
    ]) with { OutputItems = [JsonSerializer.SerializeToElement(new { type = "function_call", call_id = "call_1", name, arguments = "{}" })] };

    private static ModelToolStreamChunk Done(IReadOnlyList<ModelToolCall> calls) =>
        new(null, true, calls, [], Provider: "fake", Model: "fake", Purpose: ModelPurpose.Chat,
            AuditData: new LlmRequestAuditData("fake", "fake", true, 1, "{}", 200, "{}", null, null));

    private sealed class FakeModelClient(IReadOnlyList<ModelToolStreamChunk> responses) : IAegisModelClient
    {
        public int StreamCalls { get; private set; }
        private int responseCalls;
        public List<ModelToolRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelToolStreamChunk> RespondWithToolsStreamAsync(
            ModelToolRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request with { InputItems = request.InputItems?.ToList() });
            StreamCalls++;
            var response = responses[responseCalls++];
            if (!string.IsNullOrEmpty(response.Content))
                yield return new ModelToolStreamChunk(response.Content, false, [], []);
            await Task.Yield();
            yield return response with { Content = null };
        }

        public Task<ModelCompletionResponse> GenerateAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public IAsyncEnumerable<ModelStreamChunk> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ModelToolResponse> RespondWithToolsAsync(ModelToolRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request with { InputItems = request.InputItems?.ToList() ?? request.Request.InputItems?.ToList() });
            var response = responses[responseCalls++];
            return Task.FromResult(new ModelToolResponse(response.Content ?? "", response.Provider!, response.Model!,
                response.Purpose!.Value, response.ToolCalls, response.OutputItems, response.ResponseId,
                response.MetadataJson, response.AuditData!));
        }
    }

    private sealed class FakeTool(bool throwError = false) : IAegisTool
    {
        public string Name => "fake_tool";
        public string Description => "Fake tool";
        public JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new { type = "object" });
        public int Calls { get; private set; }
        public Task<AegisToolResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (throwError) throw new InvalidOperationException("secret internal error");
            return Task.FromResult(new AegisToolResult(true, "{\"ok\":true}"));
        }
    }
}
