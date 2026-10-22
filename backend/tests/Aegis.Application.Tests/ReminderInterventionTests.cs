using System.Text.Json;
using Aegis.Application.Chat;
using Aegis.Application.Llm;
using Aegis.Application.Models;
using Aegis.Application.Nodes;
using Aegis.Application.Prompts;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Nodes;
using Aegis.Infrastructure.Persistence;
using Aegis.Infrastructure.Proactivity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;
namespace Aegis.Application.Tests;

public sealed class ReminderInterventionTests
{
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.Parse("2026-10-22T12:00:00Z"); public override DateTimeOffset GetUtcNow() => Now; }
    private static AegisDbContext MemoryDb() => new(new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static async Task<(Guid Conversation, Guid Rule, Guid Node)> Seed(AegisDbContext db, Clock clock) {
        var conversation = new Conversation("Conversa original"); conversation.AddMessage("user", "Me lembra de comprar leite ao sair do campus", clock.Now);
        var node = new AegisNode(Guid.NewGuid(), "Celular sintético", NodePlatform.Android, "0.7.2", 1, clock.Now);
        var rule = new GeographicReminderRule("Comprar leite", [Guid.NewGuid()], node.Id, "exit", false, null, null, "UTC", clock.Now, conversation.Id);
        db.Conversations.Add(conversation); db.Nodes.Add(node); db.NodeCapabilities.Add(new(node.Id, NotificationContract.ConversationCapability, 1)); db.Set<GeographicReminderRule>().Add(rule);
        await db.SaveChangesAsync(); return (conversation.Id, rule.Id, node.Id);
    }
    private static GeneratedIntervention Generated() => new("Preciso que você abra a Aegis no celular para concluir a ativação daquele lembrete.", "fixture-model", "{}", new("openai", "fixture-model", true, 10, "{\"purpose\":\"reminder_intervention\"}", 200, null, null, null));
    private static async Task Recover(ReminderInterventionStore store, Clock clock) {
        var start = clock.Now;
        foreach (var minutes in new[] { 1, 3, 5 }) {
            clock.Now = start.AddMinutes(minutes);
            foreach (var claim in await store.ReconcileAsync(default)) await store.RecoveryCompletedAsync(claim, "unavailable", default);
        }
        clock.Now = start.AddMinutes(10); await store.ReconcileAsync(default);
    }
    [Theory]
    [InlineData("installed")][InlineData("active")][InlineData("waiting_initial")]
    public async Task InstalledAndLegitimateTransitionWaitNeverEscalate(string status) {
        await using var db = MemoryDb(); var clock = new Clock(); var ids = await Seed(db, clock);
        var rule = await db.Set<GeographicReminderRule>().SingleAsync(); rule.Confirm(rule.Revision, status, null, clock.Now); await db.SaveChangesAsync();
        var store = new ReminderInterventionStore(db, clock); clock.Now = clock.Now.AddHours(2);
        Assert.Empty(await store.ReconcileAsync(default)); Assert.Null(await store.ClaimAsync(default)); Assert.Empty(db.Set<ReminderIntervention>());
    }
    [Fact] public async Task TransientFailureThatRecoversNeedsNoModelOrMessage() {
        await using var db = MemoryDb(); var clock = new Clock(); await Seed(db, clock);
        var store = new ReminderInterventionStore(db, clock);
        clock.Now = clock.Now.AddMinutes(1); var claim = Assert.Single(await store.ReconcileAsync(default)); await store.RecoveryCompletedAsync(claim, "accepted", default);
        var rule = await db.Set<GeographicReminderRule>().SingleAsync(); rule.Confirm(1, "waiting_initial", null, clock.Now); await db.SaveChangesAsync();
        clock.Now = clock.Now.AddHours(1); await store.ReconcileAsync(default); Assert.Null(await store.ClaimAsync(default));
        Assert.Equal("resolved", (await db.Set<ReminderIntervention>().SingleAsync()).State); Assert.Empty(db.ChatMessages.Where(m => m.IsProactive));
    }
    [Fact] public async Task PendingSyncIsBoundedRecoveryIsLimitedAndExactlyOneRealAssistantMessageIsCommitted() {
        await using var db = MemoryDb(); var clock = new Clock(); var ids = await Seed(db, clock);
        var store = new ReminderInterventionStore(db, clock);
        await store.ReconcileAsync(default); Assert.Null(await store.ClaimAsync(default));
        await Recover(store, clock);
        var claim = Assert.IsType<InterventionClaim>(await store.ClaimAsync(default));
        Assert.Equal(ids.Conversation, claim.ConversationId); Assert.Equal(ids.Rule, claim.RuleId);
        Assert.Equal(3, JsonDocument.Parse(claim.RecoveriesJson).RootElement.GetArrayLength());
        Assert.Contains("causa exata não foi identificada", claim.Diagnosis);
        Assert.Contains("canal disponível", claim.Diagnosis);
        Assert.True(await store.CompleteAsync(claim, Generated(), default));
        // Restarted store and repeated completion cannot duplicate the incident/message/outbox.
        var restarted = new ReminderInterventionStore(db, clock);
        Assert.False(await restarted.CompleteAsync(claim, Generated(), default)); Assert.Null(await restarted.ClaimAsync(default));
        var message = Assert.Single(await db.ChatMessages.Where(m => m.IsProactive).ToListAsync());
        Assert.Equal("assistant", message.Role); Assert.Equal(ids.Conversation, message.ConversationId); Assert.Equal(clock.Now, message.CreatedAt); Assert.Equal(1L, message.ProactiveSequence);
        Assert.Single(await db.Set<ProactiveNotification>().ToListAsync()); Assert.Single(await db.LlmRequestAudits.ToListAsync());
        Assert.True((await db.Conversations.SingleAsync()).HasUnreadProactive);
        Assert.Single(await db.ChatMessages.Where(m => m.Role == "user").ToListAsync());
    }
    [Theory][InlineData("installed")][InlineData("paused")][InlineData("deleted")]
    public async Task ProblemResolvedPausedOrConversationDeletedWhileModelWorksDiscardsStaleText(string resolution) {
        await using var db = MemoryDb(); var clock = new Clock(); await Seed(db, clock); var store = new ReminderInterventionStore(db, clock); await Recover(store, clock);
        var claim = (await store.ClaimAsync(default))!; var rule = await db.Set<GeographicReminderRule>().SingleAsync();
        if (resolution == "installed") rule.Confirm(1, "installed", null, clock.Now);
        else if (resolution == "paused") rule.SetStatus("pause", clock.Now);
        else (await db.Conversations.SingleAsync()).Delete(clock.Now);
        await db.SaveChangesAsync(); Assert.False(await store.CompleteAsync(claim, Generated(), default)); Assert.Empty(db.ChatMessages.Where(m => m.IsProactive)); Assert.Empty(db.Set<ProactiveNotification>());
    }
    [Fact] public async Task ModelFailureRetriesWithNewLeaseAndExpiredOwnerCannotCommit() {
        await using var db = MemoryDb(); var clock = new Clock(); await Seed(db, clock); var store = new ReminderInterventionStore(db, clock); await Recover(store, clock);
        var first = (await store.ClaimAsync(default))!;
        Assert.True(await store.FailAsync(first, default)); Assert.Null(await store.ClaimAsync(default)); clock.Now = clock.Now.AddSeconds(30);
        var retry = (await new ReminderInterventionStore(db, clock).ClaimAsync(default))!;
        Assert.NotEqual(first.Token, retry.Token); Assert.False(await store.CompleteAsync(first, Generated(), default)); Assert.True(await store.CompleteAsync(retry, Generated(), default));
    }
    [Fact] public async Task WorkerLeaseRestartRecoversAndRetryBudgetIsFinite() {
        await using var db = MemoryDb(); var clock = new Clock(); await Seed(db, clock); var store = new ReminderInterventionStore(db, clock); await Recover(store, clock);
        var stale = (await store.ClaimAsync(default))!; clock.Now = clock.Now.AddMinutes(3);
        var next = (await store.ClaimAsync(default))!; Assert.False(await store.CompleteAsync(stale, Generated(), default));
        for (var attempt = 0; attempt < 4; attempt++) { await store.FailAsync(next, default); clock.Now = clock.Now.AddHours(1); if (attempt < 3) next = (await store.ClaimAsync(default))!; }
        Assert.Null(await store.ClaimAsync(default)); Assert.Equal("generation_failed", (await db.Set<ReminderIntervention>().SingleAsync()).State);
    }
    [Fact] public async Task KnownPermissionProblemSuppliesOnlyConfirmedDiagnosisAndNoToolsToModel() {
        await using var db = MemoryDb(); var clock = new Clock(); await Seed(db, clock);
        var rule = await db.Set<GeographicReminderRule>().SingleAsync(); rule.Confirm(1, "recoverable_error", "background_location_required", clock.Now); await db.SaveChangesAsync();
        var store = new ReminderInterventionStore(db, clock); await Recover(store, clock); var claim = (await store.ClaimAsync(default))!;
        var model = new Model(); var result = await new ProactiveMessageGenerator(model, new Prompts()).GenerateAsync(claim, default);
        Assert.Equal(Generated().Content, result.Content); Assert.Empty(model.Request!.Tools);
        var input = model.Request.Request.InputItems!;
        using var data = JsonDocument.Parse(input[1].GetProperty("content").GetString()!);
        Assert.Contains("localização em segundo plano", data.RootElement.GetProperty("Diagnosis").GetString());
        Assert.Contains("Me lembra de comprar leite", input[1].GetProperty("content").GetString());
        Assert.Contains("DADO NÃO CONFIÁVEL", input[0].GetProperty("content").GetString());
        Assert.DoesNotContain("Comprar leite", model.Request.Request.AuditInputItems![1].GetRawText());
    }
    [Fact] public async Task NotificationFailureCannotRemoveMessageOrCreateRecursiveIncidentAndReadNeverReordersHistory() {
        await using var db = MemoryDb(); var clock = new Clock(); var ids = await Seed(db, clock); var store = new ReminderInterventionStore(db, clock); await Recover(store, clock);
        await store.CompleteAsync((await store.ClaimAsync(default))!, Generated(), default);
        var dispatcher = new Dispatcher(); var processor = new ProactiveNotificationProcessor(db, store, new NodeRegistry(db, clock), dispatcher, clock);
        Assert.True(await processor.ProcessNextAsync(default)); Assert.True(await processor.ProcessNextAsync(default));
        Assert.Equal(1, dispatcher.Calls); Assert.Equal("Aegis", dispatcher.Command!.Input.Title); Assert.Equal("Nova mensagem da Aegis", dispatcher.Command.Input.Body); Assert.Equal(ids.Conversation, dispatcher.Command.Input.ConversationId);
        Assert.Single(await db.ChatMessages.Where(m => m.IsProactive).ToListAsync()); Assert.Single(await db.Set<ReminderIntervention>().ToListAsync());
        var before = (await db.Conversations.SingleAsync()).UpdatedAt;
        var reading = new ConversationProactivity(db); await reading.MarkReadAsync(ids.Conversation, 1, default); await reading.MarkReadAsync(ids.Conversation, 0, default);
        Assert.False((await db.Conversations.SingleAsync()).HasUnreadProactive); Assert.Equal(before, (await db.Conversations.SingleAsync()).UpdatedAt);
        Assert.Equal(1L, Assert.Single(await reading.ActivityAsync(default)).ReadProactiveSequence);
    }
    [Theory][InlineData("accepted", false)][InlineData("success", true)]
    public async Task NotificationAcceptanceAndNativePostAreDifferentAndRetriesKeepCommandId(string status, bool posted) {
        await using var db = MemoryDb(); var clock = new Clock(); await Seed(db, clock); var store = new ReminderInterventionStore(db, clock); await Recover(store, clock); await store.CompleteAsync((await store.ClaimAsync(default))!, Generated(), default);
        var dispatcher = new Dispatcher { Status = "failed" }; var processor = new ProactiveNotificationProcessor(db, store, new NodeRegistry(db, clock), dispatcher, clock);
        await processor.ProcessNextAsync(default); await processor.ProcessNextAsync(default); var id = dispatcher.Command!.CommandId;
        dispatcher.Status = status; clock.Now = clock.Now.AddSeconds(10); await processor.ProcessNextAsync(default);
        Assert.Equal(id, dispatcher.Command.CommandId); var delivery = await db.Set<ProactiveNotification>().SingleAsync(n => n.NodeId != null);
        Assert.NotNull(delivery.AcceptedAt); Assert.Equal(posted, delivery.PostedAt is not null); Assert.False(await processor.ProcessNextAsync(default));
    }
    [Fact] public async Task NormalAssistantResponseCreatesNoProactiveNotificationOrUnreadMarker() {
        await using var db = MemoryDb(); var clock = new Clock(); await Seed(db, clock); db.ChatMessages.Add((await db.Conversations.SingleAsync()).AddMessage("assistant", "Resposta normal")); await db.SaveChangesAsync();
        Assert.Empty(db.Set<ProactiveNotification>()); Assert.False((await db.Conversations.SingleAsync()).HasUnreadProactive);
    }
    [Theory][InlineData(true,false)][InlineData(false,true)]
    public async Task ModelCannotReturnToolsOrIncompleteProactiveText(bool tools, bool incomplete) {
        await using var db = MemoryDb(); var clock = new Clock(); await Seed(db,clock); var store = new ReminderInterventionStore(db,clock); await Recover(store,clock);
        var model = new Model { ReturnTool = tools, Incomplete = incomplete };
        var claim = (await store.ClaimAsync(default))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ProactiveMessageGenerator(model,new Prompts()).GenerateAsync(claim,default));
        Assert.Empty(db.ChatMessages.Where(m => m.IsProactive)); Assert.Empty(db.Set<ProactiveNotification>());
    }
    private sealed class Dispatcher : INodeNotificationDispatcher {
        public int Calls; public string Status = "permission_denied"; public NodeNotificationCommand? Command;
        public Task<NotificationDispatchResult> DispatchAsync(Guid actor, NotificationShowRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<NotificationDispatchResult> DispatchToNodeAsync(Guid id, NodeNotificationCommand command, CancellationToken ct = default) { Calls++; Command = command; return Task.FromResult(new NotificationDispatchResult(new(id,"synthetic"), "online", Status == "accepted" ? "fcm" : "live_websocket", Status, command.CommandId)); }
    }
    private sealed class Prompts : IPromptBuilder {
        public Task<PromptBuildResult> BuildPromptAsync(IReadOnlyList<ChatMessage> recentHistory, string currentUserMessage, string? pendingActionState = null, CancellationToken cancellationToken = default, string? automaticMemoryContext = null) => Task.FromResult(new PromptBuildResult("Você é Aegis, uma assistente feminina.", null, []));
    }
    private sealed class Model : IAegisModelClient {
        public ModelToolRequest? Request; public bool Incomplete; public bool ReturnTool;
        public Task<ModelToolResponse> RespondWithToolsAsync(ModelToolRequest request, CancellationToken cancellationToken = default) { Request = request; var result = Generated(); return Task.FromResult(new ModelToolResponse(result.Content,"openai",result.Model,ModelPurpose.Chat,ReturnTool ? [new("call", "external_action", JsonSerializer.SerializeToElement(new { }))] : [],[],null,Incomplete ? "{\"status\":\"incomplete\"}" : null,result.Audit)); }
        public IAsyncEnumerable<ModelToolStreamChunk> RespondWithToolsStreamAsync(ModelToolRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [NodePostgresTests.PostgresFact] public Task ManualTitleCommittedDuringGenerationCannotBeOverwritten() => WithPostgres(async create => {
        var clock = new Clock(); Guid id;
        await using (var seed = create()) { id = (await Seed(seed,clock)).Conversation; }
        await using var generation = create(); var stale = await generation.GetConversationWithMessagesAsync(id);
        await using (var user = create()) { (await user.Conversations.SingleAsync()).Rename("Título escolhido pelo usuário"); await user.SaveChangesAsync(); }
        Assert.False(await generation.SetGeneratedTitleIfEligibleAsync(id,"Comprar leite na saída","Comprar leite na saída",default));
        await using var check = create(); Assert.Equal("Título escolhido pelo usuário",(await check.Conversations.SingleAsync()).Title);
    });
    [Fact] public async Task CurrentDiagnosisChangingDuringGenerationRequeuesInsteadOfPublishingWrongCause() {
        await using var db = MemoryDb(); var clock = new Clock(); await Seed(db,clock); var store = new ReminderInterventionStore(db,clock); await Recover(store,clock);
        var claim = (await store.ClaimAsync(default))!; var rule = await db.Set<GeographicReminderRule>().SingleAsync();
        rule.Confirm(1,"recoverable_error","background_location_required",clock.Now); await db.SaveChangesAsync();
        Assert.False(await store.CompleteAsync(claim,Generated(),default));
        var fresh = (await store.ClaimAsync(default))!; Assert.Contains("segundo plano",fresh.Diagnosis);
        Assert.Empty(db.ChatMessages.Where(m=>m.IsProactive));
    }

    private static async Task WithPostgres(Func<Func<AegisDbContext>, Task> run) {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("AEGIS_NODE_TEST_DATABASE"));
        if (connection.Database != "aegis_nodes_test") throw new InvalidOperationException("Disposable aegis_nodes_test required.");
        var schema = "intervention_" + Guid.NewGuid().ToString("N"); connection.SearchPath = schema;
        AegisDbContext Create() => new(new DbContextOptionsBuilder<AegisDbContext>().UseNpgsql(connection.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory",schema)).Options);
        await using var db = Create(); await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA \"" + schema + "\"");
        try { await db.Database.MigrateAsync(); await run(Create); } finally { await db.Database.ExecuteSqlRawAsync("DROP SCHEMA \"" + schema + "\" CASCADE"); }
    }
    [NodePostgresTests.PostgresFact] public Task ConcurrentWorkersAndRestartsProduceOneMessageAndOneNotificationIntent() => WithPostgres(async create => {
        var clock = new Clock(); await using (var seed = create()) { await Seed(seed,clock); await Recover(new(seed,clock),clock); }
        await using var a = create(); await using var b = create(); var sa = new ReminderInterventionStore(a,clock); var sb = new ReminderInterventionStore(b,clock);
        var claims = await Task.WhenAll(sa.ClaimAsync(default), sb.ClaimAsync(default)); var claim = Assert.Single(claims, c => c is not null)!;
        Assert.True(await sa.CompleteAsync(claim,Generated(),default)); Assert.False(await sb.CompleteAsync(claim,Generated(),default));
        await using var check = create(); Assert.Single(await check.ChatMessages.Where(m => m.IsProactive).ToListAsync()); Assert.Single(await check.Set<ProactiveNotification>().ToListAsync());
        var summary = Assert.Single(await check.GetRecentConversationSummariesAsync(30)); Assert.True(summary.HasUnreadProactive);
    });
    [NodePostgresTests.PostgresFact] public Task ConcurrentDeviceReadsAreMonotonicAndCannotAcknowledgeFutureMessage() => WithPostgres(async create => {
        var clock = new Clock(); Guid id;
        await using (var seed = create()) { var ids = await Seed(seed,clock); id = ids.Conversation; var store = new ReminderInterventionStore(seed,clock); await Recover(store,clock); await store.CompleteAsync((await store.ClaimAsync(default))!,Generated(),default); }
        await using var a = create(); await using var b = create();
        await Task.WhenAll(new ConversationProactivity(a).MarkReadAsync(id,1,default),new ConversationProactivity(b).MarkReadAsync(id,0,default));
        await using var check = create(); var conversation = await check.Conversations.SingleAsync(); Assert.Equal(1,conversation.ReadProactiveSequence);
        check.ChatMessages.Add(conversation.AddProactiveMessage("Outro incidente",clock.Now.AddMinutes(1))); await check.SaveChangesAsync();
        await new ConversationProactivity(a).MarkReadAsync(id,1,default);
        check.ChangeTracker.Clear(); conversation = await check.Conversations.SingleAsync(); Assert.True(conversation.HasUnreadProactive); Assert.Equal(1,conversation.ReadProactiveSequence);
    });
}
