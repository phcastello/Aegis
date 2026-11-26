using Aegis.Application.Chat;
using Aegis.Application.Llm;
using Aegis.Application.Models;
using Aegis.Application.Observability;
using Aegis.Domain;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Chat;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class BackgroundResearchTests
{
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

    private static ServiceProvider Services(ResearchModel model, ConversationChanges changes)
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AegisDbContext>(options => options.UseInMemoryDatabase(dbName));
        services.AddSingleton<IConversationChangePublisher>(changes);
        services.AddSingleton<IAegisModelClient>(model);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new WebSearchOptions());
        services.AddSingleton<AegisMetrics>();
        services.AddScoped<ResearchJobStore>();
        services.AddScoped<IResearchDelegationQueue>(provider => provider.GetRequiredService<ResearchJobStore>());
        return services.BuildServiceProvider();
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

        public Task<ModelToolResponse> RespondWithToolsAsync(ModelToolRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ModelToolStreamChunk> RespondWithToolsStreamAsync(ModelToolRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Assert.Empty(request.Tools);
            Assert.NotNull(request.NativeSearchActionBudget);
            JobInput = request.Request.Input;
            yield return new(null, false, [], [], ToolStatus: new("web_search", "started", "Pesquisando…"));
            yield return new(null, false, [], [], WebResearchSources: [new("https://example.org/reference", 1)]);
            yield return new(null, false, [], [], WebResearchSources: [new("https://example.org/reference", 1)]);
            Started.TrySetResult();
            try { await Finish.Task.WaitAsync(ct); }
            finally { if (ct.IsCancellationRequested) Abandoned.TrySetResult(); }

            yield return new("Resultado verificado.", false, [], []);
            using var document = JsonDocument.Parse("""
                [{"type":"web_search_call","id":"ws_1","status":"completed","action":{"type":"search",
                    "sources":[{"type":"url","url":"https://example.org/reference"}]}}]
                """);
            var audit = new LlmRequestAuditData("fake", "research", true, 1, "{}", 200, null, null, null);
            yield return new(null, true, [], document.RootElement.EnumerateArray().Select(item => item.Clone()).ToArray(),
                Provider: "fake", Model: "research", AuditData: audit);
        }
    }
}
