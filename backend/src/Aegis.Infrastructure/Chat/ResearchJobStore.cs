using Aegis.Application.Chat;
using Aegis.Application.Llm;
using Aegis.Application.Models;
using Aegis.Domain;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Text.Json;

namespace Aegis.Infrastructure.Chat;

public sealed class ResearchJobStore(AegisDbContext db, TimeProvider clock)
{
    public const int MaximumRequestLength = 12_000;
    internal const int MaximumSources = 25;
    internal const int MaximumSourceUrlLength = 2_048;
    private const int MaximumRecentJobs = 20;
    private static readonly TimeSpan JobTimeout = TimeSpan.FromMinutes(3);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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

        var priorContent = await db.ChatMessages.Where(message => message.Id == job.UserMessageId)
            .Select(message => message.Content).SingleAsync(ct);
        if ((request.ConversationId is { } id && id != job.ConversationId) || priorContent != content)
            throw new ResearchJobConflictException();
        return Snapshot(job);
    }

    public async Task<ResearchJobSnapshot?> GetAsync(Guid id, CancellationToken ct)
    {
        var job = await VisibleJobs().AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, ct);
        return job is null ? null : Snapshot(job);
    }

    public async Task<IReadOnlyList<ResearchJobSnapshot>> ListAsync(Guid conversationId, CancellationToken ct)
    {
        var jobs = await VisibleJobs().AsNoTracking().Where(job => job.ConversationId == conversationId)
            .OrderByDescending(job => job.State == ResearchJob.Queued || job.State == ResearchJob.Running)
            .ThenByDescending(job => job.CreatedAt).ThenByDescending(job => job.Id)
            .Take(MaximumRecentJobs).ToListAsync(ct);
        return jobs.Select(Snapshot).ToArray();
    }

    public async Task<ResearchJobSnapshot?> CancelAsync(Guid id, CancellationToken ct)
    {
        while (true)
        {
            var job = await VisibleJobs().SingleOrDefaultAsync(item => item.Id == id, ct);
            if (job is null) return null;
            if (job.State is not (ResearchJob.Queued or ResearchJob.Running)) return Snapshot(job);
            job.Cancel(clock.GetUtcNow());
            if (await SaveTransitionAsync(ct)) return Snapshot(job);
        }
    }

    internal async Task<ResearchJobInput?> ClaimAsync(CancellationToken ct)
    {
        await ExpireStaleJobsAsync(ct);
        var job = await VisibleJobs().Where(item => item.State == ResearchJob.Queued)
            .OrderBy(item => item.CreatedAt).ThenBy(item => item.Id).FirstOrDefaultAsync(ct);
        if (job is null) return null;
        var content = await db.ChatMessages.Where(message => message.Id == job.UserMessageId)
            .Select(message => message.Content).SingleAsync(ct);
        job.Start(clock.GetUtcNow(), JobTimeout);
        return await SaveTransitionAsync(ct)
            ? new ResearchJobInput(job.Id, job.ConversationId, job.UserMessageId, content, job.DeadlineAt!.Value)
            : null;
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
            job.Fail("research_interrupted", now);
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

    private static ResearchJobSnapshot Snapshot(ResearchJob job) => new(
        job.Id, job.ConversationId, job.UserMessageId, job.State, job.Phase, job.CreatedAt, job.UpdatedAt,
        job.ResultMessageId, job.ErrorCode, JsonSerializer.Deserialize<WebResearchSourceCount[]>(job.SourcesJson, JsonOptions) ?? []);
}

public sealed class ResearchJobConflictException : Exception { }
