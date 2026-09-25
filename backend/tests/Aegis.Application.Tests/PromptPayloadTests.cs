using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using System.Text.Json;
using Aegis.Application.Models;
using Aegis.Application.Observability;
using Aegis.Application.Prompts;
using Aegis.Application.Runtime;
using Aegis.Domain;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Models;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class PromptPayloadTests
{
    [Fact]
    public async Task PayloadKeepsStableDeveloperAndConversationPrefixBeforeRuntime()
    {
        var builder = new PromptBuilder(new FixedRuntimeContext("Horário: 2026-09-25 12:00 UTC"));
        var history = new[]
        {
            new ChatMessage(Guid.NewGuid(), ChatRoles.User, "mensagem anterior"),
            new ChatMessage(Guid.NewGuid(), ChatRoles.Assistant, "resposta anterior")
        };
        var prompt = await builder.BuildPromptAsync(history, "mensagem atual");
        var handler = new CaptureHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/") };
        using var metrics = new AegisMetrics();
        var client = new OpenAIResponsesClient(httpClient, Options.Create(new OpenAIOptions { ApiKey = "fake" }), metrics, NullLogger<OpenAIResponsesClient>.Instance);
        var tools = new[]
        {
            new ModelToolDefinition("z_tool", "Z", JsonSerializer.SerializeToElement(new { type = "object" })),
            new ModelToolDefinition("a_tool", "A", JsonSerializer.SerializeToElement(new { type = "object" }))
        };

        await client.RespondWithToolsAsync(new ModelToolRequest(
            new ModelRequest(prompt.Prompt, "mensagem atual", InputItems: prompt.InputItems), tools));

        using var payload = JsonDocument.Parse(handler.Body!);
        var root = payload.RootElement;
        Assert.Equal("gpt-5.6-luna", root.GetProperty("model").GetString());
        Assert.Equal("implicit", root.GetProperty("prompt_cache_options").GetProperty("mode").GetString());
        Assert.Equal("medium", root.GetProperty("reasoning").GetProperty("effort").GetString());
        var input = root.GetProperty("input");
        Assert.Equal(new[] { "developer", "user", "assistant", "user", "developer" },
            input.EnumerateArray().Select(item => item.GetProperty("role").GetString()));
        var stable = input[0].GetProperty("content")[0];
        Assert.Equal("explicit", stable.GetProperty("prompt_cache_breakpoint").GetProperty("mode").GetString());
        Assert.DoesNotContain("2026-09-25", stable.GetProperty("text").GetString());
        Assert.DoesNotContain("mensagem atual", stable.GetProperty("text").GetString());
        Assert.Contains("2026-09-25", input[4].GetProperty("content").GetString());
        Assert.Equal("mensagem atual", input[3].GetProperty("content").GetString());
        Assert.Equal(new[] { "a_tool", "z_tool" },
            root.GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task NextTurnPreservesPreviousUserBoundaryBeforeChangedTimestamp()
    {
        var previousUser = new ChatMessage(Guid.NewGuid(), ChatRoles.User, "primeira pergunta");
        var previousAssistant = new ChatMessage(previousUser.ConversationId, ChatRoles.Assistant, "primeira resposta");
        var first = await new PromptBuilder(new FixedRuntimeContext("Horário: 12:00"))
            .BuildPromptAsync([], previousUser.Content);
        var second = await new PromptBuilder(new FixedRuntimeContext("Horário: 12:05"))
            .BuildPromptAsync([previousUser, previousAssistant], "segunda pergunta",
                pendingEmailActionState: "Existe uma ação pendente de Gmail.");

        Assert.Equal(first.InputItems[0].GetRawText(), second.InputItems[0].GetRawText());
        Assert.Equal(first.InputItems[1].GetRawText(), second.InputItems[1].GetRawText());
        Assert.Equal("developer", first.InputItems[2].GetProperty("role").GetString());
        Assert.Equal("assistant", second.InputItems[2].GetProperty("role").GetString());
        Assert.Contains("12:05", second.InputItems[^1].GetProperty("content").GetString());
        Assert.Contains("ação pendente", second.InputItems[^1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task ChatReasoningEffortUsesConfiguredValue()
    {
        var handler = new CaptureHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/") };
        using var metrics = new AegisMetrics();
        var client = new OpenAIResponsesClient(httpClient,
            Options.Create(new OpenAIOptions { ApiKey = "fake", ChatReasoningEffort = "low" }),
            metrics, NullLogger<OpenAIResponsesClient>.Instance);
        await client.RespondWithToolsAsync(new ModelToolRequest(new ModelRequest("", "oi"), []));
        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.Equal("low", payload.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
    }

    [Fact]
    public async Task CacheUsageIsEmittedAsStructuredMetrics()
    {
        var values = new Dictionary<string, long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, activeListener) =>
        {
            if (instrument.Meter.Name == "Aegis") activeListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => values[instrument.Name] = value);
        listener.Start();

        using var metrics = new AegisMetrics();
        using var httpClient = new HttpClient(new CaptureHandler()) { BaseAddress = new Uri("https://api.openai.com/") };
        var client = new OpenAIResponsesClient(httpClient, Options.Create(new OpenAIOptions { ApiKey = "fake" }), metrics, NullLogger<OpenAIResponsesClient>.Instance);
        await client.RespondWithToolsAsync(new ModelToolRequest(new ModelRequest("", "oi"), []));

        Assert.Equal(100, values["aegis_llm_input_tokens_total"]);
        Assert.Equal(40, values["aegis_llm_cached_input_tokens_total"]);
        Assert.Equal(20, values["aegis_llm_cache_write_tokens_total"]);
        Assert.Equal(10, values["aegis_llm_output_tokens_total"]);
    }

    private sealed class FixedRuntimeContext(string text) : IRuntimeContextProvider
    {
        public Task<string> GetRuntimeContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(text);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"model\":\"gpt-5.6-luna\",\"output_text\":\"ok\",\"output\":[],\"usage\":{\"input_tokens\":100,\"input_tokens_details\":{\"cached_tokens\":40,\"cache_write_tokens\":20},\"output_tokens\":10}}", Encoding.UTF8, "application/json")
            };
        }
    }
}
