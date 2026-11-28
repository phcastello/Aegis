using Aegis.Application.Chat;
using Aegis.Application.Llm;
using Aegis.Application.Models;
using Aegis.Application.Prompts;
using Aegis.Application.Tools;
using Aegis.Application.Voice;
using Aegis.Domain;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aegis.Infrastructure.Chat;

public sealed class ResearchJobStore(AegisDbContext db, TimeProvider clock,
    IDataProtectionProvider? protection = null, IOwnerInstructions? ownerInstructions = null)
    : IResearchDelegationQueue, IBackgroundWorkStatusReader
{
    public const int MaximumRequestLength = 12_000;
    internal const int MaximumSources = 128;
    internal const int MaximumSourceUrlLength = 2_048;
    private const int MaximumRecentJobs = 20;
    private static readonly TimeSpan JobTimeout = TimeSpan.FromMinutes(3);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string DelegationKey = "researchDelegation";
    private const string PythonDelegationKey = "pythonDelegation";
    private const string VideoDelegationKey = "videoDelegation";
    private const string PinnedInstructionsKey = "backgroundInstructions";
    private readonly IDataProtector? instructionsProtector = protection?.CreateProtector("Aegis.ResearchJob.OwnerInstructions.v1");

    public void Stage(ChatMessage userMessage, string brief, Guid? voiceSessionId = null, bool isPython = false,
        bool isVideo = false, InstructionSettings? instructions = null)
    {
        if (userMessage.Role != ChatRoles.User || db.Entry(userMessage).State != EntityState.Unchanged ||
            string.IsNullOrWhiteSpace(brief) || brief.Length > IResearchDelegationQueue.MaximumBriefLength)
            throw new ArgumentException("Informe uma mensagem aceita e uma tarefa válida.");

        var previous = userMessage.MetadataJson is { } json ? JsonNode.Parse(json) : null;
        var metadata = previous as JsonObject ?? new JsonObject();
        if (previous is not null && previous is not JsonObject) metadata["previousMetadata"] = previous;
        var delegation = new JsonObject { ["version"] = 1, ["brief"] = brief.Trim() };
        if (voiceSessionId is { } session && session != Guid.Empty) delegation["voiceSessionId"] = JsonValue.Create(session);
        metadata[isVideo ? VideoDelegationKey : isPython ? PythonDelegationKey : DelegationKey] = delegation;
        PinInstructions(metadata, instructions);
        userMessage.AttachAuditData(userMessage.Model, userMessage.PromptSnapshot, userMessage.RuntimeContextSnapshot,
            metadata.ToJsonString(JsonOptions));
        db.ResearchJobs.Add(new ResearchJob(userMessage.Id, userMessage.ConversationId, userMessage.Id, clock.GetUtcNow()));
    }

    public async Task<ResearchJobSnapshot> CreateAsync(CreateResearchJobRequest request, CancellationToken ct)
    {
        var content = request.Content?.Trim();
        if (request.JobId == Guid.Empty || request.ConversationId == Guid.Empty ||
            string.IsNullOrEmpty(content) || content.Length > MaximumRequestLength)
            throw new ArgumentException("Informe um pedido de pesquisa válido e seu identificador.");

        var replay = await FindReplayAsync(request, content, ct);
        if (replay is not null) return replay;

        var conversation = request.ConversationId is { } conversationId
            ? await db.Conversations.SingleOrDefaultAsync(c => c.Id == conversationId && c.DeletedAt == null, ct)
                ?? throw new ConversationNotFoundException(conversationId)
            : new Conversation(Conversation.DefaultTitle);
        if (request.ConversationId is null) db.Conversations.Add(conversation);

        var now = clock.GetUtcNow();
        var userMessage = conversation.AddMessage(ChatRoles.User, content, now);
        if (ownerInstructions is not null)
        {
            var metadata = new JsonObject();
            PinInstructions(metadata, await ownerInstructions.ReadAsync(ct));
            userMessage.AttachAuditData(null, null, null, metadata.ToJsonString(JsonOptions));
        }
        db.ChatMessages.Add(userMessage);
        var job = new ResearchJob(request.JobId, conversation.Id, userMessage.Id, now);
        db.ResearchJobs.Add(job);
        try
        {
            // Saving together means a losing retry never creates an extra message or conversation.
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return await FindReplayAsync(request, content, ct) ?? throw new InvalidOperationException("Research job conflict.", e);
        }

        return Snapshot(job);
    }

    private async Task<ResearchJobSnapshot?> FindReplayAsync(CreateResearchJobRequest request, string content, CancellationToken ct)
    {
        var job = await VisibleJobs().AsNoTracking().SingleOrDefaultAsync(item => item.Id == request.JobId, ct);
        if (job is null) return null;

        var prior = await db.ChatMessages.Where(message => message.Id == job.UserMessageId)
            .Select(message => new { message.Content, message.MetadataJson }).SingleAsync(ct);
        if ((request.ConversationId is { } id && id != job.ConversationId) || prior.Content != content ||
            job.Id == job.UserMessageId && ReadDelegation(prior.MetadataJson) is { IsPython: true } or { IsVideo: true })
            throw new ResearchJobConflictException();
        return Snapshot(job);
    }

    public async Task<ResearchJobSnapshot?> GetAsync(Guid id, CancellationToken ct)
    {
        var job = await VisibleJobs().AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, ct);
        return job is null ? null : Snapshot(job, await ReadMetadataAsync(job, ct));
    }

    public async Task<ResearchVoiceResult?> GetVoiceResultAsync(Guid id, Guid voiceSessionId, CancellationToken ct)
    {
        if (voiceSessionId == Guid.Empty) return null;
        var result = await (
            from job in VisibleJobs().AsNoTracking()
            join message in db.ChatMessages.AsNoTracking() on job.UserMessageId equals message.Id
            where job.Id == id && job.Id == job.UserMessageId && job.State != ResearchJob.Cancelled &&
                message.ConversationId == job.ConversationId && message.Role == ChatRoles.User
            select new { job.State, job.ConversationId, job.ResultMessageId, message.MetadataJson })
            .SingleOrDefaultAsync(ct);
        if (result is null) return null;
        var delegation = ReadDelegation(result.MetadataJson);
        if (delegation.VoiceSessionId != voiceSessionId) return null;
        if (result.State == ResearchJob.Failed)
            return new(result.ConversationId, delegation.IsVideo
                ? "Tive um problema e não consegui terminar a análise do vídeo."
                : delegation.IsPython
                ? "Tive um problema e não consegui concluir a tarefa."
                : "Tive um problema e não consegui concluir a pesquisa.");
        if (result.State is ResearchJob.Queued or ResearchJob.Running)
            return new(result.ConversationId, null);

        var answer = await db.ChatMessages.AsNoTracking()
            .Where(message => message.Id == result.ResultMessageId && message.ConversationId == result.ConversationId &&
                message.Role == ChatRoles.Assistant)
            .Select(message => message.Content).SingleOrDefaultAsync(ct);
        return answer is null ? null : new(result.ConversationId, ResearchSpeechText.FromAnswer(answer));
    }

    public async Task<IReadOnlyList<ResearchJobSnapshot>> ListAsync(Guid conversationId, CancellationToken ct)
    {
        var jobs = await VisibleJobs().AsNoTracking().Where(job => job.ConversationId == conversationId)
            .OrderByDescending(job => job.State == ResearchJob.Queued || job.State == ResearchJob.Running)
            .ThenByDescending(job => job.CreatedAt).ThenByDescending(job => job.Id)
            .Take(MaximumRecentJobs).ToListAsync(ct);
        var delegated = jobs.Where(job => job.Id == job.UserMessageId).Select(job => job.UserMessageId).ToArray();
        var metadata = await db.ChatMessages.AsNoTracking().Where(message => delegated.Contains(message.Id))
            .ToDictionaryAsync(message => message.Id, message => message.MetadataJson, ct);
        return jobs.Select(job => Snapshot(job, metadata.GetValueOrDefault(job.UserMessageId))).ToArray();
    }

    public async Task<IReadOnlyList<BackgroundWorkStatus>> ReadStatusAsync(
        Guid conversationId, bool includeRecentVoiceContext, CancellationToken ct)
    {
        var visible = VisibleJobs().AsNoTracking();
        var rows = await ReadStatusRowsAsync(visible.Where(job => job.ConversationId == conversationId), ct);
        if (rows.Count == 0 && includeRecentVoiceContext)
        {
            // New wake sessions have no conversation ID from the previous session. Give their
            // owner a recent status without carrying request text across conversations.
            var recent = clock.GetUtcNow() - TimeSpan.FromMinutes(10);
            rows = await ReadStatusRowsAsync(visible.Where(job => job.State == ResearchJob.Queued ||
                job.State == ResearchJob.Running || job.UpdatedAt >= recent), ct);
        }
        return rows.Select(row =>
        {
            var current = row.Job.ConversationId == conversationId;
            var snapshot = Snapshot(row.Job, row.Metadata);
            return new BackgroundWorkStatus(snapshot.Kind ?? "web_search", snapshot.State, snapshot.Phase,
                snapshot.UpdatedAt, snapshot.Sources.Count, current ? ShortRequest(row.Request) : "", current);
        }).ToArray();
    }

    private sealed record StatusRow(ResearchJob Job, string Request, string? Metadata);

    private Task<List<StatusRow>> ReadStatusRowsAsync(IQueryable<ResearchJob> jobs, CancellationToken ct) =>
        (from job in jobs
         join message in db.ChatMessages.AsNoTracking() on job.UserMessageId equals message.Id
         where message.ConversationId == job.ConversationId && message.Role == ChatRoles.User
         orderby (job.State == ResearchJob.Queued || job.State == ResearchJob.Running) descending, job.UpdatedAt descending, job.Id descending
         select new StatusRow(job, message.Content, message.MetadataJson)).Take(5).ToListAsync(ct);

    private static string ShortRequest(string text) => text.Length <= 240 ? text : text[..240] + "…";

    public async Task<ResearchJobSnapshot?> CancelAsync(Guid id, CancellationToken ct)
    {
        while (true)
        {
            var job = await VisibleJobs().SingleOrDefaultAsync(item => item.Id == id, ct);
            if (job is null) return null;
            var metadata = await ReadMetadataAsync(job, ct);
            if (job.State is not (ResearchJob.Queued or ResearchJob.Running)) return Snapshot(job, metadata);
            job.Cancel(clock.GetUtcNow());
            if (await SaveTransitionAsync(ct)) return Snapshot(job, metadata);
        }
    }

    internal async Task<ResearchJobInput?> ClaimAsync(CancellationToken ct, bool allowWeb = true, bool allowPython = false,
        bool allowVideo = false)
    {
        if (!allowWeb && !allowPython && !allowVideo) return null;
        await ExpireStaleJobsAsync(ct);
        var pending = VisibleJobs().Where(item => item.State == ResearchJob.Queued);
        if (!allowWeb || !allowPython || !allowVideo)
        {
            var stagedPython = DelegatedMessageIds(PythonDelegationKey);
            var stagedVideo = DelegatedMessageIds(VideoDelegationKey);
            pending = pending.Where(item =>
                allowPython && item.Id == item.UserMessageId && stagedPython.Contains(item.UserMessageId) ||
                allowVideo && item.Id == item.UserMessageId && stagedVideo.Contains(item.UserMessageId) ||
                allowWeb && (item.Id != item.UserMessageId ||
                    !stagedPython.Contains(item.UserMessageId) && !stagedVideo.Contains(item.UserMessageId)));
        }
        var job = await pending
            .OrderBy(item => item.CreatedAt).ThenBy(item => item.Id).FirstOrDefaultAsync(ct);
        if (job is null) return null;
        var message = await db.ChatMessages.Where(message => message.Id == job.UserMessageId)
            .Select(message => new { message.Content, message.MetadataJson }).SingleAsync(ct);
        var delegation = job.Id == job.UserMessageId ? ReadDelegation(message.MetadataJson) : default;
        var content = job.Id == job.UserMessageId ? delegation.Brief : message.Content;
        if (content is null)
        {
            job.Fail("research_incomplete", clock.GetUtcNow());
            await SaveTransitionAsync(ct);
            return null;
        }
        InstructionSettings? instructions;
        try
        {
            instructions = ReadPinnedInstructions(message.MetadataJson);
        }
        catch (CryptographicException)
        {
            job.Fail("background_instructions_unavailable", clock.GetUtcNow());
            await SaveTransitionAsync(ct);
            return null;
        }
        job.Start(clock.GetUtcNow(), JobTimeout);
        return await SaveTransitionAsync(ct)
            ? new ResearchJobInput(job.Id, job.ConversationId, job.UserMessageId,
                delegation.IsPython || delegation.IsVideo
                    ? $"Pedido original do usuário\n{message.Content}\n\nContexto da tarefa\n{content}" : content,
                job.DeadlineAt!.Value, delegation.VoiceSessionId.HasValue, delegation.IsPython, delegation.IsVideo, instructions)
            : null;
    }

    private void PinInstructions(JsonObject metadata, InstructionSettings? settings)
    {
        if (settings is null) return;
        var protector = instructionsProtector ?? throw new InvalidOperationException("A proteção dos dados do trabalho está indisponível.");
        metadata[PinnedInstructionsKey] = protector.Protect(JsonSerializer.Serialize(settings, JsonOptions));
    }

    private InstructionSettings? ReadPinnedInstructions(string? metadata)
    {
        if (metadata is null) return null;
        using var document = JsonDocument.Parse(metadata);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty(PinnedInstructionsKey, out var field))
            return null;
        if (instructionsProtector is null || field.ValueKind != JsonValueKind.String || field.GetString() is not { Length: > 0 } ciphertext)
            throw new CryptographicException("A revisão protegida do trabalho está indisponível.");
        try
        {
            return JsonSerializer.Deserialize<InstructionSettings>(instructionsProtector.Unprotect(ciphertext), JsonOptions)
                ?? throw new CryptographicException("A revisão protegida do trabalho está inválida.");
        }
        catch (JsonException error)
        {
            throw new CryptographicException("A revisão protegida do trabalho está inválida.", error);
        }
    }

    private IQueryable<Guid> DelegatedMessageIds(string key)
    {
        var messages = db.ChatMessages.Where(message => message.MetadataJson != null);
        return (db.Database.IsNpgsql()
            ? messages.Where(message => EF.Functions.JsonExists(message.MetadataJson!, key))
            : messages.Where(message => message.MetadataJson!.Contains("\"" + key + "\":")))
            .Select(message => message.Id);
    }

    internal async Task<ChatAttachmentContent> GetDelegatedAttachmentsAsync(ResearchJobInput job, CancellationToken ct)
    {
        var files = await db.GetMessageAttachmentsAsync(job.UserMessageId, ct);
        if (files.Count > 0) return await ChatAttachmentContent.FromStoredAsync(db, files, ct);
        var conversation = await db.GetConversationWithMessagesAsync(job.ConversationId, ct);
        var original = conversation?.Messages.FirstOrDefault(m => m.Id == job.UserMessageId);
        if (original is null) return new ChatAttachmentContent();
        return await ChatAttachmentContent.ForFollowUpAsync(db, job.ConversationId,
            conversation!.Messages.Where(m => m.CreatedAt < original.CreatedAt ||
                m.CreatedAt == original.CreatedAt && m.Id.CompareTo(original.Id) < 0),
            original.Content, ct);
    }

    private Task<string?> ReadMetadataAsync(ResearchJob job, CancellationToken ct) => job.Id != job.UserMessageId
        ? Task.FromResult<string?>(null)
        : db.ChatMessages.AsNoTracking().Where(message => message.Id == job.UserMessageId)
            .Select(message => message.MetadataJson).SingleOrDefaultAsync(ct);

    private static (string? Brief, Guid? VoiceSessionId, bool IsPython, bool IsVideo) ReadDelegation(string? metadata)
    {
        if (metadata is null) return default;
        using var document = JsonDocument.Parse(metadata);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return default;
        var isPython = root.TryGetProperty(PythonDelegationKey, out var delegation);
        var isVideo = !isPython && root.TryGetProperty(VideoDelegationKey, out delegation);
        if ((!isPython && !isVideo && !root.TryGetProperty(DelegationKey, out delegation)) || delegation.ValueKind != JsonValueKind.Object ||
            !delegation.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out var versionNumber) || versionNumber != 1 ||
            !delegation.TryGetProperty("brief", out var brief) || brief.ValueKind != JsonValueKind.String)
            return default;
        var text = brief.GetString();
        if (text is not { Length: <= IResearchDelegationQueue.MaximumBriefLength } || string.IsNullOrWhiteSpace(text))
            return default;
        Guid? session = delegation.TryGetProperty("voiceSessionId", out var voiceSessionId) &&
            voiceSessionId.ValueKind == JsonValueKind.String && voiceSessionId.TryGetGuid(out var parsed) && parsed != Guid.Empty
                ? parsed : null;
        return (text, session, isPython, isVideo);
    }

    internal Task<bool> IsRunningAsync(Guid id, CancellationToken ct) =>
        VisibleJobs().AnyAsync(job => job.Id == id && job.State == ResearchJob.Running, ct);

    internal async Task<bool> ReportAsync(Guid id, string? phase, IReadOnlyList<WebResearchSourceCount> sources, CancellationToken ct)
    {
        // Reload the tracked state from an API request that may have cancelled the job.
        db.ChangeTracker.Clear();
        var job = await VisibleJobs().SingleOrDefaultAsync(item => item.Id == id && item.State == ResearchJob.Running, ct);
        if (job is null) return false;
        var encoded = JsonSerializer.Serialize(sources, JsonOptions);
        if (phase == job.Phase && encoded == job.SourcesJson) return true;
        job.Report(phase, encoded, clock.GetUtcNow());
        return await SaveTransitionAsync(ct);
    }

    internal async Task CompleteAsync(ResearchJobInput input, ResearchJobResult result, CancellationToken ct, ScriptArtifacts? artifacts = null)
    {
        db.ChangeTracker.Clear();
        var job = await VisibleJobs().SingleOrDefaultAsync(item => item.Id == input.JobId && item.State == ResearchJob.Running, ct);
        if (job is null) return;

        var conversation = await db.Conversations.SingleOrDefaultAsync(
            item => item.Id == input.ConversationId && item.DeletedAt == null, ct);
        if (conversation is null) return;

        var now = clock.GetUtcNow();
        var message = conversation.AddMessage(ChatRoles.Assistant, result.Content, now);
        message.AttachAuditData(result.Model, null, null, result.MetadataJson);
        db.ChatMessages.Add(message);
        if (artifacts is not null) await artifacts.AttachAsync(db, message.Id, ct);
        AddAudit(job, message.Id, result.Audit);
        job.Complete(message.Id, now);
        await SaveTransitionAsync(ct);
    }

    internal async Task FailAsync(Guid id, string errorCode, LlmRequestAuditData? audit, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var job = await db.ResearchJobs.SingleOrDefaultAsync(item => item.Id == id && item.State == ResearchJob.Running, ct);
        if (job is null) return;
        if (audit is not null) AddAudit(job, null, audit);
        job.Fail(errorCode, clock.GetUtcNow());
        await SaveTransitionAsync(ct);
    }

    private void AddAudit(ResearchJob job, Guid? messageId, LlmRequestAuditData audit) =>
        db.LlmRequestAudits.Add(new LlmRequestAudit(job.ConversationId, job.UserMessageId, messageId,
            audit.Provider, audit.Model, audit.Success, audit.DurationMilliseconds, audit.RequestPayloadJson,
            audit.HttpStatusCode, audit.ResponseBody, audit.FailureReason, audit.ErrorType));

    private async Task ExpireStaleJobsAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var expired = await db.ResearchJobs
            .Where(job => job.State == ResearchJob.Running && job.DeadlineAt <= now)
            .OrderBy(job => job.DeadlineAt).Take(MaximumRecentJobs).ToListAsync(ct);
        foreach (var job in expired)
        {
            // Do not replay an interrupted provider request with unknown billing.
            var delegation = ReadDelegation(await ReadMetadataAsync(job, ct));
            job.Fail(delegation.IsVideo ? "video_interrupted" : delegation.IsPython ? "python_interrupted" : "research_interrupted", now);
            if (!await SaveTransitionAsync(ct)) return;
        }
    }

    private IQueryable<ResearchJob> VisibleJobs() => db.ResearchJobs.Where(job =>
        db.Conversations.Any(conversation => conversation.Id == job.ConversationId && conversation.DeletedAt == null));

    private async Task<bool> SaveTransitionAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    private static ResearchJobSnapshot Snapshot(ResearchJob job, string? metadata = null) => new(
        job.Id, job.ConversationId, job.UserMessageId, job.State, job.Phase, job.CreatedAt, job.UpdatedAt,
        job.ResultMessageId, job.ErrorCode, JsonSerializer.Deserialize<WebResearchSourceCount[]>(job.SourcesJson, JsonOptions) ?? [],
        ReadDelegation(metadata) switch { { IsVideo: true } => "video", { IsPython: true } => "python", _ => null });
}

public sealed class ResearchJobConflictException : Exception { }
