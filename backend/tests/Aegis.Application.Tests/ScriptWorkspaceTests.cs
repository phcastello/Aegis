using System.Text.Json;
using Aegis.Application.Tools;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Persistence;
using Aegis.Infrastructure.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class ScriptWorkspaceTests
{
    private static readonly PythonScriptResult Ok = new(true, "", "");

    [Fact]
    public async Task OpensSelectedOriginalsIncludingGeneratedFilesAndResumesWorkWithoutCrossingConversations()
    {
        using var app = new ScriptWorkspaceFixture();
        var chat = app.Conversation();
        var other = app.Conversation();
        var userFile = app.Attach(chat, "user", "dados.csv", "first"u8.ToArray());
        var generated = app.Attach(chat, "assistant", "dados.csv", "generated"u8.ToArray());
        var foreign = app.Attach(other, "user", "oculto.csv", "foreign"u8.ToArray());
        var context = new ToolExecutionContext(chat.Id, userFile.MessageId, "");
        var result = await app.Workspace.RunAsync(context, [userFile.Id.ToString()], async (dirs, ct) =>
        {
            using var index = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(dirs.Inputs, "index.json"), ct));
            Assert.Equal(2, index.RootElement.GetArrayLength());
            Assert.DoesNotContain("oculto", index.RootElement.ToString());
            Assert.Contains(index.RootElement.EnumerateArray(), row =>
                row.GetProperty("id").GetGuid() == generated.Id && row.GetProperty("role").GetString() == "assistant" &&
                row.GetProperty("messageId").GetGuid() == generated.MessageId);
            Assert.False(File.Exists(Path.Combine(dirs.Inputs, generated.Id.ToString("N"))));
            Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(dirs.Inputs, userFile.Id.ToString("N")), ct));
            Directory.CreateDirectory(Path.Combine(dirs.Work, "parcial"));
            await File.WriteAllTextAsync(Path.Combine(dirs.Work, "parcial", "valor"), "42", ct);
            await File.WriteAllTextAsync(Path.Combine(dirs.Outputs, "entrega.csv"), "total\n42", ct);
            return Ok;
        }, default);
        Assert.True(result.Success, result.Stderr);
        Assert.Equal("entrega.csv", Assert.Single(result.GeneratedFiles!));
        Assert.Equal("total\n42", app.Outputs["entrega.csv"]);

        // A new service instance must reopen committed state, ignoring failed mutations.
        var reopened = app.NewWorkspace();
        var failed = await reopened.RunAsync(context, ["dados.csv"], (_, _) => throw new Xunit.Sdk.XunitException("ambiguous name ran"), default);
        Assert.False(failed.Success);
        failed = await reopened.RunAsync(context, [foreign.Id.ToString()], (_, _) => throw new Xunit.Sdk.XunitException("foreign id ran"), default);
        Assert.False(failed.Success);
        failed = await reopened.RunAsync(context, [], (dirs, _) =>
        {
            File.WriteAllText(Path.Combine(dirs.Work, "parcial", "valor"), "incorrect");
            return Task.FromResult(new PythonScriptResult(false, "", "failure"));
        }, default);
        Assert.False(failed.Success);
        result = await reopened.RunAsync(context, [generated.Id.ToString()], (dirs, _) =>
        {
            Assert.Empty(Directory.GetFiles(dirs.Outputs));
            Assert.Equal("42", File.ReadAllText(Path.Combine(dirs.Work, "parcial", "valor")));
            Assert.Equal("generated", File.ReadAllText(Path.Combine(dirs.Inputs, generated.Id.ToString("N"))));
            return Task.FromResult(Ok);
        }, default);
        Assert.True(result.Success, result.Stderr);
        var second = await reopened.RunAsync(new(other.Id, foreign.MessageId, ""), [], (dirs, _) =>
        {
            Assert.Empty(Directory.GetFileSystemEntries(dirs.Work));
            return Task.FromResult(Ok);
        }, default);
        Assert.True(second.Success);
    }

    [Fact]
    public async Task ConcurrentTurnsSerializeTheirWritesAndACancelledWaitLeavesTheWorkspaceAvailable()
    {
        using var app = new ScriptWorkspaceFixture();
        var chat = app.Conversation();
        var context = new ToolExecutionContext(chat.Id, chat.Messages.First().Id, "");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = app.Workspace.RunAsync(context, [], async (dirs, ct) =>
        {
            started.SetResult();
            await finish.Task.WaitAsync(ct);
            await File.WriteAllTextAsync(Path.Combine(dirs.Work, "contador"), "1", ct);
            return Ok;
        }, default);
        await started.Task;
        using var cancelled = new CancellationTokenSource();
        var waiting = app.Workspace.RunAsync(context, [], (_, _) => throw new InvalidOperationException(), cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        var second = app.Workspace.RunAsync(context, [], (dirs, _) =>
        {
            Assert.Equal("1", File.ReadAllText(Path.Combine(dirs.Work, "contador")));
            return Task.FromResult(Ok);
        }, default);
        finish.SetResult();
        Assert.True((await first).Success);
        Assert.True((await second).Success);
    }

    [Fact]
    public async Task MessageActivityRenewsRetentionAndALateMessageCannotReviveExpiredWork()
    {
        using var app = new ScriptWorkspaceFixture();
        var chat = app.Conversation();
        var context = new ToolExecutionContext(chat.Id, chat.Messages.First().Id, "");
        var initial = await app.Workspace.RunAsync(context, [], (dirs, _) =>
        {
            File.WriteAllText(Path.Combine(dirs.Work, "saved"), "ok");
            return Task.FromResult(Ok);
        }, default);
        Assert.True(initial.Success);
        app.Now += TimeSpan.FromHours(60);
        app.Attach(chat, "user", "retido.txt", "arquivo"u8.ToArray());
        app.Now += TimeSpan.FromHours(14);
        await app.Workspace.SweepAsync(default);
        Assert.True(File.Exists(app.Snapshot(chat.Id)));
        app.Now += TimeSpan.FromHours(59);
        app.Attach(chat, "user", "outro.txt", []);
        // No cleaner ran while the app was offline. This new message starts a fresh workspace.
        var second = await app.NewWorkspace().RunAsync(context, ["retido.txt"], (dirs, _) =>
        {
            Assert.Empty(Directory.GetFiles(dirs.Work));
            Assert.Equal(2, Directory.GetFiles(dirs.Inputs).Length);
            File.WriteAllText(Path.Combine(dirs.Work, "fresh"), "ok");
            return Task.FromResult(Ok);
        }, default);
        Assert.True(second.Success);
        chat.Delete();
        app.Db.SaveChanges();
        await app.Workspace.SweepAsync(default);
        Assert.False(File.Exists(app.Snapshot(chat.Id)));
    }
}

