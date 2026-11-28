using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aegis.Application.Tools;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Aegis.Infrastructure.Tools;

internal sealed record ScriptProgram(string Code, string? Input);
internal sealed record ScriptExecution(string RuntimeRevision, ScriptProgram Program,
    Func<ScriptProgram, ScriptDirectories, CancellationToken, Task<PythonScriptResult>> Replay);
internal sealed record ScriptWorkState(Guid? Revision, string? Digest, bool Reproducible)
{
    public static readonly ScriptWorkState Empty = new(null, null, true);
    public static readonly ScriptWorkState Legacy = new(null, null, false);
}
internal sealed record ScriptIntermediate(string PathDigest, string? ContentDigest, long? Size);
internal sealed record ScriptWorkManifest(string? Digest, IReadOnlyList<ScriptIntermediate> Entries, int Count);
internal sealed record StoredScriptExecution(Guid UserMessageId, bool Success, string? Error, long DurationMilliseconds,
    string RuntimeRevision, string ScriptDigest, string? SanitizedCode, Guid[] SourceAttachments,
    ScriptWorkState Work, Guid? Parent, bool MutatedWorkspace, int Count, IReadOnlyList<ScriptIntermediate> Intermediates,
    Guid[] StagedOutputs, string? ProtectedProgram, Guid? RestoredFrom);
internal sealed record ScriptReplayStep(ScriptProgram Program, Guid[] Attachments, string? ExpectedDigest);
internal sealed record ScriptReplayPlan(Guid RestoredFrom, ScriptWorkState Work, IReadOnlyList<ScriptReplayStep> Steps);
internal sealed class ScriptRestoreException(string message) : Exception(message);

internal sealed class ScriptRecipes(AegisDbContext db, IDataProtectionProvider protection, TimeProvider clock)
{
    private const int MaximumReplaySteps = 20;
    private const int MaxAuditedIntermediates = 32;
    private static readonly TimeSpan AuditRetention = TimeSpan.FromDays(30);
    private const string ProtectionPurpose = "Aegis.ScriptWorkspace.Recipe.v1";

    public async Task RecordAsync(Guid id, ToolExecutionContext context, ScriptExecution execution, PythonScriptResult result,
        long milliseconds, IReadOnlyCollection<Guid> inputs, IReadOnlyDictionary<string, Guid> outputs,
        ScriptWorkState work, Guid? parent, ScriptWorkManifest manifest, bool mutatedWorkspace, Guid? restoredFrom,
        CancellationToken ct)
    {
        string? replay = result.Success && (mutatedWorkspace && work is { Revision: not null, Reproducible: true } || outputs.Count > 0)
            ? Protector(context.ConversationId, id).Protect(JsonSerializer.Serialize(execution.Program))
            : null;
        var record = new StoredScriptExecution(context.UserMessageId, result.Success, result.ErrorCode, milliseconds,
            execution.RuntimeRevision, Digest(execution.Program.Code), result.SanitizedCode, inputs.ToArray(), work,
            parent, mutatedWorkspace, manifest.Count, manifest.Entries,
            outputs.Values.ToArray(), replay, restoredFrom);
        var now = clock.GetUtcNow();
        var retained = result.Success && (mutatedWorkspace || outputs.Count > 0 || restoredFrom.HasValue);
        db.ToolContextEntries.Add(new ToolContextEntry(context.ConversationId, ScriptRecipeReferences.Scope,
            ScriptRecipeReferences.Execution, id.ToString("N"), JsonSerializer.Serialize(record),
            PythonScriptTool.ToolName, retained ? DateTimeOffset.MaxValue : now + AuditRetention, now));
        await db.SaveChangesAsync(ct);
    }

    public async Task<ScriptReplayPlan> PlanAsync(Guid conversationId, string selector, ScriptWorkState available,
        string runtimeRevision, CancellationToken ct)
    {
        try
        {
            var records = db.ToolContextEntries.AsNoTracking().Where(entry =>
                entry.ConversationId == conversationId && entry.Scope == ScriptRecipeReferences.Scope);
            var targetId = await ResolveDeliveryAsync(records, conversationId, selector, ct);
            var target = await FindExecutionAsync(records, targetId, ct);
            if (target.Success && target.Work == available)
                return new ScriptReplayPlan(targetId, available, []);
            if (!target.Success || !target.Work.Reproducible)
                throw new ScriptRestoreException("A entrega foi criada antes de existir uma receita completa. Refaça a análise usando os anexos originais.");

            var steps = new List<ScriptReplayStep>();
            var visited = new HashSet<Guid>();
            var current = target.Work.Revision;
            while (current is { } id)
            {
                if (steps.Count >= MaximumReplaySteps || !visited.Add(id))
                    throw new ScriptRestoreException("A receita é longa demais para a recuperação automática. Refaça a etapa necessária usando os arquivos da conversa.");
                var entry = id == targetId ? target : await FindExecutionAsync(records, id, ct);
                if (!entry.Success || !entry.MutatedWorkspace || entry.ProtectedProgram is null ||
                    entry.Work.Revision != id || !entry.RuntimeRevision.Equals(runtimeRevision, StringComparison.Ordinal))
                    throw new ScriptRestoreException("A receita exige uma etapa ou versão do Python indisponível. Refaça a etapa necessária usando os arquivos da conversa.");
                var json = Protector(conversationId, id).Unprotect(entry.ProtectedProgram);
                var program = JsonSerializer.Deserialize<ScriptProgram>(json)!;
                steps.Add(new ScriptReplayStep(program, entry.SourceAttachments, entry.Work.Digest));
                current = entry.Parent;
            }
            steps.Reverse();
            return new ScriptReplayPlan(targetId, target.Work, steps);
        }
        catch (Exception error) when (error is JsonException or CryptographicException)
        {
            throw new ScriptRestoreException("A receita antiga não pôde ser lida. Refaça a análise usando os arquivos originais.");
        }
    }

