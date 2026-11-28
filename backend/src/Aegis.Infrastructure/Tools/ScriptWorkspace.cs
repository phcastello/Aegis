using System.IO.Compression;
using System.Text.Json;
using Aegis.Application.Chat;
using Aegis.Application.Tools;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aegis.Infrastructure.Tools;

internal sealed record ScriptDirectories(string Inputs, string Work, string Outputs);

public sealed class ScriptWorkspace : IScriptWorkspaceCleanup
{
    internal static readonly TimeSpan Retention = TimeSpan.FromHours(72);
    private const long MaxWorkBytes = 768L * 1024 * 1024;
    private const long DiskReserve = 1024L * 1024 * 1024;
    private const int MaxEntries = 4096;
    private readonly SemaphoreSlim[] locks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1)).ToArray();
    private readonly SemaphoreSlim diskWriter = new(1);
    private readonly string storage;
    private readonly string scratch;
    private readonly IServiceScopeFactory scopes;
    private readonly IScriptArtifactStore artifacts;
    private readonly TimeProvider clock;
    private readonly ILogger<ScriptWorkspace> logger;

    public ScriptWorkspace(IServiceScopeFactory scopes, IScriptArtifactStore artifacts, TimeProvider clock, ILogger<ScriptWorkspace> logger)
        : this(scopes, artifacts, clock, logger, "/var/lib/aegis/script-workspaces", "/run/aegis-script") { }

    internal ScriptWorkspace(IServiceScopeFactory scopes, IScriptArtifactStore artifacts, TimeProvider clock, ILogger<ScriptWorkspace> logger,
        string storage, string scratch)
    {
        this.scopes = scopes;
        this.artifacts = artifacts;
        this.clock = clock;
        this.logger = logger;
        this.storage = storage;
        this.scratch = scratch;
    }

    // Only mount a kernel-bounded filesystem writable into untrusted Python.
    internal bool HasBoundedScratch => ScriptFileSystem.IsBoundedScratch(scratch);
    private SemaphoreSlim Gate(Guid id) => locks[(uint)id.GetHashCode() % locks.Length];

    internal async Task<PythonScriptResult> RunAsync(ToolExecutionContext context, IReadOnlyList<string> selection,
        Func<ScriptDirectories, CancellationToken, Task<PythonScriptResult>> execute, CancellationToken ct)
    {
        var id = context.ConversationId;
        var gate = Gate(id);
        await gate.WaitAsync(ct);
        string durable = Path.Combine(storage, id.ToString("N")), ephemeral = Path.Combine(scratch, id.ToString("N"));
        string snapshot = Path.Combine(durable, "work.zip"), pending = Path.Combine(durable, "next.zip");
        string inputs = Path.Combine(durable, "inputs");
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AegisDbContext>();
            var latest = await ReconcileAsync(db, id, ct);
            if (latest is null) return Failed("workspace_unavailable", "Esta conversa não está disponível.");
            Directory.CreateDirectory(durable);
            var dirs = new ScriptDirectories(inputs, Path.Combine(ephemeral, "work"), Path.Combine(ephemeral, "outputs"));
            Directory.CreateDirectory(inputs);
            Directory.CreateDirectory(dirs.Work);
            Directory.CreateDirectory(dirs.Outputs);
            await PrepareInputsAsync(db, id, selection, inputs, ct);
            if (File.Exists(snapshot))
                ZipFile.ExtractToDirectory(snapshot, dirs.Work);

            var result = await execute(dirs, ct);
            if (!result.Success) return result;
            ct.ThrowIfCancellationRequested();

            var outputs = new DirectoryInfo(dirs.Outputs).EnumerateFileSystemInfos().Take(ChatUploadLimits.MaxFiles + 1).ToArray();
            if (outputs.Length > ChatUploadLimits.MaxFiles || outputs.Any(f => f is not FileInfo || f.LinkTarget is not null))
                throw new ArgumentException("Coloque até 20 arquivos regulares diretamente em /outputs. Para entregar uma pasta, compacte-a em um ZIP.");
            var work = InspectWork(dirs.Work);
            if (work.Count > 0)
            {
                await diskWriter.WaitAsync(ct);
                try
                {
                    EnsureDiskSpace(work.OfType<FileInfo>().Sum(f => f.Length));
                    await SaveSnapshotAsync(dirs.Work, work, pending, ct);
                    File.SetLastWriteTimeUtc(pending, latest.Value.UtcDateTime);
                }
                finally { diskWriter.Release(); }
            }

            var staged = await artifacts.StageAsync(outputs.OrderBy(f => f.Name, StringComparer.Ordinal)
                .Select(f => new ScriptArtifactFile(f.Name, f.FullName)).ToArray(), context, ct);
            if (work.Count > 0) File.Move(pending, snapshot, overwrite: true);
            else File.Delete(snapshot);
            context.Artifacts.Publish(staged);
            return result with { GeneratedFiles = staged.Keys.ToArray() };
        }
        catch (ArgumentException e) { return Failed("invalid_workspace_files", e.Message); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            logger.LogWarning("Script workspace could not complete for conversation {ConversationId}: {ErrorType}", id, e.GetType().Name);
            return Failed("workspace_io_failed", "Não foi possível preparar ou salvar os arquivos. Reduza o conjunto de trabalho e confira os nomes selecionados.");
        }
        finally
        {
            try
            {
                RemoveDirectory(ephemeral);
                RemoveDirectory(inputs);
                if (File.Exists(pending)) File.Delete(pending);
                if (Directory.Exists(durable) && !Directory.EnumerateFileSystemEntries(durable).Any()) Directory.Delete(durable);
            }
            finally { gate.Release(); }
        }
    }

    private async Task PrepareInputsAsync(AegisDbContext db, Guid conversationId, IReadOnlyList<string> selectors, string inputs, CancellationToken ct)
    {
        var catalog = await db.ChatAttachments.AsNoTracking()
            .Where(f => f.Message.ConversationId == conversationId && f.Message.Conversation!.DeletedAt == null)
            .OrderBy(f => f.Message.CreatedAt).ThenBy(f => f.MessageId).ThenBy(f => f.Position)
            .Select(f => new { id = f.Id, messageId = f.MessageId, role = f.Message.Role,
                createdAt = f.Message.CreatedAt, name = f.FileName, size = f.SizeBytes }).ToListAsync(ct);
        await using (var index = File.Create(Path.Combine(inputs, "index.json")))
            await JsonSerializer.SerializeAsync(index, catalog.Select(row => new
            {
                row.id, row.messageId, row.role, row.createdAt, row.name, row.size, path = "/inputs/" + row.id.ToString("N")
            }), cancellationToken: ct);
        var selected = new Dictionary<Guid, Guid>();
        foreach (string selector in selectors)
        {
            bool isId = Guid.TryParse(selector, out var id);
            var matches = catalog.Where(row => isId && row.id == id || row.name.Equals(selector, StringComparison.Ordinal)).ToList();
            if (matches.Count != 1)
                throw new ArgumentException("O nome está ausente ou é ambíguo. Consulte /inputs/index.json em uma execução sem input_files e indique cada ID desejado.");
            selected.TryAdd(matches[0].id, matches[0].messageId);
        }
        if (selected.Count == 0) return;
        // Only the requested originals touch disk. Their names never become host paths.
        await diskWriter.WaitAsync(ct);
        try
        {
            EnsureDiskSpace(catalog.Where(row => selected.ContainsKey(row.id)).Sum(row => (long)row.size));
            foreach (var (id, message) in selected)
            {
                var file = await db.GetAttachmentAsync(message, id, ct) ?? throw new IOException();
                await using var source = await db.OpenAttachmentContentAsync(file, ct);
                await using var copy = File.Create(Path.Combine(inputs, id.ToString("N")));
                await source.CopyToAsync(copy, ct);
                if (copy.Length != file.SizeBytes) throw new IOException();
            }
        }
        finally { diskWriter.Release(); }
    }

    private void EnsureDiskSpace(long bytes)
    {
        if (ScriptFileSystem.AvailableBytes(storage) < bytes + DiskReserve)
            throw new ArgumentException("Espaço insuficiente para esta execução. Selecione menos anexos de cada vez ou reduza os intermediários.");
    }

    private static IReadOnlyList<FileSystemInfo> InspectWork(string root)
    {
        var result = new List<FileSystemInfo>();
        long total = 0;
        var dirs = new Stack<DirectoryInfo>();
        dirs.Push(new DirectoryInfo(root));
        while (dirs.TryPop(out var dir))
        {
            foreach (var entry in dir.EnumerateFileSystemInfos())
            {
                var relative = Path.GetRelativePath(root, entry.FullName);
                if (entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                    relative.Contains('\\') || relative.Count(c => c == Path.DirectorySeparatorChar) > 32)
                    throw new ArgumentException("/work aceita arquivos regulares e pastas com até 32 níveis, sem links.");
                if (entry is DirectoryInfo child) dirs.Push(child);
                else if (entry is FileInfo file && file.Length <= ChatUploadLimits.MaxFileBytes) total += file.Length;
                else throw new ArgumentException("Um arquivo intermediário excede 512 MiB.");
                result.Add(entry);
                if (result.Count > MaxEntries || total > MaxWorkBytes)
                    throw new ArgumentException("O conjunto de intermediários em /work excede 768 MiB ou 4096 itens.");
            }
        }
        return result;
    }

    private static async Task SaveSnapshotAsync(string root, IReadOnlyList<FileSystemInfo> entries, string destination, CancellationToken ct)
    {
        await using var stream = File.Create(destination);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            string name = Path.GetRelativePath(root, entry.FullName).Replace(Path.DirectorySeparatorChar, '/');
            var saved = zip.CreateEntry(entry is DirectoryInfo ? name + "/" : name, CompressionLevel.NoCompression);
            if (entry is FileInfo file)
            {
                using var output = saved.Open();
                await using var source = file.OpenRead();
                await source.CopyToAsync(output, ct);
            }
        }
    }

    private static PythonScriptResult Failed(string code, string detail) => new(false, "", detail, code);

    public async Task DeleteAsync(Guid conversationId, CancellationToken ct)
    {
        var gate = Gate(conversationId);
        await gate.WaitAsync(ct);
        try
        {
            RemoveDirectory(Path.Combine(storage, conversationId.ToString("N")));
            RemoveDirectory(Path.Combine(scratch, conversationId.ToString("N")));
        }
        finally { gate.Release(); }
    }

    internal async Task SweepAsync(CancellationToken ct)
    {
        var ids = new HashSet<Guid>();
        foreach (var root in new[] { storage, scratch })
            if (Directory.Exists(root))
                foreach (var path in Directory.EnumerateDirectories(root))
                    if (Guid.TryParseExact(Path.GetFileName(path), "N", out var id)) ids.Add(id);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AegisDbContext>();
        foreach (var id in ids)
        {
            var gate = Gate(id);
            await gate.WaitAsync(ct);
            try { await ReconcileAsync(db, id, ct); }
            finally { gate.Release(); }
        }
    }

    private async Task<DateTimeOffset?> ReconcileAsync(AegisDbContext db, Guid id, CancellationToken ct)
    {
        string durable = Path.Combine(storage, id.ToString("N"));
        string snapshot = Path.Combine(durable, "work.zip");
        // Anything outside the committed snapshot belongs to a prior, interrupted run.
        RemoveDirectory(Path.Combine(scratch, id.ToString("N")));
        RemoveDirectory(Path.Combine(durable, "inputs"));
        if (File.Exists(Path.Combine(durable, "next.zip"))) File.Delete(Path.Combine(durable, "next.zip"));
        var messages = db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == id && m.Conversation!.DeletedAt == null)
            .Select(m => m.CreatedAt);
        var latest = await messages.MaxAsync(m => (DateTimeOffset?)m, ct);
        bool keep = File.Exists(snapshot) && latest.HasValue && clock.GetUtcNow() - latest.Value < Retention;
        if (keep)
        {
            // A new message after a long silence must not revive a workspace that
            // expired while the service was stopped or between cleanup passes.
            DateTimeOffset previous = File.GetLastWriteTimeUtc(snapshot);
            await foreach (var time in messages.Where(time => time > previous).OrderBy(time => time)
                .AsAsyncEnumerable().WithCancellation(ct))
            {
                if (time - previous >= Retention) { keep = false; break; }
                previous = time;
            }
            if (keep) File.SetLastWriteTimeUtc(snapshot, latest!.Value.UtcDateTime);
        }
        if (!keep) RemoveDirectory(durable);
        return latest;
    }

    private static void RemoveDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
