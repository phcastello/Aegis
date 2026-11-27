using Aegis.Application.Chat;
using Aegis.Application.Llm;
using Aegis.Application.Models;
using Aegis.Application.Voice;
using Aegis.Domain;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aegis.Infrastructure.Chat;

public sealed class ResearchJobStore(AegisDbContext db, TimeProvider clock) : IResearchDelegationQueue
{
    public const int MaximumRequestLength = 12_000;
    internal const int MaximumSources = 128;
    internal const int MaximumSourceUrlLength = 2_048;
    private const int MaximumRecentJobs = 20;
    private static readonly TimeSpan JobTimeout = TimeSpan.FromMinutes(3);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string DelegationKey = "researchDelegation";
    private const string PythonDelegationKey = "pythonDelegation";

    public void Stage(ChatMessage userMessage, string brief, Guid? voiceSessionId = null, bool isPython = false)
    {
        if (userMessage.Role != ChatRoles.User || db.Entry(userMessage).State != EntityState.Unchanged ||
            string.IsNullOrWhiteSpace(brief) || brief.Length > IResearchDelegationQueue.MaximumBriefLength)
            throw new ArgumentException("Informe uma mensagem aceita e uma tarefa válida.");

        var previous = userMessage.MetadataJson is { } json ? JsonNode.Parse(json) : null;
        var metadata = previous as JsonObject ?? new JsonObject();
        if (previous is not null && previous is not JsonObject) metadata["previousMetadata"] = previous;
        var delegation = new JsonObject { ["version"] = 1, ["brief"] = brief.Trim() };
        if (voiceSessionId is { } session && session != Guid.Empty) delegation["voiceSessionId"] = JsonValue.Create(session);
        metadata[isPython ? PythonDelegationKey : DelegationKey] = delegation;
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
            job.Id == job.UserMessageId && ReadDelegation(prior.MetadataJson).IsPython)
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
            return new(result.ConversationId, delegation.IsPython
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

    internal async Task<ResearchJobInput?> ClaimAsync(CancellationToken ct, bool allowWeb = true, bool allowPython = false)
    {
        if (!allowWeb && !allowPython) return null;
        await ExpireStaleJobsAsync(ct);
        var pending = VisibleJobs().Where(item => item.State == ResearchJob.Queued);
        if (allowWeb != allowPython)
        {
            var stagedPython = PythonJobMessageIds();
            pending = allowPython
                ? pending.Where(item => item.Id == item.UserMessageId && stagedPython.Contains(item.UserMessageId))
                : pending.Where(item => item.Id != item.UserMessageId || !stagedPython.Contains(item.UserMessageId));
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
        job.Start(clock.GetUtcNow(), JobTimeout);
        return await SaveTransitionAsync(ct)
            ? new ResearchJobInput(job.Id, job.ConversationId, job.UserMessageId,
                delegation.IsPython ? $"Pedido original do usuário\n{message.Content}\n\nContexto da tarefa\n{content}" : content,
                job.DeadlineAt!.Value, delegation.VoiceSessionId.HasValue, delegation.IsPython)
            : null;
    }

    private IQueryable<Guid> PythonJobMessageIds()
    {
        var messages = db.ChatMessages.Where(message => message.MetadataJson != null);
        var python = db.Database.IsNpgsql()
            ? messages.Where(message => EF.Functions.JsonExists(message.MetadataJson!, PythonDelegationKey))
            : messages.Where(message => message.MetadataJson!.Contains("\"" + PythonDelegationKey + "\":"));
        return python.Select(message => message.Id);
    }

    private Task<string?> ReadMetadataAsync(ResearchJob job, CancellationToken ct) => job.Id != job.UserMessageId
        ? Task.FromResult<string?>(null)
        : db.ChatMessages.AsNoTracking().Where(message => message.Id == job.UserMessageId)
            .Select(message => message.MetadataJson).SingleOrDefaultAsync(ct);

    private static (string? Brief, Guid? VoiceSessionId, bool IsPython) ReadDelegation(string? metadata)
    {
        if (metadata is null) return default;
        using var document = JsonDocument.Parse(metadata);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return default;
        var isPython = root.TryGetProperty(PythonDelegationKey, out var delegation);
        if ((!isPython && !root.TryGetProperty(DelegationKey, out delegation)) || delegation.ValueKind != JsonValueKind.Object ||
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
        return (text, session, isPython);
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

    internal async Task CompleteAsync(ResearchJobInput input, ResearchJobResult result, CancellationToken ct)
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
            job.Fail(delegation.IsPython ? "python_interrupted" : "research_interrupted", now);
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
        ReadDelegation(metadata).IsPython ? "python" : null);
}

public sealed class ResearchJobConflictException : Exception { }
