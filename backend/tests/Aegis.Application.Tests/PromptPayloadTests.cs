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
    public async Task PayloadKeepsStableDeveloperMessageBeforeRuntimeHistoryAndCurrentUser()
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
        Assert.Equal("explicit", root.GetProperty("prompt_cache_options").GetProperty("mode").GetString());
        var input = root.GetProperty("input");
        Assert.Equal(new[] { "developer", "developer", "user", "assistant", "user" },
            input.EnumerateArray().Select(item => item.GetProperty("role").GetString()));
        var stable = input[0].GetProperty("content")[0];
        Assert.Equal("explicit", stable.GetProperty("prompt_cache_breakpoint").GetProperty("mode").GetString());
        Assert.DoesNotContain("2026-09-25", stable.GetProperty("text").GetString());
        Assert.DoesNotContain("mensagem atual", stable.GetProperty("text").GetString());
        Assert.Contains("2026-09-25", input[1].GetProperty("content").GetString());
        Assert.Equal("mensagem atual", input[4].GetProperty("content").GetString());
        Assert.Equal(new[] { "a_tool", "z_tool" },
            root.GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()));
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
