using System.Diagnostics.Metrics;
using Aegis.Api.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Aegis.Application.Chat;
using Aegis.Application.Llm;
using Aegis.Application.Memory;
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

public sealed class FinalSpeechSegmentTests
{
    [Fact]
    public void ParserEmitsOnlyClosedUnitsAcrossEveryCharacterIncludingEscapes()
    {
        var parser = new FinalSpeechSegmentParser();
        const string first = "Claro, o Dr. Ávila chega às 15:30, por R$ 2,50.";
        var json = JsonSerializer.Serialize(new { segments = new[] { first, "Ele disse \"não\"." }, continuation = "close" });
        var emitted = new List<string>();
        foreach (var ch in json) emitted.AddRange(parser.Append(ch.ToString()));
        Assert.Equal(new[] { first, "Ele disse \"não\"." }, emitted);
        var partial = new FinalSpeechSegmentParser();
        Assert.Empty(partial.Append("{\"segments\":[\"Isso ainda não terminou"));
        Assert.Equal(new[] { "Isso ainda não terminou." }, partial.Append(".\",\"Segundo"));
    }

    [Theory]
    [InlineData("{\"segments\":[123]")]
    [InlineData("{\"segments\":[{\"content\":\"oi\"}")]
    [InlineData("{\"segments\":[\" \"")]
    public void ParserRejectsInvalidCommittedValues(string json) =>
        Assert.ThrowsAny<Exception>(() => new FinalSpeechSegmentParser().Append(json));

