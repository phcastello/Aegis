using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Aegis.Api.Controllers;
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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
                Assert.NotNull(result);
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
            Assert.False(string.IsNullOrWhiteSpace(request.Request.ReplyStyleInstructions));
            Assert.Contains(request.Request.ReplyStyleInstructions!, request.Request.Instructions);
            Assert.Equal("hands_free", request.Request.Metadata!["origin"]);
            Assert.Equal(session.ToString("D"), request.Request.Metadata["voice_session_id"]);
            var context = Assert.Single(request.Request.InputItems!, item =>
                item.TryGetProperty("content", out var text) && text.ValueKind == JsonValueKind.String &&
                text.GetString()!.Contains("Origem operacional: hands_free")).GetProperty("content").GetString()!;
            Assert.Contains("tela do dispositivo pode estar bloqueada", context);
            Assert.Contains("Comece direto pela conclusão ou providência principal", context);
            Assert.Contains("Use a mesma personalidade e o mesmo critério de humor do chat.", context);
            Assert.Contains("vá até o fim da resposta", context);
            Assert.DoesNotContain("Android", context);
            Assert.DoesNotContain("Windows", context);
        });
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task IgnoringBackgroundCreatesNoTurnOrSideEffects(bool streaming, bool existingConversation, bool mixedBatch)
    {
        using var db = new AegisDbContext(new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        using var metrics = new AegisMetrics();
        using var registry = new ActiveTurnRegistry(metrics);
        var tool = new BenignTool();
        var ignore = new ModelToolCall("ignore", HandsFreeDirection.IgnoreToolName, JsonSerializer.SerializeToElement(new { }));
        var model = new Model([Response(mixedBatch
            ? [ignore, new("unwanted-action", tool.Name, JsonSerializer.SerializeToElement(new { }))]
            : [ignore], mixedBatch ? "Texto indevido" : "")]);
        var loop = new AegisToolLoop(model, new AegisToolRegistry([tool]), NullLogger<AegisToolLoop>.Instance, metrics);
        var priorTurnId = Guid.NewGuid();
        Guid? conversationId = null;
        DateTimeOffset? priorUpdatedAt = null;
        if (existingConversation)
        {
            var conversation = new Aegis.Domain.Entities.Conversation("Conversa de teste");
            conversationId = conversation.Id;
            db.AddConversation(conversation);
            db.AddChatMessage(conversation.AddMessage(Aegis.Domain.ChatRoles.User, "Minha agenda"));
            db.AddChatMessage(conversation.AddMessage(Aegis.Domain.ChatRoles.Assistant, new string('x', 700) + "Quer que eu liste?"));
            await db.SaveChangesAsync();
            priorUpdatedAt = conversation.UpdatedAt;
            registry.Register(priorTurnId, conversation.Id);
        }
        var chat = new ChatService(db, new PromptBuilder(new Runtime()), loop, null!, registry, null!,
            automaticMemory: new() { Enabled = true });
        var request = new SendMessageRequest { TurnId = Guid.NewGuid(), ConversationId = conversationId, Origin = "hands_free",
            VoiceSessionId = Guid.NewGuid(), AllowSilentIgnore = true, Content = "Aegis, João, fecha a porta" };

        if (streaming)
        {
            var events = new List<ChatStreamEvent>();
            await foreach (var item in chat.StreamMessageAsync(request)) events.Add(item);
            var ignored = Assert.Single(events);
            Assert.Equal("ignored", ignored.Type);
            Assert.Equal(request.TurnId, ignored.TurnId);
            Assert.Null(ignored.ConversationId);
            Assert.Null(ignored.MessageId);
        }
        else Assert.Null(await chat.SendMessageAsync(request));

        Assert.Null(registry.Find(request.TurnId!.Value));
        Assert.Equal(0, tool.Calls);
        var modelRequest = Assert.Single(model.Requests);
        Assert.Equal("João, fecha a porta", modelRequest.Request.Input);
        Assert.Contains(modelRequest.Tools, definition => definition.Name == HandsFreeDirection.IgnoreToolName);
        Assert.Contains(modelRequest.Request.InputItems!, item => item.ToString().Contains("hands_free_ignore"));
        if (existingConversation)
        {
            Assert.Contains(modelRequest.Request.InputItems!, item => item.ToString().Contains("Quer que eu liste?"));
            Assert.True(registry.IsCurrent(conversationId!.Value, priorTurnId));
        }
        await db.SaveChangesAsync(); // Even a later save on the same context cannot persist a discarded turn.
        if (existingConversation)
            Assert.Equal(priorUpdatedAt, (await db.Conversations.SingleAsync()).UpdatedAt);
        Assert.Equal(existingConversation ? 1 : 0, await db.Conversations.CountAsync());
        Assert.Equal(existingConversation ? 2 : 0, await db.ChatMessages.CountAsync());
        Assert.Empty(await db.MemoryExtractionJobs.ToListAsync());
        Assert.Empty(await db.LlmRequestAudits.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WakeOnlyOptInIsSilentWithoutCallingModel(bool streaming)
    {
        var chat = new ChatService(null!, null!, null!, null!, null!, null!);
        var request = new SendMessageRequest { TurnId = Guid.NewGuid(), Origin = "hands_free",
            VoiceSessionId = Guid.NewGuid(), AllowSilentIgnore = true, Content = "Aegis?!" };
        if (streaming)
        {
            var events = new List<ChatStreamEvent>();
            await foreach (var item in chat.StreamMessageAsync(request)) events.Add(item);
            Assert.Equal("ignored", Assert.Single(events).Type);
        }
        else Assert.Null(await chat.SendMessageAsync(request));
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public async Task LegitimateAndLegacyRequestsStillReachNormalConversation(bool streaming, bool handsFree, bool optIn)
    {
        using var db = new AegisDbContext(new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        using var metrics = new AegisMetrics();
        using var registry = new ActiveTurnRegistry(metrics);
        var voice = DispatchProxy.Create<IVoiceService, Proxy>();
        ((Proxy)(object)voice).Call = (_, args) => Task.FromResult(registry.Register((Guid)args![0]!, (Guid)args[1]!));
        var titles = DispatchProxy.Create<IConversationTitleJobQueue, Proxy>();
        ((Proxy)(object)titles).Call = (_, _) => ValueTask.CompletedTask;
        var model = new Model([Response([], "Resposta normal")]);
        var loop = new AegisToolLoop(model, new AegisToolRegistry([]), NullLogger<AegisToolLoop>.Instance, metrics);
        var chat = new ChatService(db, new PromptBuilder(new Runtime()), loop, titles, registry, voice);
        model.OnRespond = () => Assert.Equal(handsFree && optIn ? 0 : 1, db.ChatMessages.Count());
        var request = new SendMessageRequest { TurnId = Guid.NewGuid(), Origin = handsFree ? "hands_free" : null,
            VoiceSessionId = handsFree ? Guid.NewGuid() : null, AllowSilentIgnore = optIn, Content = "Sim" };
        if (streaming)
        {
            var events = new List<ChatStreamEvent>();
            await foreach (var item in chat.StreamMessageAsync(request)) events.Add(item);
            Assert.Equal("done", events.Last().Type);
        }
        else Assert.NotNull(await chat.SendMessageAsync(request));
        var modelRequest = Assert.Single(model.Requests);
        var ignore = modelRequest.Tools.SingleOrDefault(t => t.Name == HandsFreeDirection.IgnoreToolName);
        Assert.Equal(handsFree && optIn, ignore is not null);
        var inputTexts = modelRequest.Request.InputItems?
            .Where(item => item.TryGetProperty("content", out var text) && text.ValueKind == JsonValueKind.String)
            .Select(item => item.GetProperty("content").GetString()!).ToArray() ?? [];
        var direction = inputTexts.SingleOrDefault(text => text.Contains(HandsFreeDirection.Guidance, StringComparison.Ordinal));
        Assert.Equal(handsFree && optIn, direction is not null);
        if (direction is not null)
        {
            // This is a model-request contract regression, not a claim that
            // the mock model semantically understands an utterance.
            Assert.Contains("sem intenção conversacional reconhecível E sem relação plausível com o contexto recente", direction);
            Assert.Contains("não exige esclarecimento", direction);
            Assert.Contains("Não descarte pelo número de palavras, pela pontuação, pelo idioma ou pela presença de um termo técnico", direction);
            Assert.Contains("'pare', 'hora?', 'agenda' e 'volume'", direction);
            Assert.Contains("um termo isolado em resposta a uma pergunta ou em ditado devem ser atendidos", direction);
            Assert.Contains("na dúvida razoável, atenda normalmente", direction);
            Assert.Contains("sem intenção conversacional reconhecível e sem relação plausível com o contexto recente", ignore!.Description);
            Assert.Contains("Não use para pedidos, respostas, relatos ou continuações plausíveis, mesmo curtos", ignore.Description);
        }
        Assert.Single(await db.Conversations.ToListAsync());
        Assert.Equal(2, await db.ChatMessages.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TurnCancelledDuringFirstResponseNeverPersists(bool streaming)
    {
        using var db = new AegisDbContext(new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        using var metrics = new AegisMetrics();
        using var registry = new ActiveTurnRegistry(metrics);
        var voice = DispatchProxy.Create<IVoiceService, Proxy>();
        ((Proxy)(object)voice).Call = (_, args) => Task.FromResult(registry.Register((Guid)args![0]!, (Guid)args[1]!));
        var turn = Guid.NewGuid();
        var model = new Model([Reply("close")]) { OnRespond = () => registry.CancelAndGetInfo(turn, "test") };
        var loop = new AegisToolLoop(model, new AegisToolRegistry([]), NullLogger<AegisToolLoop>.Instance, metrics);
        var chat = new ChatService(db, new PromptBuilder(new Runtime()), loop, null!, registry, voice);
        var request = new SendMessageRequest { TurnId = turn, Origin = "hands_free", VoiceSessionId = Guid.NewGuid(),
            AllowSilentIgnore = true, Content = "Sim" };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (streaming) { await foreach (var _ in chat.StreamMessageAsync(request)) { } }
            else await chat.SendMessageAsync(request);
        });
        db.ChangeTracker.Clear();
        Assert.Empty(await db.Conversations.ToListAsync());
        Assert.Empty(await db.ChatMessages.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedToolFirstRoundPersistsBeforeActionAndNeverReoffersIgnore(bool streaming)
    {
        using var db = new AegisDbContext(new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        using var metrics = new AegisMetrics();
        using var registry = new ActiveTurnRegistry(metrics);
        var voice = DispatchProxy.Create<IVoiceService, Proxy>();
        ((Proxy)(object)voice).Call = (_, args) => Task.FromResult(registry.Register((Guid)args![0]!, (Guid)args[1]!));
        var titles = DispatchProxy.Create<IConversationTitleJobQueue, Proxy>();
        ((Proxy)(object)titles).Call = (_, _) => ValueTask.CompletedTask;
        var tool = new BenignTool { OnExecute = context =>
        {
            Assert.Single(db.Conversations);
            Assert.Equal(context.UserMessageId, db.ChatMessages.Single().Id);
        }};
        var model = new Model([Response([new("act", tool.Name, JsonSerializer.SerializeToElement(new { }))]), Reply("close")]);
        var loop = new AegisToolLoop(model, new AegisToolRegistry([tool]), NullLogger<AegisToolLoop>.Instance, metrics);
        var chat = new ChatService(db, new PromptBuilder(new Runtime()), loop, titles, registry, voice);
        var request = new SendMessageRequest { TurnId = Guid.NewGuid(), Origin = "hands_free", VoiceSessionId = Guid.NewGuid(),
            AllowSilentIgnore = true, Content = "Me diga a agenda" };
        if (streaming)
        {
            var events = new List<ChatStreamEvent>();
            await foreach (var item in chat.StreamMessageAsync(request)) events.Add(item);
            Assert.Equal("conversation", events[0].Type);
            Assert.Equal("done", events[^1].Type);
        }
        else Assert.NotNull(await chat.SendMessageAsync(request));

        Assert.Equal(1, tool.Calls);
        Assert.Equal(2, model.Requests.Count);
        Assert.Contains(model.Requests[0].Tools, t => t.Name == HandsFreeDirection.IgnoreToolName);
        Assert.DoesNotContain(model.Requests[1].Tools, t => t.Name == HandsFreeDirection.IgnoreToolName);
        Assert.Equal(2, await db.ChatMessages.CountAsync());
    }

    [Fact]
    public async Task SegmentedAcceptedFirstRoundStillStreamsBeforeModelCompletion()
    {
        using var db = new AegisDbContext(new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        using var metrics = new AegisMetrics();
        using var registry = new ActiveTurnRegistry(metrics);
        var voice = DispatchProxy.Create<IVoiceService, Proxy>();
        ((Proxy)(object)voice).Call = (_, args) => Task.FromResult(registry.Register((Guid)args![0]!, (Guid)args[1]!));
        var titles = DispatchProxy.Create<IConversationTitleJobQueue, Proxy>();
        ((Proxy)(object)titles).Call = (_, _) => ValueTask.CompletedTask;
        var model = new Model([Response([new("speak", FinalSpeechSegments.ToolName,
            JsonSerializer.SerializeToElement(new { segments = new[] { "Primeira.", "Segunda." }, continuation = "close" }))])])
            { EarlySegment = "Primeira." };
        var loop = new AegisToolLoop(model, new AegisToolRegistry([]), NullLogger<AegisToolLoop>.Instance, metrics);
        var chat = new ChatService(db, new PromptBuilder(new Runtime()), loop, titles, registry, voice);
        var request = new SendMessageRequest { TurnId = Guid.NewGuid(), Origin = "hands_free", VoiceSessionId = Guid.NewGuid(),
            VoiceSegments = true, AllowSilentIgnore = true, Content = "Conte uma história longa" };
        var events = new List<ChatStreamEvent>();
        await foreach (var item in chat.StreamMessageAsync(request))
        {
            events.Add(item);
            if (item.Type == "final_segment" && events.Count == 2)
            {
                Assert.False(model.Completed);
                Assert.Equal(2, db.ChatMessages.Count());
            }
        }
        Assert.Equal(new[] { "conversation", "final_segment", "final_segment", "done" }, events.Select(x => x.Type));
        Assert.Single(model.Requests);
        Assert.Equal(2, db.ChatMessages.Count());
    }

    [Fact]
    public async Task ThinkingInterruptionBeforeAcceptanceKeepsThePriorRequestWithoutMakingAPhantomChat()
    {
        using var db = new AegisDbContext(new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        using var metrics = new AegisMetrics();
        using var registry = new ActiveTurnRegistry(metrics);
        var prior = Guid.NewGuid(); var session = Guid.NewGuid();
        var model = new Model([SegmentReply(), SegmentReply()]);
        model.OnRespond = () => { if (model.Requests.Count == 1) registry.CancelAndGetInfo(prior, "interrupted"); };
        var loop = new AegisToolLoop(model, new AegisToolRegistry([]), NullLogger<AegisToolLoop>.Instance, metrics);
        var chat = new ChatService(db, new PromptBuilder(new Runtime()), loop, Titles(), registry, HandoffVoice(registry));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in chat.StreamMessageAsync(VoiceRequest(prior, session, "Conte uma história."))) { }
        });
        db.ChangeTracker.Clear();
        Assert.Empty(await db.Conversations.ToListAsync());
        Assert.Empty(await db.ChatMessages.ToListAsync());
        var correction = VoiceRequest(Guid.NewGuid(), session, "Na verdade, curta.", new(prior, "fallback não deve prevalecer"));
        var events = new List<ChatStreamEvent>();
        await foreach (var item in chat.StreamMessageAsync(correction)) events.Add(item);
        Assert.Equal("done", events[^1].Type);
        Assert.Single(await db.Conversations.ToListAsync());
        var user = await db.ChatMessages.SingleAsync(x => x.Role == Aegis.Domain.ChatRoles.User);
        Assert.Contains("Conte uma história.", user.Content);
        Assert.Contains("Na verdade, curta.", user.Content);
        Assert.DoesNotContain("fallback não deve prevalecer", user.Content);
    }

    [Fact]
    public async Task ThinkingInterruptionAfterFirstSegmentReusesCommittedConversationAndMarksZeroPlayed()
    {
        using var db = new AegisDbContext(new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        using var metrics = new AegisMetrics();
        using var registry = new ActiveTurnRegistry(metrics);
        var prior = Guid.NewGuid(); var session = Guid.NewGuid();
        var model = new Model([SegmentReply(), SegmentReply()]) { EarlySegment = "Primeira." };
        var loop = new AegisToolLoop(model, new AegisToolRegistry([]), NullLogger<AegisToolLoop>.Instance, metrics);
        var chat = new ChatService(db, new PromptBuilder(new Runtime()), loop, Titles(), registry, HandoffVoice(registry));
        Guid destination;
        await using (var initial = chat.StreamMessageAsync(VoiceRequest(prior, session, "Conte uma história.")).GetAsyncEnumerator())
        {
            Assert.True(await initial.MoveNextAsync());
            Assert.Equal("conversation", initial.Current.Type);
            destination = initial.Current.ConversationId!.Value;
            Assert.True(await initial.MoveNextAsync());
            Assert.Equal("final_segment", initial.Current.Type);
            // Client never rendered audio, and may not even have read the conversation marker.
        }
        db.ChangeTracker.Clear();
        var correction = VoiceRequest(Guid.NewGuid(), session, "Na verdade, curta.", new(prior, "Conte uma história."));
        var events = new List<ChatStreamEvent>();
        await foreach (var item in chat.StreamMessageAsync(correction)) events.Add(item);
        Assert.Equal(destination, events[0].ConversationId);
        Assert.Single(await db.Conversations.ToListAsync());
        Assert.Equal(2, await db.ChatMessages.CountAsync(x => x.Role == Aegis.Domain.ChatRoles.User));
        var next = await db.ChatMessages.SingleAsync(x => x.Content == "Na verdade, curta.");
        Assert.Contains("\"playedFramesInSegment\":0", next.MetadataJson);
        Assert.Contains(model.Requests[1].InputItems!, x => x.GetRawText().Contains("milissegundosOuvidosNoTrecho"));
    }

    [Fact]
    public async Task ThinkingHandoffRejectsAnotherVoiceSession()
    {
        using var db = new AegisDbContext(new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        using var metrics = new AegisMetrics(); using var registry = new ActiveTurnRegistry(metrics);
        var prior = Guid.NewGuid(); var session = Guid.NewGuid();
        var model = new Model([SegmentReply()]);
        model.OnRespond = () => registry.CancelAndGetInfo(prior, "test");
        var loop = new AegisToolLoop(model, new AegisToolRegistry([]), NullLogger<AegisToolLoop>.Instance, metrics);
        var chat = new ChatService(db, new PromptBuilder(new Runtime()), loop, Titles(), registry, HandoffVoice(registry));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        { await foreach (var _ in chat.StreamMessageAsync(VoiceRequest(prior, session, "Pedido anterior."))) { } });
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        { await foreach (var _ in chat.StreamMessageAsync(VoiceRequest(Guid.NewGuid(), Guid.NewGuid(), "Outro pedido.", new(prior)))) { } });
        var controller = new ChatController(chat, HandoffVoice(registry), NullLogger<ChatController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        await using var body = new MemoryStream(); controller.Response.Body = body;
        await controller.StreamMessage(VoiceRequest(Guid.NewGuid(), Guid.NewGuid(), "Outro pedido.", new(prior)), default);
        Assert.Equal(StatusCodes.Status400BadRequest, controller.Response.StatusCode);
        Assert.Contains("invalid_voice_generation_interruption", System.Text.Encoding.UTF8.GetString(body.ToArray()));
        Assert.Single(model.Requests);
    }

    [Fact]
    public async Task ThinkingInterruptionDoesNotResurrectAnExplicitlyIgnoredUtterance()
    {
        using var db = new AegisDbContext(new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        using var metrics = new AegisMetrics(); using var registry = new ActiveTurnRegistry(metrics);
        var prior = Guid.NewGuid(); var session = Guid.NewGuid();
        var ignore = new ModelToolCall("ignore", HandsFreeDirection.IgnoreToolName, JsonSerializer.SerializeToElement(new { }));
        var model = new Model([Response([ignore]), SegmentReply()]);
        var loop = new AegisToolLoop(model, new AegisToolRegistry([]), NullLogger<AegisToolLoop>.Instance, metrics);
        var chat = new ChatService(db, new PromptBuilder(new Runtime()), loop, Titles(), registry, HandoffVoice(registry));
        var discarded = new List<ChatStreamEvent>();
        await foreach (var item in chat.StreamMessageAsync(VoiceRequest(prior, session, "Conversa alheia."))) discarded.Add(item);
        Assert.Equal("ignored", Assert.Single(discarded).Type);
        // The native client may have missed the terminal ignored event and sent
        // a fallback; the authoritative receipt must not resurrect that content.
        await foreach (var _ in chat.StreamMessageAsync(VoiceRequest(Guid.NewGuid(), session, "Pedido legítimo.", new(prior, "Conversa alheia.")))) { }
        var user = await db.ChatMessages.SingleAsync(x => x.Role == Aegis.Domain.ChatRoles.User);
        Assert.Equal("Pedido legítimo.", user.Content);
        Assert.Single(await db.Conversations.ToListAsync());
    }

    [Fact]
    public async Task RepeatedUnacceptedGenerationHandoffsRetainEffectivePriorContentExactlyOnce()
    {
        using var db = new AegisDbContext(new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        using var metrics = new AegisMetrics(); using var registry = new ActiveTurnRegistry(metrics);
        var first = Guid.NewGuid(); var second = Guid.NewGuid(); var third = Guid.NewGuid(); var session = Guid.NewGuid();
        var model = new Model([SegmentReply(), SegmentReply(), SegmentReply()]);
        model.OnRespond = () =>
        {
            if (model.Requests.Count == 1) registry.CancelAndGetInfo(first, "interrupted");
            if (model.Requests.Count == 2) registry.CancelAndGetInfo(second, "interrupted");
        };
        var loop = new AegisToolLoop(model, new AegisToolRegistry([]), NullLogger<AegisToolLoop>.Instance, metrics);
        var chat = new ChatService(db, new PromptBuilder(new Runtime()), loop, Titles(), registry, HandoffVoice(registry));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        { await foreach (var _ in chat.StreamMessageAsync(VoiceRequest(first, session, "Primeiro pedido."))) { } });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        { await foreach (var _ in chat.StreamMessageAsync(VoiceRequest(second, session, "Segunda correção.", new(first, "fallback descartado")))) { } });
        db.ChangeTracker.Clear();
        Assert.Empty(await db.Conversations.ToListAsync());
        await foreach (var _ in chat.StreamMessageAsync(VoiceRequest(third, session, "Terceira correção.", new(second, "fallback descartado")))) { }
        var user = await db.ChatMessages.SingleAsync(x => x.Role == Aegis.Domain.ChatRoles.User);
        foreach (var text in new[] { "Primeiro pedido.", "Segunda correção.", "Terceira correção." })
            Assert.Equal(2, user.Content.Split(text, StringSplitOptions.None).Length);
        Assert.DoesNotContain("fallback descartado", user.Content);
        Assert.Single(await db.Conversations.ToListAsync());
        Assert.Equal(3, model.Requests.Count);
    }

    [Fact]
    public async Task HandoffWaitsForPriorStreamSettlementAndReusesPersistedConversation()
    {
        var options = new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using var priorContext = new AegisDbContext(options); using var nextContext = new AegisDbContext(options);
        using var metrics = new AegisMetrics(); using var registry = new ActiveTurnRegistry(metrics);
        var prior = Guid.NewGuid(); var session = Guid.NewGuid();
        ChatService Create(AegisDbContext db, Model model, IVoiceService? service = null) => new(db, new PromptBuilder(new Runtime()),
            new AegisToolLoop(model, new AegisToolRegistry([]), NullLogger<AegisToolLoop>.Instance, metrics),
            Titles(), registry, service ?? HandoffVoice(registry));
        var remoteCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<CancelTurnResult> WaitRemoteCleanup(CancelTurnResult result)
        {
            remoteCleanup.TrySetResult(); await releaseCleanup.Task; return result;
        }
        var oldVoice = HandoffVoice(registry);
        var proxy = (Proxy)(object)oldVoice; var original = proxy.Call;
        proxy.Call = (method, args) => method.Name == nameof(IVoiceService.CancelTurnAsync) && (string)args![1]! == "text_stream_interrupted"
            ? WaitRemoteCleanup(registry.CancelAndGetInfo((Guid)args[0]!, (string)args[1]!).Result)
            : original(method, args);
        var initial = Create(priorContext, new Model([SegmentReply()]) { EarlySegment = "Primeira." }, oldVoice)
            .StreamMessageAsync(VoiceRequest(prior, session, "Pedido anterior.")).GetAsyncEnumerator();
        Assert.True(await initial.MoveNextAsync());
        Assert.Equal("conversation", initial.Current.Type);
        var destination = initial.Current.ConversationId;
        Assert.True(await initial.MoveNextAsync());
        Assert.Equal("final_segment", initial.Current.Type);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellationRegistration = registry.Find(prior)!.Cancellation.Token.Register(() => cancelled.TrySetResult());
        var nextModel = new Model([SegmentReply()]);
        VoiceGenerationReceipt? preceding = null;
        nextModel.OnRespond = () => Assert.True(preceding?.Settled.IsCompleted == true,
            "The replacement model must not run before the old stream settles.");
        var nextChat = Create(nextContext, nextModel);
        var resumed = Task.Run(async () =>
        {
            var events = new List<ChatStreamEvent>();
            await foreach (var item in nextChat.StreamMessageAsync(VoiceRequest(Guid.NewGuid(), session, "Corrigindo.", new(prior, "fallback")))) events.Add(item);
            return events;
        });
        try
        {
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            preceding = registry.BeginVoiceGenerationHandoff(prior, session, destination).Receipt;
            Assert.NotNull(preceding);
            Assert.False(preceding.Settled.IsCompleted);
            Assert.False(resumed.IsCompleted);
            Assert.Empty(nextModel.Requests); // the first stream is still suspended at yield
        }
        finally
        {
            // Dispose the old stream, but deliberately keep its remote cleanup
            // pending. Database settlement must still allow the next generation.
            var disposal = initial.DisposeAsync().AsTask();
            try
            {
                await remoteCleanup.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.False(disposal.IsCompleted);
                var events = await resumed.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Equal(destination, events[0].ConversationId);
                Assert.Single(await nextContext.Conversations.ToListAsync());
                Assert.Equal(2, await nextContext.ChatMessages.CountAsync(x => x.Role == Aegis.Domain.ChatRoles.User));
                var newUser = await nextContext.ChatMessages.SingleAsync(x => x.Content == "Corrigindo.");
                Assert.Contains("\"playedFramesInSegment\":0", newUser.MetadataJson);
                Assert.False(disposal.IsCompleted);
            }
            finally { releaseCleanup.TrySetResult(); await disposal; }
        }
    }

    [Fact]
    public async Task MissingPriorReceiptUsesFallbackAndPreventsLatePriorPostFromCreatingChat()
    {
        using var db = new AegisDbContext(new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        using var metrics = new AegisMetrics(); using var registry = new ActiveTurnRegistry(metrics);
        var prior = Guid.NewGuid(); var session = Guid.NewGuid();
        var model = new Model([SegmentReply()]);
        var chat = new ChatService(db, new PromptBuilder(new Runtime()),
            new AegisToolLoop(model, new AegisToolRegistry([]), NullLogger<AegisToolLoop>.Instance, metrics), Titles(), registry, HandoffVoice(registry));
        await foreach (var _ in chat.StreamMessageAsync(VoiceRequest(Guid.NewGuid(), session, "Nova fala.", new(prior, "Pedido anterior.")))) { }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        { await foreach (var _ in chat.StreamMessageAsync(VoiceRequest(prior, session, "Pedido anterior."))) { } });
        Assert.Single(model.Requests);
        var user = await db.ChatMessages.SingleAsync(x => x.Role == Aegis.Domain.ChatRoles.User);
        Assert.Equal("Pedido anterior.\nNova fala.", user.Content);
        Assert.Equal("Pedido anterior interrompido: Pedido anterior.\nNova fala (complemento ou correção): Nova fala.",
            Assert.Single(model.Requests).Request.Input);
        Assert.Single(await db.Conversations.ToListAsync());
    }

    [Theory]
    [InlineData("resume", "Professora, repete a explicação?")]
    [InlineData("stop", "Beleza, era só isso.")]
    public async Task TemporaryVoiceDecisionKeepsOriginalTurnAndHistory(string disposition, string currentSpeech)
    {
        using var db = new AegisDbContext(new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        using var metrics = new AegisMetrics(); using var registry = new ActiveTurnRegistry(metrics);
        using var pending = new CancellationTokenSource();
        var predecessor = Guid.NewGuid(); var session = Guid.NewGuid(); var conversation = Guid.NewGuid();
        var prior = new VoiceGenerationReceipt(session, conversation, Guid.NewGuid(), "Pedido original.", null);
        using var reservation = registry.TrackPendingTurn(predecessor, conversation, pending, prior);
        var original = registry.Register(predecessor, conversation);
        var model = new Model([InterruptionDecision(disposition)]);
        var decisions = new VoiceInterruptionDecisionService(model, registry, db, new(TimeProvider.System));
        var candidate = new VoiceInterruptionRequest(session, null, currentSpeech, new(predecessor));

        var quiet = await decisions.ClassifyAsync(candidate with { Content = " " }, null, default);
        Assert.Equal("resume", quiet.Disposition);
        Assert.Empty(model.Requests);
        await Assert.ThrowsAsync<ArgumentException>(() => decisions.ClassifyAsync(
            candidate with { VoiceSessionId = Guid.NewGuid() }, null, default));
        Assert.Empty(model.Requests);

        var result = await decisions.ClassifyAsync(candidate, null, default);
        Assert.Equal(disposition, result.Disposition);
        Assert.Null(result.Receipt);
        Assert.False(original.Cancellation.IsCancellationRequested);
        Assert.False(pending.IsCancellationRequested);
        Assert.True(registry.IsCurrent(conversation, predecessor));
        Assert.Empty(await db.ChatMessages.ToListAsync());
        var request = Assert.Single(model.Requests);
        Assert.Equal(VoiceInterruptionDecisionService.ToolName, Assert.Single(request.Tools).Name);
        using var speech = JsonDocument.Parse(request.Request.Input);
        Assert.Equal(currentSpeech, speech.RootElement.GetProperty("falaAtual").GetString());
        Assert.Equal("Pedido original.", speech.RootElement.GetProperty("pedidoAnterior").GetString());

        var assistant = Guid.NewGuid();
        var firstSegment = Guid.NewGuid();
        var speechRequest = Guid.NewGuid();
        Assert.True(registry.TryTransition(predecessor, TurnStatus.Created, TurnStatus.GeneratingText));
        Assert.True(registry.TryAuthorizeSpeechSegment(predecessor, new(firstSegment, 0, assistant, "Primeira parte.")));
        Assert.True(registry.TryAuthorizeSpeechSegment(predecessor, new(Guid.NewGuid(), 1, assistant, "Última parte.")));
        Assert.True(registry.TryBeginSegmentSpeech(predecessor, firstSegment, speechRequest, "fixture"));
        Assert.True(registry.TrySetTextCompleted(predecessor, assistant));
        Assert.False(registry.TryCompleteWithoutSpeech(predecessor));
        Assert.True(registry.CompleteSegmentSpeech(speechRequest));
        Assert.Equal(disposition == "stop", registry.TryCompleteWithoutSpeech(predecessor));
    }

    [Theory]
    [InlineData("Só os de amanhã.")]
    [InlineData("Aegis é o nome que eu quero usar.")]
    public async Task ApprovedInterruptionIsBoundToExactSpeechAndRemovesHistoryLabels(string spoken)
    {
        using var db = new AegisDbContext(new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        using var metrics = new AegisMetrics(); using var registry = new ActiveTurnRegistry(metrics);
        var predecessor = Guid.NewGuid(); var session = Guid.NewGuid();
        var receipts = new VoiceInterruptionReceipts(TimeProvider.System);
        var model = new Model([InterruptionDecision("replace"), SegmentReply()]);
        var loop = new AegisToolLoop(model, new AegisToolRegistry([]), NullLogger<AegisToolLoop>.Instance, metrics);
        var chat = new ChatService(db, new PromptBuilder(new Runtime()), loop, Titles(), registry, HandoffVoice(registry),
            interruptionReceipts: receipts);
        var previous = new VoiceGenerationInterruption(predecessor, "Quero saber os horários.");
        var candidate = new VoiceInterruptionRequest(session, null, spoken, previous);
        var decision = await new VoiceInterruptionDecisionService(model, registry, db, receipts)
            .ClassifyAsync(candidate, null, default);
        Assert.Equal("replace", decision.Disposition);
        Assert.NotNull(decision.Receipt);
        Assert.Empty(await db.ChatMessages.ToListAsync());

        SendMessageRequest Request(string content) => new()
        {
            TurnId = Guid.NewGuid(), VoiceSessionId = session, Origin = "hands_free", AllowSilentIgnore = true,
            VoiceSegments = true, Content = content, InterruptedGeneration = previous, InterruptionReceipt = decision.Receipt
        };
        async Task Consume(SendMessageRequest request)
        {
            await foreach (var _ in chat.StreamMessageAsync(request)) { }
        }
        await Assert.ThrowsAsync<ArgumentException>(() => Consume(Request("Outra coisa.")));
        Assert.Single(model.Requests);
        await Consume(Request(spoken));
        await Assert.ThrowsAsync<ArgumentException>(() => Consume(Request(spoken)));
        var user = await db.ChatMessages.SingleAsync(message => message.Role == Aegis.Domain.ChatRoles.User);
        Assert.Equal($"Quero saber os horários.\n{spoken}", user.Content);
        Assert.Equal(2, model.Requests.Count);
        Assert.DoesNotContain(model.Requests[1].Tools, tool => tool.Name == HandsFreeDirection.IgnoreToolName);
        Assert.Contains($"Nova fala (complemento ou correção): {spoken}", model.Requests[1].Request.Input);

        var historical = new Aegis.Domain.Entities.ChatMessage(user.ConversationId, Aegis.Domain.ChatRoles.User,
            "Pedido anterior interrompido: Mensagem antiga.\nNova fala (complemento ou correção): Correção antiga.");
        db.ChatMessages.Add(historical);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var conversation = await chat.GetConversationAsync(user.ConversationId);
        Assert.Equal("Mensagem antiga.\nCorreção antiga.",
            Assert.Single(conversation!.Messages, message => message.Id == historical.Id).Content);
    }

    private static ModelToolResponse InterruptionDecision(string disposition) => Response(
        [new("interruption", VoiceInterruptionDecisionService.ToolName, JsonSerializer.SerializeToElement(new { disposition }))]);

    [Fact]
    public void ThinkingHandoffRequiresTheSegmentedHandsFreeContractAndDifferentTurn()
    {
        var id = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => HandsFreeReply.UserContent(new() { Content = "Oi", InterruptedGeneration = new(id) }));
        Assert.Throws<ArgumentException>(() => HandsFreeReply.UserContent(VoiceRequest(id, Guid.NewGuid(), "Oi", new(id))));
        Assert.Throws<ArgumentException>(() => HandsFreeReply.UserContent(new() { Origin = HandsFreeReply.Origin, VoiceSessionId = Guid.NewGuid(), VoiceSegments = true, AllowSilentIgnore = true, Content= "Oi", InterruptedGeneration = new(id) }));
        Assert.Throws<ArgumentException>(() => HandsFreeReply.UserContent(VoiceRequest(Guid.NewGuid(), Guid.NewGuid(), "Oi", new(id, new string('x', 16385)))));
    }

    private static SendMessageRequest VoiceRequest(Guid turn, Guid session, string content, VoiceGenerationInterruption? handoff = null) => new()
    {
        TurnId = turn, VoiceSessionId = session, Origin = "hands_free", AllowSilentIgnore = true, VoiceSegments = true,
        Content = content, InterruptedGeneration = handoff
    };
    private static ModelToolResponse SegmentReply() => Response([new("speak", FinalSpeechSegments.ToolName,
        JsonSerializer.SerializeToElement(new { segments = new[] { "Primeira.", "Segunda." }, continuation = "close" }))]);
    private static IConversationTitleJobQueue Titles()
    {
        var titles = DispatchProxy.Create<IConversationTitleJobQueue, Proxy>();
        ((Proxy)(object)titles).Call = (_, _) => ValueTask.CompletedTask;
        return titles;
    }
    private static IVoiceService HandoffVoice(ActiveTurnRegistry registry)
    {
        var voice = DispatchProxy.Create<IVoiceService, Proxy>();
        ((Proxy)(object)voice).Call = (method, args) => method.Name switch
        {
            nameof(IVoiceService.RegisterTurnAsync) => Task.FromResult(registry.Register((Guid)args![0]!, (Guid)args[1]!)),
            nameof(IVoiceService.CancelTurnAsync) => Task.FromResult(registry.CancelAndGetInfo((Guid)args![0]!, (string)args[1]!).Result),
            nameof(IVoiceService.CancelVoiceGenerationAsync) => Task.FromResult(registry.BeginVoiceGenerationHandoff(
                (Guid)args![0]!, (Guid)args[1]!, (Guid?)args[2], (CancellationToken)args[3]!).Receipt),
            _ => throw new NotSupportedException()
        };
        return voice;
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
        public Action? OnRespond { get; set; }
        public string? EarlySegment { get; init; }
        public bool Completed { get; private set; }
        public Task<ModelToolResponse> RespondWithToolsAsync(ModelToolRequest request, CancellationToken cancellationToken = default)
        {
            var response = responses[Requests.Count];
            Requests.Add(request);
            OnRespond?.Invoke();
            return Task.FromResult(response);
        }
        public async IAsyncEnumerable<ModelToolStreamChunk> RespondWithToolsStreamAsync(ModelToolRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await RespondWithToolsAsync(request, cancellationToken);
            if (response.Content.Length > 0) yield return new(response.Content, false, [], []);
            if (EarlySegment is not null) yield return new(null, false, [], [], FinalSpeechSegment: EarlySegment);
            Completed = true;
            yield return new(null, true, response.ToolCalls, response.OutputItems, response.ResponseId, response.Provider,
                response.Model, response.Purpose, response.MetadataJson, response.AuditData);
        }
    }

    private sealed class BenignTool : IAegisTool
    {
        public int Calls;
        public Action<ToolExecutionContext>? OnExecute { get; init; }
        public ToolExecutionContext? LastContext;
        public string Name => "test_read";
        public string Description => "Leitura sintética";
        public JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new { type = "object" });
        public Task<AegisToolResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastContext = context;
            OnExecute?.Invoke(context);
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
