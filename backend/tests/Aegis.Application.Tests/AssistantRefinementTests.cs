using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Aegis.Application.Chat;
using Aegis.Application.Common;
using Aegis.Application.Llm;
using Aegis.Application.Memory;
using Aegis.Application.Models;
using Aegis.Application.Observability;
using Aegis.Application.Prompts;
using Aegis.Application.Runtime;
using Aegis.Application.Tools;
using Aegis.Application.Turns;
using Aegis.Application.Voice;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class AssistantRefinementTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task MainChatLoadsHistoryOnceAndPreservesModelText(bool streaming, bool newConversation)
    {
        using var db = new AegisDbContext(new DbContextOptionsBuilder<AegisDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var conversation = new Conversation(Conversation.DefaultTitle);
        if (!newConversation) db.AddConversation(conversation);
        for (var i = 0; i < (newConversation ? 0 : 30); i++)
            db.AddChatMessage(conversation.AddMessage(i % 2 == 0 ? "user" : "assistant", $"turno {i}"));
        await db.SaveChangesAsync();
        var history = conversation.Messages.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToArray();
        var loads = 0;
        var chatDb = DispatchProxy.Create<IAegisDbContext, StubProxy>();
        ((StubProxy)(object)chatDb).InvokeMethod = (method, args) =>
        {
            if (method.Name == nameof(IAegisDbContext.GetConversationWithMessagesAsync)) loads++;
            if (method.Name == nameof(IAegisDbContext.GetRecentMessagesAsync))
                throw new InvalidOperationException("Chat must reuse the complete loaded history.");
            return method.Invoke(db, args);
        };
        using var metrics = new AegisMetrics();
        using var registry = new ActiveTurnRegistry(metrics);
        var voice = DispatchProxy.Create<IVoiceService, StubProxy>();
        ((StubProxy)(object)voice).InvokeMethod = (method, args) => method.Name == "RegisterTurnAsync"
            ? Task.FromResult(registry.Register((Guid)args![0]!, (Guid)args[1]!)) : throw new NotSupportedException();
        var queue = DispatchProxy.Create<IConversationTitleJobQueue, StubProxy>();
        ConversationTitleJob? titleJob = null;
        ((StubProxy)(object)queue).InvokeMethod = (_, args) =>
        {
            titleJob = (ConversationTitleJob)args![0]!;
            return ValueTask.CompletedTask;
        };
        var loop = new CaptureLoop();
        var chat = new ChatService(chatDb, new PromptBuilder(new Runtime()), loop, queue, registry, voice);
        const string current = "Agora tenho dois monitores. Um QHD 180Hz e um FHD 75Hz. O principal é o QHD";
        var request = new SendMessageRequest { ConversationId = newConversation ? null : conversation.Id, Content = current, TurnId = Guid.NewGuid() };
        if (streaming)
        {
            await using var iterator = chat.StreamMessageAsync(request).GetAsyncEnumerator();
            Assert.True(await iterator.MoveNextAsync());
            Assert.Equal("conversation", iterator.Current.Type);
            Assert.True(await iterator.MoveNextAsync());
            Assert.Equal("token", iterator.Current.Type);
            Assert.Equal("Aí sim. ", iterator.Current.Content);
            Assert.False(loop.Completed); // The first token arrives before the model's final chunk.
            while (await iterator.MoveNextAsync()) { }
        }
        else await chat.SendMessageAsync(request);
        var messages = loop.Request!.InputItems!.Where(x => x.GetProperty("role").GetString() is "user" or "assistant").ToArray();
        Assert.Equal(history.Length + 1, messages.Length);
        Assert.Equal(history.Select(x => x.Content).Append(current), messages.Select(x => x.GetProperty("content").GetString()));
        Assert.Single(messages, x => x.GetProperty("content").GetString() == current);
        Assert.Equal(newConversation ? 0 : 1, loads);
        conversation = await db.Conversations.SingleAsync();
        Assert.Equal("Aí sim. O secundário serve bem de apoio.", conversation.Messages.Last().Content);
        Assert.NotNull(titleJob);
        Assert.Equal(conversation.Id, titleJob.ConversationId);
        Assert.Equal(newConversation ? current : "turno 0", titleJob.UserContent);
        var restored = await chat.GetConversationAsync(conversation.Id);
        Assert.NotNull(restored);
        Assert.Equal(history.Length + 2, restored.Messages.Count);
        conversation.Delete();
        await db.SaveChangesAsync();
        Assert.Null(await chat.GetConversationAsync(conversation.Id));
        var deletedRequest = new SendMessageRequest { ConversationId = conversation.Id, Content = current, TurnId = Guid.NewGuid() };
        if (streaming)
        {
            await using var deleted = chat.StreamMessageAsync(deletedRequest).GetAsyncEnumerator();
            await Assert.ThrowsAsync<ConversationNotFoundException>(async () => { await deleted.MoveNextAsync(); });
        }
        else await Assert.ThrowsAsync<ConversationNotFoundException>(() => chat.SendMessageAsync(deletedRequest));
    }

    [Theory]
    [InlineData("qual meu time favorito?", false)]
    [InlineData("qual meu time favorito", false)]
    [InlineData("me diz qual meu time favorito", false)]
    [InlineData("e ele?", true)]
    [InlineData("e quanto eu tinha antes", true)]
    public async Task MemorySearchAnchorsCasualUserContentBeforeResolvedFallback(string user, bool fallback)
    {
        var queries = new List<string>();
        var store = DispatchProxy.Create<IMemoryStore, StubProxy>();
        var fact = new MemoryRecord("O time favorito de Pedro é a FaZe Clan.", DateTimeOffset.UtcNow.AddDays(-1), null, DateTimeOffset.UtcNow);
        var asOf = DateTimeOffset.UtcNow.AddHours(-1);
        ((StubProxy)(object)store).InvokeMethod = (method, args) =>
        {
            if (method.Name == "SearchAsync")
            {
                var query = (string)args![0]!;
                queries.Add(query);
                Assert.Equal(asOf, (DateTimeOffset)args[2]!);
                return Task.FromResult<IReadOnlyList<MemoryRecord>>(fallback && queries.Count == 1 ? [] : [fact]);
            }
            if (method.Name == "ObserveAsync") return Task.CompletedTask;
            throw new NotSupportedException(method.Name);
        };
        using var metrics = new AegisMetrics();
        var tool = new MemorySearchTool(new MemoryService(store, TimeProvider.System, metrics));
        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { query = "time favorito Pedro", asOf = asOf.ToString("O") }),
            new(Guid.NewGuid(), Guid.NewGuid(), user));
        Assert.True(result.Success);
        Assert.Equal(user, queries[0]);
        Assert.Equal(fallback ? 2 : 1, queries.Count);
        if (fallback) Assert.Equal("time favorito Pedro", queries[1]);
        Assert.Contains(fact.Id.ToString(), result.Content);
    }

    public class StubProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> InvokeMethod { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => InvokeMethod(method!, args);
    }
    private sealed class Runtime : IRuntimeContextProvider
    {
        public Task<string> GetRuntimeContextAsync(CancellationToken cancellationToken = default) => Task.FromResult("Horário atual");
    }
    private sealed class CaptureLoop : IAegisToolLoop
    {
        public ModelRequest? Request;
        public bool Completed;
        private static LlmRequestAuditData Audit => new("fake", "fake", true, 1, "{}", 200, "{}", null, null);
        public Task<ModelToolResponse> RunAsync(ModelRequest request, ToolExecutionContext context, CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(new ModelToolResponse("Aí sim. O secundário serve bem de apoio.", "fake", "fake", ModelPurpose.Chat, [], [], null, null, Audit));
        }
        public async IAsyncEnumerable<ModelStreamChunk> StreamAsync(ModelRequest request, ToolExecutionContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Request = request;
            yield return new("Aí sim. ", false);
            await Task.Yield();
            yield return new("O secundário serve bem de apoio.", false);
            Completed = true;
            yield return new(null, true, "fake", "fake", ModelPurpose.Chat, AuditData: Audit);
        }
    }
}