    [Fact]
    public async Task FirstPersistedSegmentCanBeSynthesizedWhileModelIsStillGenerating()
    {
        using var fixture = new Fixture();
        await using var events = fixture.Chat.StreamMessageAsync(fixture.Request()).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());
        Assert.Equal("conversation", events.Current.Type);
        Assert.True(await events.MoveNextAsync());
        var first = events.Current;
        Assert.Equal("final_segment", first.Type);
        Assert.Equal(0, first.Sequence);
        Assert.False(fixture.Model.Finished);
        Assert.False(fixture.Voice.TryCompleteTurnWithoutSpeech(first.TurnId!.Value));
        var persisted = await fixture.Db.ChatMessages.SingleAsync(x => x.Id == first.AssistantMessageId);
        Assert.Equal("Ora, tudo certo.", persisted.Content);
        Assert.Contains("generating", persisted.MetadataJson);
        await using var pcm = await fixture.Voice.StartSegmentSpeechAsync(new(first.TurnId!.Value, Guid.NewGuid(), persisted.Id, first.SegmentId!.Value, 0));
        Assert.Equal("Ora, tudo certo.", Assert.Single(fixture.Speech.Requests).Text);
        fixture.Voice.CompleteSpeech(pcm.SpeechRequestId);
        fixture.Model.Release.TrySetResult();
        Assert.True(await events.MoveNextAsync());
        Assert.Equal("final_segment", events.Current.Type);
        Assert.Equal(1, events.Current.Sequence);
        var second = events.Current;
        Assert.True(await events.MoveNextAsync());
        Assert.Equal("done", events.Current.Type);
        Assert.Equal("continue", events.Current.VoiceContinuation);
        Assert.False(await events.MoveNextAsync());
        Assert.Equal("Ora, tudo certo. Quer conferir mais alguma coisa?", persisted.Content);
        Assert.Contains("complete", persisted.MetadataJson);
        Assert.Single(await fixture.Db.ChatMessages.Where(x => x.Role == "assistant").ToListAsync());
        Assert.Single(fixture.Model.Requests);
        Assert.False(fixture.Voice.TryCompleteTurnWithoutSpeech(first.TurnId.Value));
        await using var lastPcm = await fixture.Voice.StartSegmentSpeechAsync(new(second.TurnId!.Value, Guid.NewGuid(), persisted.Id, second.SegmentId!.Value, 1));
        fixture.Voice.CompleteSpeech(lastPcm.SpeechRequestId);
        Assert.True(fixture.Voice.TryCompleteTurnWithoutSpeech(first.TurnId.Value));
        Assert.True(fixture.Voice.TryCompleteTurnWithoutSpeech(first.TurnId.Value));
    }

    [Fact]
    public async Task PartialFailurePreservesSpokenPrefixAndCancelsAllUpstreamSegments()
    {
        using var fixture = new Fixture();
        fixture.Model.FailAfterFirst = true;
        await using var events = fixture.Chat.StreamMessageAsync(fixture.Request()).GetAsyncEnumerator();
        await events.MoveNextAsync();
        await events.MoveNextAsync();
        var first = events.Current;
        await using var pcm = await fixture.Voice.StartSegmentSpeechAsync(new(first.TurnId!.Value, Guid.NewGuid(), first.AssistantMessageId!.Value, first.SegmentId!.Value, 0));
        fixture.Voice.CompleteSpeech(pcm.SpeechRequestId);
        fixture.Model.Release.TrySetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await events.MoveNextAsync());
        var message = await fixture.Db.ChatMessages.SingleAsync(x => x.Role == "assistant");
        Assert.Equal(first.Content, message.Content);
        Assert.Contains("interrupted", message.MetadataJson);
        Assert.True(pcm.TurnCancellationToken.IsCancellationRequested);
        Assert.Contains(fixture.Speech.Requests[0].NativeRequestId, fixture.Speech.Cancelled);
        Assert.Null(fixture.Registry.FindSpeechSegment(first.TurnId.Value, first.SegmentId.Value));
    }

    [Fact]
    public async Task RejectedDuplicateSpeechRequestCannotCancelItsOriginalOwner()
    {
        using var fixture = new Fixture();
        await using var events = fixture.Chat.StreamMessageAsync(fixture.Request()).GetAsyncEnumerator();
        await events.MoveNextAsync();
        await events.MoveNextAsync();
        var segment = events.Current;
        var request = new StartSegmentSpeechRequest(segment.TurnId!.Value, Guid.NewGuid(), segment.AssistantMessageId!.Value, segment.SegmentId!.Value, 0);
        await using var pcm = await fixture.Voice.StartSegmentSpeechAsync(request);
        var controller = new VoiceSegmentsController(fixture.Voice)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        await controller.Speech(request, CancellationToken.None);
        Assert.Equal(409, controller.Response.StatusCode);
        Assert.False(pcm.TurnCancellationToken.IsCancellationRequested);
        Assert.Empty(fixture.Speech.Cancelled);
        Assert.True(fixture.Registry.IsCurrent(segment.ConversationId!.Value, segment.TurnId.Value));
    }

    [Fact]
    public async Task RequestCancellationDuringTtsAcquisitionCancelsTheTurnAndNativeUnit()
    {
        using var fixture = new Fixture();
        fixture.Speech.HoldAcquisition = true;
        await using var events = fixture.Chat.StreamMessageAsync(fixture.Request()).GetAsyncEnumerator();
        await events.MoveNextAsync();
        await events.MoveNextAsync();
        var segment = events.Current;
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.Voice.StartSegmentSpeechAsync(new(segment.TurnId!.Value, Guid.NewGuid(), segment.AssistantMessageId!.Value, segment.SegmentId!.Value, 0), cancellation.Token);
        await fixture.Speech.AcquisitionStarted.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(fixture.Registry.Find(segment.TurnId.Value)!.Cancellation.IsCancellationRequested);
        Assert.Contains(fixture.Speech.Requests[0].NativeRequestId, fixture.Speech.Cancelled);
    }

    [Fact]
    public void SegmentOwnershipOrderingReplayAndCapacityAreBounded()
    {
        using var metrics = new AegisMetrics();
        using var registry = new ActiveTurnRegistry(metrics);
        var turn = registry.Register(Guid.NewGuid(), Guid.NewGuid());
        registry.TryTransition(turn.TurnId, TurnStatus.Created, TurnStatus.GeneratingText);
        var message = Guid.NewGuid();
        var segments = Enumerable.Range(0, 4).Select(i => new FinalSpeechSegment(Guid.NewGuid(), i, message, $"Frase {i}.")).ToArray();
        Assert.False(registry.TryAuthorizeSpeechSegment(turn.TurnId, segments[1]));
        foreach (var segment in segments) Assert.True(registry.TryAuthorizeSpeechSegment(turn.TurnId, segment));
        Assert.False(registry.TryAuthorizeSpeechSegment(turn.TurnId, segments[0]));
        Assert.False(registry.TryBeginSegmentSpeech(turn.TurnId, segments[1].Id, Guid.NewGuid(), "out-of-order"));
        var request = Guid.NewGuid();
        Assert.True(registry.TryBeginSegmentSpeech(turn.TurnId, segments[0].Id, request, "first"));
        Assert.False(registry.TryBeginSegmentSpeech(turn.TurnId, segments[0].Id, Guid.NewGuid(), "replay"));
        Assert.True(registry.TryBeginSegmentSpeech(turn.TurnId, segments[1].Id, Guid.NewGuid(), "second"));
        Assert.False(registry.TryBeginSegmentSpeech(turn.TurnId, segments[2].Id, Guid.NewGuid(), "capacity"));
        registry.CompleteSegmentSpeech(request);
        Assert.True(registry.TryBeginSegmentSpeech(turn.TurnId, segments[2].Id, Guid.NewGuid(), "third"));
        Assert.False(registry.TryBeginSpeech(turn.TurnId, Guid.NewGuid(), "legacy-full-replay"));
        var cancellation = registry.CancelAndGetInfo(turn.TurnId, "test");
        Assert.Equal(new[] { "first", "second", "third" }, cancellation.SegmentNativeSpeechRequestIds);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task HandsFreeSkipsMandatorySemanticPrefetchButTextKeepsPolicy(bool handsFree, int expected)
    {
        using var fixture = new Fixture();
        var count = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, active) => { if (ReferenceEquals(instrument, fixture.Metrics.MemoryAutoContextRequests)) active.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => count += value);
        listener.Start();
        var hybrid = new MemoryHybridRetriever(null!, null!, null!, new MemoryGraphOptions(), new MemorySemanticOptions(), new MemoryAutoContextOptions(), TimeProvider.System, fixture.Metrics);
        var chat = fixture.CreateChat(hybrid, new MemoryAutomaticOptions { Enabled = true });
        var request = new SendMessageRequest { Content = "Pergunta simples inédita.", Origin = handsFree ? "hands_free" : null, VoiceSessionId = Guid.NewGuid() };
        await chat.SendMessageAsync(request);
        Assert.Equal(expected, count);
        Assert.Single(await fixture.Db.MemoryExtractionJobs.ToListAsync());
        if (handsFree) Assert.Contains("memory_search", fixture.Model.Requests[0].Request.InputItems!.Last().GetRawText());
    }

    private sealed class Fixture : IDisposable
    {
        public AegisDbContext Db { get; } = new(new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public AegisMetrics Metrics { get; } = new();
        public ActiveTurnRegistry Registry { get; }
        public SpeechClient Speech { get; } = new();
        public SegmentModel Model { get; } = new();
        public VoiceService Voice { get; }
        public ChatService Chat { get; }
        private readonly IConversationTitleJobQueue titles;
        public Fixture()
        {
            Registry = new ActiveTurnRegistry(Metrics);
            Voice = new VoiceService(Db, Registry, Speech, new VoiceStatusCache(Speech));
            titles = DispatchProxy.Create<IConversationTitleJobQueue, HandsFreeVoiceTests.Proxy>();
            ((HandsFreeVoiceTests.Proxy)(object)titles).Call = (_, _) => ValueTask.CompletedTask;
            Chat = CreateChat();
        }
        public ChatService CreateChat(MemoryHybridRetriever? hybrid = null, MemoryAutomaticOptions? automatic = null) => new(Db, new PromptBuilder(new Runtime()),
            new AegisToolLoop(Model, new AegisToolRegistry([]), NullLogger<AegisToolLoop>.Instance, Metrics), titles, Registry, Voice,
            hybrid: hybrid, automaticMemory: automatic);
        public SendMessageRequest Request() => new() { TurnId = Guid.NewGuid(), Content = "Como vai?", Origin = "hands_free", VoiceSessionId = Guid.NewGuid(), VoiceSegments = true };
        public void Dispose() { Db.Dispose(); Registry.Dispose(); Metrics.Dispose(); }
    }
    private sealed class Runtime : IRuntimeContextProvider
    {
        public Task<string> GetRuntimeContextAsync(CancellationToken cancellationToken = default) => Task.FromResult("Horário atual: 2026-10-31 12:00.");
    }
    private sealed class SpeechClient : IAegisSpeechClient
    {
        public List<SpeechRequest> Requests { get; } = [];
        public List<string> Cancelled { get; } = [];
        public bool HoldAcquisition;
        public TaskCompletionSource AcquisitionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<SpeechStreamResponse> StreamSpeechAsync(SpeechRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            AcquisitionStarted.TrySetResult();
            if (HoldAcquisition) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new SpeechStreamResponse(new MemoryStream([0, 1, 0, 2]), new("pcm_s16le", 24000, 1, "test"));
        }
        public Task CancelAsync(string nativeSpeechRequestId, CancellationToken cancellationToken = default) { Cancelled.Add(nativeSpeechRequestId); return Task.CompletedTask; }
        public Task<SpeechServiceStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new SpeechServiceStatus(true, true, "test", 24000, 1));
    }
    private sealed class SegmentModel : IAegisModelClient
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Finished, FailAfterFirst;
        public List<ModelToolRequest> Requests { get; } = [];
        private static readonly LlmRequestAuditData Audit = new("test", "test", true, 1, "{}", 200, "{}", null, null);
        public Task<ModelToolResponse> RespondWithToolsAsync(ModelToolRequest request, CancellationToken cancellationToken = default)
        { Requests.Add(request); return Task.FromResult(new ModelToolResponse("Resposta natural.", "test", "test", ModelPurpose.Chat, [], [], null, null, Audit)); }
        public async IAsyncEnumerable<ModelToolStreamChunk> RespondWithToolsStreamAsync(ModelToolRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            yield return new(null, false, [], [], FinalSpeechSegment: "Ora, tudo certo.");
            await Release.Task.WaitAsync(cancellationToken);
            if (FailAfterFirst) throw new InvalidOperationException("Controlled provider failure.");
            yield return new(null, false, [], [], FinalSpeechSegment: "Quer conferir mais alguma coisa?");
            Finished = true;
            yield return new(null, true, [new("terminal", FinalSpeechSegments.ToolName, JsonSerializer.SerializeToElement(new { segments = new[] { "Ora, tudo certo.", "Quer conferir mais alguma coisa?" }, continuation = "continue" }))], [], Provider: "test", Model: "test", Purpose: ModelPurpose.Chat, AuditData: Audit);
        }
    }
}
