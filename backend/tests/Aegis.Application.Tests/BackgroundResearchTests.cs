using Aegis.Api.Controllers;
using Aegis.Application.Chat;
using Aegis.Application.Llm;
using Aegis.Application.Models;
using Aegis.Application.Observability;
using Aegis.Application.Prompts;
using Aegis.Application.Tools;
using Aegis.Application.Voice;
using Aegis.Domain;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Chat;
using Aegis.Infrastructure.Models;
using Aegis.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class BackgroundResearchTests
{
    private const string ExampleScript = "import secrets\nprint(secrets.randbelow(53) - 10)";

    [Fact]
    public async Task CompletedResearchStreamsTheEntireAnswerToItsOriginatingVoiceSession()
    {
        const string spoken = "São Paulo avançou mais. Recife ainda tem metas em aberto.";
        var written = "São Paulo avançou mais (levantamento municipal). " +
            "Recife ainda tem metas em aberto. [Referência](https://example.org/reference)\n\n## Análise detalhada\n" +
            string.Concat(Enumerable.Repeat("Detalhes e citações da comparação escrita. ", 45)) +
            "A última conclusão também deve ser falada.";
        var model = new ResearchModel { Answer = written };
        await using var services = Services(model, new ConversationChanges());
        var options = services.GetRequiredService<WebSearchOptions>();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AegisDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<ResearchJobStore>();
        var conversation = new Conversation(Conversation.DefaultTitle);
        var origin = conversation.AddMessage(ChatRoles.User, "Compare as duas cidades.");
        db.Conversations.Add(conversation);
        db.ChatMessages.Add(origin);
        await db.SaveChangesAsync();
        var sessionId = Guid.NewGuid();
        store.Stage(origin, "Compare São Paulo e Recife.", sessionId);
        await db.SaveChangesAsync();

        var tts = new ResearchSpeechClient();
        async Task<(int Status, IHeaderDictionary Headers, byte[] Audio)> RequestSpeech(
            Guid session, ResearchSpeechClient? client = null, CancellationToken ct = default)
        {
            await using var body = new MemoryStream();
            var controller = new ResearchJobsController(store, options) { ControllerContext = new()
            {
                HttpContext = new DefaultHttpContext()
            } };
            controller.Response.Body = body;
            await controller.Speak(origin.Id, new(session), client ?? tts, ct);
            return (controller.Response.StatusCode, controller.Response.Headers, body.ToArray());
        }

        Assert.Equal(StatusCodes.Status404NotFound, (await RequestSpeech(Guid.NewGuid())).Status);
        Assert.Equal(StatusCodes.Status409Conflict, (await RequestSpeech(sessionId)).Status);
        Assert.Empty(tts.Requests);

        model.Finish.SetResult();
        Assert.True(await Worker(services).ProcessNextAsync(CancellationToken.None));
        Assert.Contains("Comece direto pela conclusão", model.Instructions);
        Assert.Equal(StatusCodes.Status404NotFound, (await RequestSpeech(Guid.NewGuid())).Status);
        var audio = await RequestSpeech(sessionId);
        Assert.Equal(StatusCodes.Status200OK, audio.Status);
        Assert.Equal("pcm_s16le", audio.Headers["X-Aegis-Audio-Format"].ToString());
        Assert.Equal("24000", audio.Headers["X-Aegis-Sample-Rate"].ToString());
        Assert.Equal("1", audio.Headers["X-Aegis-Channels"].ToString());
        Assert.True(tts.Requests.Count > 1);
        Assert.Equal(Enumerable.Repeat(new byte[] { 1, 2, 3, 4 }, tts.Requests.Count).SelectMany(bytes => bytes), audio.Audio);
        var spokenAnswer = string.Join(' ', tts.Requests.Select(request => request.Text));
        Assert.StartsWith(spoken, spokenAnswer);
        Assert.EndsWith("A última conclusão também deve ser falada.", spokenAnswer);
        Assert.DoesNotContain("https://", spokenAnswer);
        Assert.DoesNotContain("levantamento municipal", spokenAnswer);
        Assert.All(tts.Requests, request =>
        {
            Assert.Equal(origin.ConversationId, request.ConversationId);
            Assert.InRange(request.Text.Length, 1, FinalSpeechSegments.MaxSegmentCharacters);
            Assert.Equal(26, request.NativeRequestId.Length);
        });
        Assert.Equal(tts.Requests.Count, tts.Requests.Select(request => request.NativeRequestId).Distinct().Count());
        Assert.Empty(tts.Cancellations);
        Assert.Equal(written, (await db.ChatMessages.SingleAsync(message => message.Role == ChatRoles.Assistant)).Content);

        using var interrupt = new CancellationTokenSource();
        var interrupted = new ResearchSpeechClient(pauseAtRequest: 2);
        var speaking = RequestSpeech(sessionId, interrupted, interrupt.Token);
        await interrupted.Paused.Task.WaitAsync(TimeSpan.FromSeconds(4));
        await interrupt.CancelAsync();
        await speaking.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.Equal(2, interrupted.Requests.Count);
        Assert.Equal(interrupted.Requests[1].NativeRequestId, Assert.Single(interrupted.Cancellations));
    }

    [Fact]
    public void ResearchSpeechKeepsTheDetailsAndTableValuesInTheirWrittenOrder()
    {
        const string written = """
            São Paulo avançou mais [1].

            ## Dados por cidade
            | Cidade | Veículos |
            | --- | --- |
            | São Paulo | 500 |
            - Recife sem confirmação (dado preliminar).
            """;
        Assert.Equal("São Paulo avançou mais.\nDados por cidade\nCidade, Veículos\nSão Paulo, 500\nRecife sem confirmação.",
            ResearchSpeechText.FromAnswer(written));
    }

    [Fact]
    public async Task DelegatedResearchCommitsWithTheAcknowledgementAndSurvivesRequestCancellation()
    {
        const string brief = "Confira a notícia sobre o edital público ABC e compare com a versão anterior.";
        var model = new ResearchModel();
        var changes = new ConversationChanges();
        await using var services = Services(model, changes);
        using var requestAborted = new CancellationTokenSource();
        ChatMessage origin;
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AegisDbContext>();
            var conversation = new Conversation(Conversation.DefaultTitle);
            db.Conversations.Add(conversation);
            origin = conversation.AddMessage(ChatRoles.User, "Confira a notícia que eu mencionei.");
            origin.AttachAuditData(null, null, null, """{"voicePlayback":{"version":1}}""");
            db.ChatMessages.Add(origin);
            await db.SaveChangesAsync(requestAborted.Token);

            var queue = scope.ServiceProvider.GetRequiredService<IResearchDelegationQueue>();
            queue.Stage(origin, brief);
            db.ChatMessages.Add(conversation.AddMessage(ChatRoles.Assistant, "Vou conferir o edital e a versão anterior."));

            await using var beforeCommit = services.CreateAsyncScope();
            var beforeDb = beforeCommit.ServiceProvider.GetRequiredService<AegisDbContext>();
            Assert.Empty(await beforeDb.ResearchJobs.ToListAsync());
            Assert.Equal(1, await beforeDb.ChatMessages.CountAsync());
            await db.SaveChangesAsync(requestAborted.Token);
        }
        requestAborted.Cancel();

        var work = Worker(services).ProcessNextAsync(CancellationToken.None);
        await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(4));
        try
        {
            Assert.Equal(brief, model.JobInput);
            await using var scope = services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<ResearchJobStore>();
            var running = await store.GetAsync(origin.Id, CancellationToken.None);
            Assert.Equal("running", running!.State);
            Assert.Equal("searching", running.Phase);
            Assert.Equal(new WebResearchSourceCount("https://example.org/reference", 2), Assert.Single(running.Sources));
            var db = scope.ServiceProvider.GetRequiredService<AegisDbContext>();
            var conversation = await db.Conversations.SingleAsync();
            db.ChatMessages.Add(conversation.AddMessage(ChatRoles.User, "Outro pedido independente."));
            await db.SaveChangesAsync();
        }
        finally { model.Finish.TrySetResult(); }

        Assert.True(await work.WaitAsync(TimeSpan.FromSeconds(4)));
        await using var finalScope = services.CreateAsyncScope();
        var final = finalScope.ServiceProvider.GetRequiredService<ResearchJobStore>();
        var snapshot = await final.GetAsync(origin.Id, CancellationToken.None);
        Assert.Equal("completed", snapshot!.State);
        Assert.Equal(origin.Id, snapshot.UserMessageId);
        Assert.Equal(new WebResearchSourceCount("https://example.org/reference", 2), Assert.Single(snapshot.Sources));
        var finalDb = finalScope.ServiceProvider.GetRequiredService<AegisDbContext>();
        var message = await finalDb.ChatMessages.SingleAsync(row => row.Id == snapshot.ResultMessageId);
        Assert.Equal("Resultado verificado.", message.Content);
        var evidence = WebResearchMetadata.Read(message.MetadataJson, message.Content);
        Assert.Equal("https://example.org/reference", Assert.Single(evidence!.Sources).Url);
        Assert.Equal(message.Id, (await finalDb.LlmRequestAudits.SingleAsync()).AssistantMessageId);
        Assert.Equal(4, await finalDb.ChatMessages.CountAsync());
        var initialMessage = await finalDb.ChatMessages.SingleAsync(row => row.Id == origin.Id);
        Assert.Equal("Confira a notícia que eu mencionei.", initialMessage.Content);
        using var metadata = JsonDocument.Parse(initialMessage.MetadataJson!);
        Assert.Equal(1, metadata.RootElement.GetProperty("voicePlayback").GetProperty("version").GetInt32());
        Assert.Contains(origin.ConversationId, changes.Ids);
    }

    [Fact]
    public async Task CancellationIsDurableAndStopsTheProviderWithoutPublishingLateOutput()
    {
        var model = new ResearchModel();
        await using var services = Services(model, new ConversationChanges());
        ResearchJobSnapshot queued;
        await using (var scope = services.CreateAsyncScope())
        {
            queued = await scope.ServiceProvider.GetRequiredService<ResearchJobStore>()
                .CreateAsync(new(Guid.NewGuid(), null, "Pesquise."), CancellationToken.None);
        }

        var work = Worker(services).ProcessNextAsync(CancellationToken.None);
        await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.Equal("Pesquise.", model.JobInput);
        await using (var scope = services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<ResearchJobStore>();
            var cancelled = await store.CancelAsync(queued.Id, CancellationToken.None);
            Assert.Equal("cancelled", cancelled!.State);
        }

        Assert.True(await work.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(model.Abandoned.Task.IsCompletedSuccessfully);
        Assert.False(model.Finish.Task.IsCompleted);
        await using var finalScope = services.CreateAsyncScope();
        var final = finalScope.ServiceProvider.GetRequiredService<ResearchJobStore>();
        Assert.Equal("cancelled", (await final.GetAsync(queued.Id, CancellationToken.None))!.State);
        Assert.Equal("cancelled", (await final.CancelAsync(queued.Id, CancellationToken.None))!.State);
        Assert.Equal(1, await finalScope.ServiceProvider.GetRequiredService<AegisDbContext>().ChatMessages.CountAsync());
    }

    [Fact]
    public async Task ReplayingTheSameClientIdNeverAddsAnotherConversationOrUserMessage()
    {
        await using var services = Services(new ResearchModel(), new ConversationChanges());
        var request = new CreateResearchJobRequest(Guid.NewGuid(), null, "Pesquise.");
        ResearchJobSnapshot initial;
        await using (var scope = services.CreateAsyncScope())
        {
            initial = await scope.ServiceProvider.GetRequiredService<ResearchJobStore>()
                .CreateAsync(request, CancellationToken.None);
        }

        await using var repeatScope = services.CreateAsyncScope();
        var store = repeatScope.ServiceProvider.GetRequiredService<ResearchJobStore>();
        var replay = await store.CreateAsync(request, CancellationToken.None);
        Assert.Equal(initial.Id, replay.Id);
        Assert.Equal(initial.ConversationId, replay.ConversationId);
        Assert.Equal(initial.UserMessageId, replay.UserMessageId);
        await Assert.ThrowsAsync<ResearchJobConflictException>(() =>
            store.CreateAsync(request with { Content = "Pedido diferente." }, CancellationToken.None));
        var db = repeatScope.ServiceProvider.GetRequiredService<AegisDbContext>();
        Assert.Equal(1, await db.Conversations.CountAsync());
        Assert.Equal(1, await db.ChatMessages.CountAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PythonJobRequiresAnActualScriptResultAndUsesItsOwnVoiceSession(bool runScript)
    {
        const string request = "Gere um inteiro aleatório entre -10 e 42, inclusive.";
        const string brief = "Use o intervalo inteiro indicado; explique também a inclusão dos limites.";
        var runner = new ScriptRunner();
        var model = new ScriptModel(runScript);
        await using var services = Services(model, new ConversationChanges(), runner, webEnabled: false);
        var sessionId = Guid.NewGuid();
        Guid jobId;
        Guid conversationId;
        Guid webJobId;
        await using (var scope = services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<ResearchJobStore>();
            var webJob = await store.CreateAsync(new(Guid.NewGuid(), null, "A web ficou desativada."), CancellationToken.None);
            webJobId = webJob.Id;
            var db = scope.ServiceProvider.GetRequiredService<AegisDbContext>();
            var conversation = new Conversation(Conversation.DefaultTitle);
            var origin = conversation.AddMessage(ChatRoles.User, request);
            db.Conversations.Add(conversation);
            db.ChatMessages.Add(origin);
            await db.SaveChangesAsync();
            store.Stage(origin, brief, sessionId, isPython: true);
            await db.SaveChangesAsync();
            jobId = origin.Id;
            conversationId = origin.ConversationId;
            Assert.Equal("python", (await store.GetAsync(jobId, CancellationToken.None))?.Kind);
            Assert.Null((await store.GetVoiceResultAsync(jobId, sessionId, CancellationToken.None))?.Text);
            await Assert.ThrowsAsync<ResearchJobConflictException>(() =>
                store.CreateAsync(new(jobId, conversationId, request), CancellationToken.None));
        }

        Assert.True(await Worker(services).ProcessNextAsync(CancellationToken.None));
        Assert.Equal(runScript ? 1 : 0, runner.Runs);
        Assert.Contains(request, model.JobInput);
        Assert.Contains(brief, model.JobInput);
        Assert.NotNull(model.Instructions);
        Assert.Contains("Comece direto pela conclusão", model.Instructions);

        await using var finalScope = services.CreateAsyncScope();
        var finalStore = finalScope.ServiceProvider.GetRequiredService<ResearchJobStore>();
        var dbAfter = finalScope.ServiceProvider.GetRequiredService<AegisDbContext>();
        Assert.Equal(ResearchJob.Queued, (await finalStore.GetAsync(webJobId, CancellationToken.None))!.State);
        var final = Assert.Single(await finalStore.ListAsync(conversationId, CancellationToken.None));
        Assert.Equal("python", final.Kind);
        Assert.Empty(final.Sources);
        Assert.Null(await finalStore.GetVoiceResultAsync(jobId, Guid.NewGuid(), CancellationToken.None));
        var result = await finalStore.GetVoiceResultAsync(jobId, sessionId, CancellationToken.None);
        if (runScript)
        {
            Assert.Equal(ResearchJob.Completed, final.State);
            Assert.Equal("O inteiro sorteado foi 42. Os dois limites foram incluídos.", result!.Text);
            Assert.Equal(result.Text, (await dbAfter.ChatMessages.SingleAsync(message => message.Id == final.ResultMessageId)).Content);
            var audit = await dbAfter.LlmRequestAudits.SingleAsync();
            Assert.Equal("{\"type\":\"background_python\"}", audit.RequestPayloadJson);
            Assert.Null(audit.ResponseBody);
        }
        else
        {
            Assert.Equal(ResearchJob.Failed, final.State);
            Assert.Equal("python_incomplete", final.ErrorCode);
            Assert.Null(final.ResultMessageId);
            Assert.Equal("Tive um problema e não consegui concluir a tarefa.", result!.Text);
            Assert.Equal(0, await dbAfter.ChatMessages.CountAsync(message => message.Role == ChatRoles.Assistant));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelegatedJobsSendSharedPersonalityAndTaskInstructionsInTheFirstProviderRequest(bool isPython)
    {
        const string original = "Pedido de origem desta conversa.";
        const string brief = "Contexto autorizado para o trabalho.";
        const string preference = "Comece com uma unidade de medida por extenso.";
        var transport = new DelegatedJobTransport(isPython);
        using var http = new HttpClient(transport) { BaseAddress = new Uri("https://responses.test/") };
        using var metrics = new AegisMetrics();
        var model = new OpenAIResponsesClient(http, Options.Create(new OpenAIOptions { ApiKey = "fake" }),
            metrics, NullLogger<OpenAIResponsesClient>.Instance);
        var runner = new ScriptRunner();
        await using var services = Services(model, new ConversationChanges(), runner);
        var session = Guid.NewGuid();
        Guid jobId;
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AegisDbContext>();
            var conversation = new Conversation(Conversation.DefaultTitle);
            var origin = conversation.AddMessage(ChatRoles.User, original);
            var settings = scope.ServiceProvider.GetRequiredService<IOwnerInstructions>();
            await settings.UpdateAsync(preference, Guid.Empty, CancellationToken.None);
            db.Conversations.Add(conversation);
            db.ChatMessages.Add(origin);
            await db.SaveChangesAsync();
            scope.ServiceProvider.GetRequiredService<ResearchJobStore>().Stage(origin, brief, session, isPython);
            await db.SaveChangesAsync();
            jobId = origin.Id;
        }

        Assert.True(await Worker(services).ProcessNextAsync(CancellationToken.None));
        Assert.Equal(isPython ? 2 : 1, transport.Payloads.Count);
        Assert.Equal(isPython ? 1 : 0, runner.Runs);
        using var first = JsonDocument.Parse(transport.Payloads[0]);
        var input = first.RootElement.GetProperty("input");
        Assert.Equal("developer", input[0].GetProperty("role").GetString());
        var instructions = input[0].GetProperty("content").GetString();
        var personality = await PromptBuilder.LoadPersonalityAsync();
        Assert.StartsWith(personality + "\n\n", instructions);
        Assert.Contains("Comece direto pela conclusão", instructions);
        Assert.Contains(isPython ? "Use run_python" : "Use a pesquisa nativa", instructions);
        Assert.Equal("user", input[1].GetProperty("role").GetString());
        Assert.Contains(preference, input[1].GetProperty("content").GetString());
        var userContent = input[2].GetProperty("content").GetString();
        Assert.Contains(brief, userContent);
        if (isPython) Assert.Contains(original, userContent);
        else Assert.DoesNotContain(original, userContent);
        var tool = Assert.Single(first.RootElement.GetProperty("tools").EnumerateArray());
        Assert.Equal(isPython ? "function" : "web_search", tool.GetProperty("type").GetString());
        if (isPython) Assert.Equal("run_python", tool.GetProperty("name").GetString());

        await using var finalScope = services.CreateAsyncScope();
        var state = await finalScope.ServiceProvider.GetRequiredService<ResearchJobStore>().GetAsync(jobId, CancellationToken.None);
        Assert.Equal(ResearchJob.Completed, state?.State);
        if (isPython)
        {
            var db = finalScope.ServiceProvider.GetRequiredService<AegisDbContext>();
            var audit = await db.LlmRequestAudits.SingleAsync();
            Assert.Equal("{\"type\":\"background_python\"}", audit.RequestPayloadJson);
            Assert.Null(audit.ResponseBody);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelegatedWorkerReceivesTheRightAttachmentEvenAfterAnotherTurn(bool isPython)
    {
        var transport = new DelegatedJobTransport(isPython);
        using var http = new HttpClient(transport) { BaseAddress = new Uri("https://responses.test/") };
        using var metrics = new AegisMetrics();
        var model = new OpenAIResponsesClient(http, Options.Create(new OpenAIOptions { ApiKey = "fake" }),
            metrics, NullLogger<OpenAIResponsesClient>.Instance);
        await using var services = Services(model, new ConversationChanges(), new ScriptRunner());
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AegisDbContext>();
        var conversation = new Conversation(Conversation.DefaultTitle);
        var now = DateTimeOffset.UtcNow;
        var source = conversation.AddMessage(ChatRoles.User, "Olhe o arquivo.", now.AddMinutes(-2));
        var jobMessage = conversation.AddMessage(ChatRoles.User, "Compare o total do anexo.", now.AddMinutes(-1));
        var later = conversation.AddMessage(ChatRoles.User, "Agora outro assunto.", now);
        db.Conversations.Add(conversation);
        db.ChatAttachments.Add(new ChatAttachment(source.Id, 0, "pedido.csv", "text/csv", "custo,93742"u8.ToArray()));
        db.ChatAttachments.Add(new ChatAttachment(later.Id, 0, "seguinte.txt", "text/plain", "subsequente"u8.ToArray()));
        await db.SaveChangesAsync();
        scope.ServiceProvider.GetRequiredService<ResearchJobStore>().Stage(jobMessage, "Compare com o valor público padrão.", isPython: isPython);
        await db.SaveChangesAsync();
        Assert.True(await Worker(services).ProcessNextAsync(default));
        Assert.NotEmpty(transport.Payloads);
        Assert.Contains("93742", transport.Payloads[0]);
        Assert.DoesNotContain("subsequente", transport.Payloads[0]);
        var audit = await db.LlmRequestAudits.SingleAsync();
        Assert.DoesNotContain("93742", audit.RequestPayloadJson + audit.ResponseBody);
    }

    private static ServiceProvider Services(IAegisModelClient model, ConversationChanges changes,
        IPythonScriptRunner? script = null, bool webEnabled = true)
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AegisDbContext>(options => options.UseInMemoryDatabase(dbName));
        services.AddSingleton<IConversationChangePublisher>(changes);
        services.AddSingleton<IAegisModelClient>(model);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new WebSearchOptions { Enabled = webEnabled });
        if (script is not null) services.AddSingleton(script);
        services.AddSingleton<AegisMetrics>();
        services.AddScoped<ResearchJobStore>();
        services.AddScoped<IOwnerInstructions, OwnerInstructionsStore>();
        services.AddScoped<IResearchDelegationQueue>(provider => provider.GetRequiredService<ResearchJobStore>());
        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<AegisDbContext>().Database.EnsureCreated();
        return provider;
    }

    private static ResearchWorker Worker(ServiceProvider services) => new(
        services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System,
        services.GetRequiredService<WebSearchOptions>(), NullLogger<ResearchWorker>.Instance);

    private sealed class ConversationChanges : IConversationChangePublisher
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<Guid> Ids = new();
        public void Publish(Guid conversationId) => Ids.Enqueue(conversationId);
    }

    private sealed class ResearchModel : IAegisModelClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Abandoned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? JobInput { get; private set; }
        public string? Instructions { get; private set; }
        public string Answer { get; init; } = "Resultado verificado.";

        public Task<ModelToolResponse> RespondWithToolsAsync(ModelToolRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ModelToolStreamChunk> RespondWithToolsStreamAsync(ModelToolRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Assert.Empty(request.Tools);
            Assert.NotNull(request.NativeSearchActionBudget);
            JobInput = request.Request.Input;
            Instructions = request.Request.Instructions;
            yield return new(null, false, [], [], ToolStatus: new("web_search", "started", "Pesquisando…"));
            yield return new(null, false, [], [], WebResearchSources: [new("https://example.org/reference", 1)]);
            yield return new(null, false, [], [], WebResearchSources: [new("https://example.org/reference", 1)]);
            Started.TrySetResult();
            try { await Finish.Task.WaitAsync(ct); }
            finally { if (ct.IsCancellationRequested) Abandoned.TrySetResult(); }

            yield return new(Answer, false, [], []);
            using var document = JsonDocument.Parse("""
                [{"type":"web_search_call","id":"ws_1","status":"completed","action":{"type":"search",
                    "sources":[{"type":"url","url":"https://example.org/reference"}]}}]
                """);
            var audit = new LlmRequestAuditData("fake", "research", true, 1, "{}", 200, null, null, null);
            yield return new(null, true, [], document.RootElement.EnumerateArray().Select(item => item.Clone()).ToArray(),
                Provider: "fake", Model: "research", AuditData: audit);
        }
    }

    private sealed class ScriptModel(bool runScript) : IAegisModelClient
    {
        public string? JobInput { get; private set; }
        public string? Instructions { get; private set; }

        public Task<ModelToolResponse> RespondWithToolsAsync(ModelToolRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ModelToolStreamChunk> RespondWithToolsStreamAsync(ModelToolRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Assert.Equal("run_python", Assert.Single(request.Tools).Name);
            Assert.Null(request.NativeSearchActionBudget);
            JobInput = request.Request.Input;
            Instructions = request.Request.Instructions;
            Assert.Contains(request.AuditInputItems!, item => item.GetRawText().Contains("[python_tool_brief_redacted]"));
            var audit = new LlmRequestAuditData("fake", "script", true, 1, JobInput, 200, ExampleScript, null, null);
            var output = request.InputItems?.SingleOrDefault(item => item.TryGetProperty("type", out var type) &&
                type.GetString() == "function_call_output");
            if (runScript && output is null or { ValueKind: JsonValueKind.Undefined })
            {
                var args = JsonSerializer.SerializeToElement(new { code = ExampleScript, input = (string?)null });
                yield return new(null, true, [new("python_1", "run_python", args)], [],
                    Provider: "fake", Model: "script", AuditData: audit);
                yield break;
            }

            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            if (runScript)
            {
                using var toolResponse = JsonDocument.Parse(output!.Value.GetProperty("output").GetString()!);
                var number = toolResponse.RootElement.GetProperty("stdout").GetString()!.Trim();
                yield return new($"O inteiro sorteado foi {number}. Os dois limites foram incluídos.", false, [], []);
            }
            else yield return new("O inteiro sorteado foi 17.", false, [], []);
            yield return new(null, true, [], [], Provider: "fake", Model: "script", AuditData: audit);
        }
    }

    private sealed class DelegatedJobTransport(bool isPython) : HttpMessageHandler
    {
        public List<string> Payloads { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Payloads.Add(await request.Content!.ReadAsStringAsync(ct));
            object item = isPython && Payloads.Count == 1
                ? new { type = "function_call", call_id = "call_1", name = "run_python",
                    arguments = JsonSerializer.Serialize(new { code = ExampleScript, input = (string?)null }) }
                : new { type = "message", content = new[] { new { type = "output_text", text = "Resultado verificado." } } };
            var response = new { type = "response.completed", response = new
            {
                id = "response_" + Payloads.Count, model = "test-model", output = new[] { item }
            } };
            var body = "data: " + JsonSerializer.Serialize(response) + "\n\ndata: [DONE]\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
        }
    }

    private sealed class ScriptRunner : IPythonScriptRunner
    {
        public bool IsAvailable => true;
        public int Runs { get; private set; }

        public Task<PythonScriptResult> RunAsync(string code, string? input, IReadOnlyList<string> inputFiles,
            ToolExecutionContext context, CancellationToken cancellationToken)
        {
            Assert.Contains("secrets.randbelow(53)", code);
            Assert.Null(input);
            Runs++;
            return Task.FromResult(new PythonScriptResult(true, "42\n", ""));
        }
    }

    private sealed class ResearchSpeechClient(int pauseAtRequest = int.MaxValue) : IAegisSpeechClient
    {
        public List<SpeechRequest> Requests { get; } = [];
        public List<string> Cancellations { get; } = [];
        public TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<SpeechStreamResponse> StreamSpeechAsync(SpeechRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (Requests.Count == pauseAtRequest)
            {
                Paused.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return new SpeechStreamResponse(new MemoryStream([1, 2, 3, 4]), new("pcm_s16le", 24000, 1, "fixture"));
        }

        public Task CancelAsync(string nativeSpeechRequestId, CancellationToken cancellationToken = default)
        {
            Cancellations.Add(nativeSpeechRequestId);
            return Task.CompletedTask;
        }

        public Task<SpeechServiceStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
