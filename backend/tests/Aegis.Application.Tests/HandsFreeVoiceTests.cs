using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Aegis.Application.Chat;
using Aegis.Application.Llm;
using Aegis.Application.Models;
using Aegis.Application.Observability;
using Aegis.Application.Prompts;
using Aegis.Application.Runtime;
using Aegis.Application.Tools;
using Aegis.Application.Turns;
using Aegis.Application.Voice;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class HandsFreeVoiceTests
{
    [Theory]
    [InlineData(false, "continue")]
    [InlineData(true, "continue")]
    [InlineData(false, "close")]
    [InlineData(true, "close")]
    [InlineData(false, "invalid")]
    [InlineData(true, "invalid")]
    public async Task TerminalReplyIncludesDecisionWithoutAnotherModelCall(bool streaming, string decision)
    {
        var model = new Model([Reply(decision)]);
        var result = await Run(model, streaming);
        Assert.Equal("Quer que eu leia a agenda?", result.Content);
        Assert.Equal(decision == "continue" ? "continue" : "close", result.Decision);
        Assert.Single(model.Requests);
        Assert.Contains(model.Requests[0].Tools, x => x.Name == HandsFreeReply.ToolName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingToolFlowThenTerminalReplyUsesOnlyRequiredRounds(bool streaming)
    {
        var tool = new BenignTool();
        var model = new Model([Response([new("call-read", tool.Name, JsonSerializer.SerializeToElement(new { }))]), Reply("close")]);
        var result = await Run(model, streaming, tool);
        Assert.Equal(2, model.Requests.Count);
        Assert.Equal(1, tool.Calls);
        Assert.Equal(HandsFreeReply.Origin, tool.LastContext!.Origin);
        Assert.Equal("close", result.Decision);
        Assert.Contains(model.Requests[1].InputItems!, item => item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingDecisionHasBoundedCloseFallbackAndNoAdditionalRequest(bool streaming)
    {
        var model = new Model([Response([], "Resposta normal")]);
        var result = await Run(model, streaming);
        Assert.Equal("Resposta normal", result.Content);
        Assert.Equal("close", result.Decision);
        Assert.Single(model.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AmbiguousReplyAndActionBatchExecutesNoAction(bool streaming)
    {
        var tool = new BenignTool();
        var model = new Model([Response([.. Reply("continue").ToolCalls,
            new("call-read", tool.Name, JsonSerializer.SerializeToElement(new { }))])]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(model, streaming, tool));
        Assert.Equal(0, tool.Calls);
        Assert.Single(model.Requests);
    }

    [Fact]
    public async Task OrdinaryChatDoesNotReceiveVoiceToolOrDecision()
    {
        var model = new Model([Response([], "oi")]);
        using var metrics = new AegisMetrics();
        var loop = new AegisToolLoop(model, new AegisToolRegistry([]), NullLogger<AegisToolLoop>.Instance, metrics);
        var response = await loop.RunAsync(new("prompt", "pedido"), new(Guid.NewGuid(), Guid.NewGuid(), "pedido"));
        Assert.Null(response.VoiceContinuation);
        Assert.DoesNotContain(model.Requests[0].Tools, x => x.Name == HandsFreeReply.ToolName);
    }

    [Theory]
    [InlineData("Aegis")]
    [InlineData("Aegis, ")]
    [InlineData("aegis?!")]
    public async Task WakeOnlyActivationCreatesNoConversationAndRunsNoModel(string content)
    {
        // Dependencies are deliberately absent: validation must precede every persistence/model operation.
        var chat = new ChatService(null!, null!, null!, null!, null!, null!);
        var request = new SendMessageRequest { Origin = "hands_free", VoiceSessionId = Guid.NewGuid(), Content = content };
        await Assert.ThrowsAsync<ArgumentException>(() => chat.SendMessageAsync(request));
    }

    [Fact]
    public void HandsFreeRequiresSessionAndStripsOnlyTheLeadingName()
    {
        Assert.Throws<ArgumentException>(() => HandsFreeReply.UserContent(new() { Origin = "hands_free", Content = "oi" }));
        Assert.Equal("o que tem hoje?", HandsFreeReply.UserContent(new() { Origin = "hands_free", VoiceSessionId = Guid.NewGuid(), Content = "Aegis, o que tem hoje?" }));
        Assert.Equal("Aegis está aqui", HandsFreeReply.UserContent(new() { Content = "Aegis está aqui" }));
        Assert.Equal("Aegisteste", HandsFreeReply.UserContent(new() { Origin = "hands_free", VoiceSessionId = Guid.NewGuid(), Content = "Aegisteste" }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatPersistsOnlySpokenAnswerAndKeepsOperationalOriginAndConversation(bool streaming)
    {
        using var db = new AegisDbContext(new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        using var metrics = new AegisMetrics();
        using var registry = new ActiveTurnRegistry(metrics);
        var voice = DispatchProxy.Create<IVoiceService, Proxy>();
        ((Proxy)(object)voice).Call = (_, args) => Task.FromResult(registry.Register((Guid)args![0]!, (Guid)args[1]!));
        var titles = DispatchProxy.Create<IConversationTitleJobQueue, Proxy>();
        ((Proxy)(object)titles).Call = (_, _) => ValueTask.CompletedTask;
        var model = new Model([Reply("continue"), Reply("close")]);
        var loop = new AegisToolLoop(model, new AegisToolRegistry([]), NullLogger<AegisToolLoop>.Instance, metrics);
        var chat = new ChatService(db, new PromptBuilder(new Runtime()), loop, titles, registry, voice);
        var session = Guid.NewGuid();
        Guid? conversation = null;
        foreach (var text in new[] { "Aegis, veja minha agenda", "Pode ler" })
        {
            var turn = Guid.NewGuid();
            var request = new SendMessageRequest { TurnId = turn, ConversationId = conversation, Origin = "hands_free", VoiceSessionId = session, Content = text };
            if (streaming)
            {
                var events = new List<ChatStreamEvent>();
                await foreach (var item in chat.StreamMessageAsync(request)) events.Add(item);
                var done = Assert.Single(events, item => item.Type == "done");
                conversation = done.ConversationId;
                Assert.Equal(text == "Pode ler" ? "close" : "continue", done.VoiceContinuation);
                Assert.NotNull(done.AssistantMessageId);
            }
            else
            {
                var result = await chat.SendMessageAsync(request);
                conversation = result.ConversationId;
                Assert.Equal(text == "Pode ler" ? "close" : "continue", result.VoiceContinuation);
            }
            Assert.True(registry.TryBeginSpeech(turn, Guid.NewGuid(), "test-native"));
        }
        Assert.Single(await db.Conversations.ToListAsync());
        var messages = await db.ChatMessages.ToListAsync();
        Assert.Equal(4, messages.Count);
        Assert.DoesNotContain(messages, message => message.Content.Contains("continuation"));
        Assert.All(model.Requests, request =>
        {
            Assert.Equal("hands_free", request.Request.Metadata!["origin"]);
            Assert.Equal(session.ToString("D"), request.Request.Metadata["voice_session_id"]);
            Assert.Contains(request.Request.InputItems!, item => item.GetRawText().Contains("hands_free"));
        });
    }

    private static async Task<(string Content, string? Decision)> Run(Model model, bool streaming, BenignTool? tool = null)
    {
        using var metrics = new AegisMetrics();
        var loop = new AegisToolLoop(model, new AegisToolRegistry(tool is null ? [] : [tool]), NullLogger<AegisToolLoop>.Instance, metrics);
        var context = new ToolExecutionContext(Guid.NewGuid(), Guid.NewGuid(), "pedido", "hands_free", Guid.NewGuid());
        if (!streaming)
        {
            var response = await loop.RunAsync(new("prompt", "pedido"), context);
            return (response.Content, response.VoiceContinuation);
        }
        var chunks = new List<ModelStreamChunk>();
        await foreach (var chunk in loop.StreamAsync(new("prompt", "pedido"), context)) chunks.Add(chunk);
        return (string.Concat(chunks.Select(x => x.Content)), chunks[^1].VoiceContinuation);
    }

    private static ModelToolResponse Reply(string decision) => Response([new("reply", HandsFreeReply.ToolName,
        JsonSerializer.SerializeToElement(new { speech = "Quer que eu leia a agenda?", continuation = decision }))]);

    private static ModelToolResponse Response(IReadOnlyList<ModelToolCall> calls, string content = "") =>
        new(content, "fake", "fake", ModelPurpose.Chat, calls, [], null, null,
            new LlmRequestAuditData("fake", "fake", true, 1, "{}", 200, "{}", null, null));

    private sealed class Model(IReadOnlyList<ModelToolResponse> responses) : IAegisModelClient
    {
        public List<ModelToolRequest> Requests { get; } = [];
        public Task<ModelToolResponse> RespondWithToolsAsync(ModelToolRequest request, CancellationToken cancellationToken = default)
        {
            var response = responses[Requests.Count];
            Requests.Add(request);
            return Task.FromResult(response);
        }
        public async IAsyncEnumerable<ModelToolStreamChunk> RespondWithToolsStreamAsync(ModelToolRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await RespondWithToolsAsync(request, cancellationToken);
            if (response.Content.Length > 0) yield return new(response.Content, false, [], []);
            yield return new(null, true, response.ToolCalls, response.OutputItems, response.ResponseId, response.Provider,
                response.Model, response.Purpose, response.MetadataJson, response.AuditData);
        }
    }

    private sealed class BenignTool : IAegisTool
    {
        public int Calls;
        public ToolExecutionContext? LastContext;
        public string Name => "test_read";
        public string Description => "Leitura sintética";
        public JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new { type = "object" });
        public Task<AegisToolResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastContext = context;
            return Task.FromResult(new AegisToolResult(true, "{}"));
        }
    }
    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Call { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Call(method!, args);
    }
    private sealed class Runtime : IRuntimeContextProvider
    {
        public Task<string> GetRuntimeContextAsync(CancellationToken cancellationToken = default) => Task.FromResult("Horário atual");
    }
}