    private async Task<Guid> ResolveDeliveryAsync(IQueryable<ToolContextEntry> entries,
        Guid conversation, string selector, CancellationToken ct)
    {
        string? row;
        if (selector.Equals("latest", StringComparison.OrdinalIgnoreCase))
        {
            row = await entries.Where(entry => entry.EntryType == ScriptRecipeReferences.Delivery)
                .OrderByDescending(entry => entry.CreatedAt).ThenByDescending(entry => entry.Id)
                .Select(entry => entry.DataJson).FirstOrDefaultAsync(ct);
        }
        else
        {
            var files = db.ChatAttachments.AsNoTracking().Where(file => file.Message.ConversationId == conversation &&
                file.Message.Conversation!.DeletedAt == null);
            files = Guid.TryParse(selector, out var id) ? files.Where(file => file.Id == id)
                : files.Where(file => file.FileName == selector);
            var ids = await files.OrderByDescending(file => file.Message.CreatedAt).ThenByDescending(file => file.Id)
                .Select(file => file.Id).Take(40).ToListAsync(ct);
            var keys = ids.Select(file => file.ToString("N")).ToArray();
            var matching = await entries.Where(entry => entry.EntryType == ScriptRecipeReferences.Output && keys.Contains(entry.Key))
                .Select(entry => new { entry.Key, entry.DataJson }).ToDictionaryAsync(entry => entry.Key, entry => entry.DataJson, ct);
            row = keys.Select(key => matching.GetValueOrDefault(key)).FirstOrDefault(value => value is not null);
        }
        if (row is null)
            throw new ScriptRestoreException("Nenhuma receita salva para esta entrega na conversa. Refaça a etapa a partir dos anexos disponíveis.");
        using var json = JsonDocument.Parse(row);
        return json.RootElement.GetProperty("ExecutionId").GetGuid();
    }

    private static async Task<StoredScriptExecution> FindExecutionAsync(IQueryable<ToolContextEntry> entries, Guid id,
        CancellationToken ct)
    {
        var key = id.ToString("N");
        var json = await entries.Where(entry => entry.EntryType == ScriptRecipeReferences.Execution && entry.Key == key)
            .Select(entry => entry.DataJson).SingleOrDefaultAsync(ct);
        return (json is null ? null : JsonSerializer.Deserialize<StoredScriptExecution>(json))
            ?? throw new ScriptRestoreException("A receita está incompleta. Refaça a análise usando os arquivos originais.");
    }

    public static async Task<ScriptWorkManifest> ManifestAsync(string root, IReadOnlyList<FileSystemInfo> entries, CancellationToken ct)
    {
        if (entries.Count == 0) return new ScriptWorkManifest(null, [], 0);
        using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var sampled = new List<ScriptIntermediate>(Math.Min(entries.Count, MaxAuditedIntermediates));
        foreach (var item in entries.OrderBy(entry => entry.FullName, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var path = Digest(Path.GetRelativePath(root, item.FullName).Replace(Path.DirectorySeparatorChar, '/'));
            fingerprint.AppendData(Convert.FromHexString(path));
            if (item is FileInfo file)
            {
                await using var contents = file.OpenRead();
                var hash = await SHA256.HashDataAsync(contents, ct);
                fingerprint.AppendData([1]);
                fingerprint.AppendData(hash);
                if (sampled.Count < MaxAuditedIntermediates)
                    sampled.Add(new ScriptIntermediate(path, Convert.ToHexString(hash), file.Length));
            }
            else
            {
                fingerprint.AppendData([0]);
                if (sampled.Count < MaxAuditedIntermediates)
                    sampled.Add(new ScriptIntermediate(path, null, null));
            }
        }
        return new ScriptWorkManifest(Convert.ToHexString(fingerprint.GetHashAndReset()), sampled, entries.Count);
    }

    public static async Task ForgetExpiredAsync(AegisDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var expired = db.ToolContextEntries.Where(entry => entry.Scope == ScriptRecipeReferences.Scope &&
            (entry.ExpiresAt <= now || entry.Conversation!.DeletedAt != null));
        if (db.Database.IsRelational()) await expired.ExecuteDeleteAsync(ct);
        else
        {
            db.ToolContextEntries.RemoveRange(await expired.ToListAsync(ct));
            await db.SaveChangesAsync(ct);
        }
    }

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private IDataProtector Protector(Guid conversationId, Guid executionId) =>
        protection.CreateProtector(ProtectionPurpose, conversationId.ToString("N"), executionId.ToString("N"));
}
