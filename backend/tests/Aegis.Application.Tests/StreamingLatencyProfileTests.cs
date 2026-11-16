using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aegis.Api.Controllers;
using Aegis.Application.Chat;
using Aegis.Application.Memory;
using Aegis.Application.Models;
using Aegis.Application.Observability;
using Aegis.Application.Voice;
using Aegis.Infrastructure.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aegis.Application.Tests;

[Collection("LatencyProfileEnvironment")]
public sealed class StreamingLatencyProfileTests
{
    [Fact]
    public async Task HeadersAreMarkedBeforeFirstSseAndEveryRoundHasNumericUsage()
    {
        using var setting = new ProfileSetting(true);
        var logger = new Logger<OpenAIResponsesClient>();
        var handler = new ProviderHandler(["123.75"], gated: true);
        using var http = new HttpClient(handler) { BaseAddress = new("https://example.invalid/") };
        using var metrics = new AegisMetrics();
        var model = new OpenAIResponsesClient(http, Options.Create(new OpenAIOptions { ApiKey = "private-api-key" }), metrics, logger);
        using var profile = TurnLatencyProfile.Begin(Guid.NewGuid(), logger);
        for (var round = 0; round < 2; round++)
        {
            await using var stream = model.RespondWithToolsStreamAsync(Request()).GetAsyncEnumerator();
            var first = stream.MoveNextAsync().AsTask();
            if (round == 0)
            {
                await logger.HeadersSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Single(logger.Entries, entry => entry.Stage == "model.response_headers" && entry.Count == 200);
                Assert.DoesNotContain(logger.Entries, entry => entry.Stage == "model.first_sse");
                Assert.False(first.IsCompleted);
                handler.Release.TrySetResult();
            }
            Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("Resposta privada.", stream.Current.FinalSpeechSegment);
            while (await stream.MoveNextAsync()) { }
        }
        foreach (var stage in new[] { "model.response_headers", "model.first_sse", "model.final_segment", "model.service_tier.priority" })
            Assert.Equal(2, logger.Entries.Count(entry => entry.Stage == stage));
        Assert.Equal(2, logger.Entries.Count(entry => entry.Stage == "model.provider_processing" && entry.Duration == 123.75));
        foreach (var (stage, count) in new[] { ("model.input_tokens", 120L), ("model.cached_input_tokens", 90L),
                     ("model.cache_write_tokens", 5L), ("model.output_tokens", 15L), ("model.reasoning_tokens", 7L) })
            Assert.Equal(2, logger.Entries.Count(entry => entry.Stage == stage && entry.Count == count));
        Assert.DoesNotContain(logger.Messages, message => message.Contains("private-provider-id") ||
            message.Contains("private-api-key") || message.Contains("private-prompt") || message.Contains("Resposta privada"));
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-0.1")]
    [InlineData("1e9999")]
    [InlineData("private-invalid-header")]
    [InlineData("123,75")]
    public async Task InvalidProcessingHeadersNeverBecomeDurationsOrLogTheirValue(string header)
    {
        var result = await RunProvider(true, [header]);
        Assert.Contains(result.Entries, entry => entry.Stage == "model.response_headers");
        Assert.DoesNotContain(result.Entries, entry => entry.Stage == "model.provider_processing");
        Assert.DoesNotContain(result.Messages, message => message.Contains(header));
    }

    [Fact]
    public async Task MultipleProcessingHeadersAreIgnoredAndMalformedOptionalUsageCannotBreakStreaming()
    {
        var result = await RunProvider(true, ["12.5", "13.5"], malformedOptionalUsage: true);
        Assert.DoesNotContain(result.Entries, entry => entry.Stage == "model.provider_processing" || entry.Stage == "model.reasoning_tokens");
        Assert.Contains(result.Entries, entry => entry.Stage == "model.input_tokens" && entry.Count == 120);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("auto")]
    [InlineData("priority")]
    [InlineData("fast")]
    [InlineData("flex")]
    [InlineData("scale")]
    public async Task KnownServiceTierIsReportedOnlyAsAnAllowlistedStage(string tier)
    {
        var result = await RunProvider(true, ["12.5"], serviceTier: tier);
        Assert.Single(result.Entries, entry => entry.Stage == "model.service_tier." + tier && entry.Count == 1);
    }

    [Fact]
    public async Task UnknownServiceTierNeverBecomesAStageOrLogValue()
    {
        var result = await RunProvider(true, ["12.5"], serviceTier: "private-unknown-tier");
        Assert.DoesNotContain(result.Entries, entry => entry.Stage.StartsWith("model.service_tier."));
        Assert.DoesNotContain(result.Messages, message => message.Contains("private-unknown-tier"));
    }

    [Fact]
    public async Task OptInDoesNotChangeRequestOrStreamFieldsAndOffProducesNoProfile()
    {
        var off = await RunProvider(false, ["123.75"]);
        var on = await RunProvider(true, ["123.75"]);
        Assert.Empty(off.Entries);
        Assert.NotEmpty(on.Entries);
        Assert.Equal(off.Request, on.Request);
        Assert.Equal(off.Stream, on.Stream);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task ControllerMarksFirstSegmentOnlyAfterSuccessfulFlush(bool enabled, bool cancelFlush)
    {
        using var setting = new ProfileSetting(enabled);
        var logger = new Logger<ChatController>();
        var turn = Guid.NewGuid();
        var events = new[] { ChatStreamEvent.FinalSegment(turn, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 0, "Primeira frase."),
            ChatStreamEvent.FinalSegment(turn, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, "Segunda frase.") };
        var body = new GatedFlushStream(cancelFlush);
        var controller = new ChatController(new Chat(events), null!, logger)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        controller.Response.Body = body;
        var pending = controller.StreamMessage(new() { Content = "private-prompt", TurnId = turn }, CancellationToken.None);
        await body.FlushEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(logger.Entries, entry => entry.Stage == "chat.first_final_segment_flushed");
        body.Release.TrySetResult();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        if (enabled && !cancelFlush)
            Assert.Single(logger.Entries, entry => entry.Stage == "chat.first_final_segment_flushed" && entry.Count == 0);
        else
            Assert.DoesNotContain(logger.Entries, entry => entry.Stage == "chat.first_final_segment_flushed");
        if (!enabled) Assert.Empty(logger.Entries);
        if (!cancelFlush)
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
            Assert.Equal(string.Concat(events.Select(item => JsonSerializer.Serialize(item, options) + "\n")), Encoding.UTF8.GetString(body.ToArray()));
            Assert.Equal("application/x-ndjson", controller.Response.ContentType);
            Assert.Equal("no", controller.Response.Headers["X-Accel-Buffering"].ToString());
        }
    }

    private static ModelToolRequest Request() => new(new ModelRequest("private-prompt", "private-request"), [FinalSpeechSegments.Definition]);

    private static async Task<(string Request, string Stream, Entry[] Entries, string[] Messages)> RunProvider(
        bool enabled, string[] headers, bool malformedOptionalUsage = false, string serviceTier = "priority")
    {
        using var setting = new ProfileSetting(enabled);
        var logger = new Logger<OpenAIResponsesClient>();
        var handler = new ProviderHandler(headers, malformedOptionalUsage: malformedOptionalUsage, serviceTier: serviceTier);
        using var http = new HttpClient(handler) { BaseAddress = new("https://example.invalid/") };
        using var metrics = new AegisMetrics();
        var model = new OpenAIResponsesClient(http, Options.Create(new OpenAIOptions { ApiKey = "private-api-key" }), metrics, logger);
        var chunks = new List<ModelToolStreamChunk>();
        using (TurnLatencyProfile.Begin(Guid.NewGuid(), logger))
            await foreach (var chunk in model.RespondWithToolsStreamAsync(Request()))
                chunks.Add(chunk with { AuditData = chunk.AuditData is { } audit ? audit with { DurationMilliseconds = 0 } : null });
        return (handler.Request!, JsonSerializer.Serialize(chunks), logger.Entries, logger.Messages);
    }

    private sealed class ProviderHandler(string[] processingHeaders, bool gated = false, bool malformedOptionalUsage = false, string serviceTier = "priority") : HttpMessageHandler
    {
        public string? Request;
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (!gated) Release.TrySetResult();
            var arguments = JsonSerializer.Serialize(new { segments = new[] { "Resposta privada." }, continuation = "close" });
            var usage = new { input_tokens = 120, output_tokens = 15,
                input_tokens_details = new { cached_tokens = 90, cache_write_tokens = 5 },
                output_tokens_details = new { reasoning_tokens = malformedOptionalUsage ? (object)"private-invalid-usage" : 7 } };
            var events = new object[] {
                new { type = "response.output_item.added", item = new { type = "function_call", id = "item-1", name = FinalSpeechSegments.ToolName } },
                new { type = "response.function_call_arguments.delta", item_id = "item-1", delta = arguments },
                new { type = "response.completed", response = new { id = "response-1", model = "gpt-6-luna", status = "completed", service_tier = serviceTier, usage,
                    output = new[] { new { type = "function_call", call_id = "call-1", name = FinalSpeechSegments.ToolName, arguments } } } }
            };
            var data = ": heartbeat\n\n" + string.Concat(events.Select(item => "data: " + JsonSerializer.Serialize(item) + "\n\n")) + "data: [DONE]\n\n";
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new GatedReadStream(Encoding.UTF8.GetBytes(data), Release.Task)) };
            response.Headers.TryAddWithoutValidation("openai-processing-ms", processingHeaders);
            response.Headers.TryAddWithoutValidation("x-request-id", "private-provider-id");
            return response;
        }
    }

    private sealed class GatedReadStream(byte[] bytes, Task gate) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await gate.WaitAsync(cancellationToken); return await base.ReadAsync(buffer, cancellationToken); }
    }

    private sealed class GatedFlushStream(bool cancel) : MemoryStream
    {
        public readonly TaskCompletionSource FlushEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushEntered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            if (cancel) throw new OperationCanceledException();
            await base.FlushAsync(cancellationToken);
        }
    }

    private sealed record Entry(string Stage, long? Count, double Duration);
    private sealed class Logger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<Entry> entries = new();
        private readonly ConcurrentQueue<string> messages = new();
        public Entry[] Entries => entries.ToArray();
        public string[] Messages => messages.ToArray();
        public readonly TaskCompletionSource HeadersSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            messages.Enqueue(formatter(state, exception));
            if (state is not IEnumerable<KeyValuePair<string, object?>> values) return;
            var fields = values.ToDictionary(item => item.Key, item => item.Value);
            if (!fields.TryGetValue("Stage", out var stage) || stage is not string name) return;
            entries.Enqueue(new(name, fields["Count"] as long?, Convert.ToDouble(fields["DurationMilliseconds"])));
            if (name == "model.response_headers") HeadersSeen.TrySetResult();
        }
    }

    private sealed class ProfileSetting : IDisposable
    {
        private readonly string? previous = Environment.GetEnvironmentVariable("AEGIS_LATENCY_PROFILE");
        public ProfileSetting(bool enabled) => Environment.SetEnvironmentVariable("AEGIS_LATENCY_PROFILE", enabled ? "1" : null);
        public void Dispose() => Environment.SetEnvironmentVariable("AEGIS_LATENCY_PROFILE", previous);
    }

    private sealed class Chat(ChatStreamEvent[] events) : IChatService
    {
        public async IAsyncEnumerable<ChatStreamEvent> StreamMessageAsync(SendMessageRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        { foreach (var item in events) { cancellationToken.ThrowIfCancellationRequested(); yield return item; } await Task.CompletedTask; }
        public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ConversationResponse?> GetConversationAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<MemoryActivitySnapshot?> GetMemoryActivityAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ConversationPageResponse> GetRecentConversationsAsync(int limit = 30, string? cursor = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ConversationSummaryResponse?> RenameConversationAsync(Guid id, string? title, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> DeleteConversationAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