internal sealed class ScriptWorkspaceFixture : IDisposable, IScriptArtifactStore
{
    private readonly ServiceProvider services;
    private readonly IServiceScope scope;
    private readonly string storage = Directory.CreateTempSubdirectory().FullName;
    private readonly string scratch;
    private readonly Clock clock = new();
    public DateTimeOffset Now { get => clock.Value; set => clock.Value = value; }
    public Dictionary<string, string> Outputs { get; } = [];
    public AegisDbContext Db => scope.ServiceProvider.GetRequiredService<AegisDbContext>();
    public ScriptWorkspace Workspace { get; }
    public ScriptWorkspaceFixture(string? scratchParent = null)
    {
        scratch = Directory.CreateDirectory(Path.Combine(scratchParent ?? Path.GetTempPath(), Guid.NewGuid().ToString("N"))).FullName;
        string database = Guid.NewGuid().ToString();
        services = new ServiceCollection().AddDbContext<AegisDbContext>(o => o.UseInMemoryDatabase(database)).BuildServiceProvider();
        scope = services.CreateScope();
        Workspace = NewWorkspace();
    }
    public ScriptWorkspace NewWorkspace() => new(services.GetRequiredService<IServiceScopeFactory>(), this, clock,
        NullLogger<ScriptWorkspace>.Instance, storage, scratch);
    public Conversation Conversation()
    {
        var chat = new Conversation();
        chat.AddMessage("user", "pedido", Now);
        Db.AddConversation(chat);
        Db.SaveChanges();
        return chat;
    }
    public ChatAttachment Attach(Conversation chat, string role, string name, byte[] bytes)
    {
        var message = chat.AddMessage(role, "arquivo", Now);
        Db.AddChatMessage(message);
        var file = new ChatAttachment(message.Id, 0, name, "text/plain", bytes);
        Db.AddChatAttachments([file]);
        Db.SaveChanges();
        return file;
    }
    public string Snapshot(Guid id) => Path.Combine(storage, id.ToString("N"), "work.zip");
    public async Task<IReadOnlyDictionary<string, Guid>> StageAsync(IReadOnlyList<ScriptArtifactFile> files, ToolExecutionContext context, CancellationToken ct)
    {
        foreach (var file in files) Outputs[file.FileName] = await File.ReadAllTextAsync(file.FullPath, ct);
        return files.ToDictionary(f => f.FileName, _ => Guid.NewGuid());
    }
    public Task DiscardPendingAsync(IReadOnlyList<Guid> ids) => Task.CompletedTask;
    public void Dispose()
    {
        scope.Dispose();
        services.Dispose();
        Directory.Delete(scratch, true);
        Directory.Delete(storage, true);
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Value { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Value;
    }
}
