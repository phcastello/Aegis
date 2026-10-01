using Aegis.Application.Memory;
using Aegis.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Aegis.Api.Controllers;

// Development and acceptance only. This route is not registered as a model tool.
[ApiController]
[Route("api/memory/diagnostics")]
public sealed class MemoryDiagnosticsController(AegisDbContext db, MemoryHybridRetriever retrieval,
    IConfiguration configuration) : ControllerBase
{
    private bool Enabled => configuration.GetValue<bool>("AEGIS_MEMORY_DIAGNOSTICS_ENABLED");
    private static string Safe(string value) => MemorySecretGuard.ContainsSecret(value) ? "[REDACTED]" : value;

    public sealed record RetrievalRequest(string Query, DateTimeOffset? AsOf = null, int Limit = 10,
        bool Automatic = false);

    [HttpGet("environment")]
    public async Task<IActionResult> EnvironmentState(CancellationToken ct)
    {
        if (!Enabled) return NotFound();
        return Ok(new
        {
            isolated = configuration.GetValue<bool>("AEGIS_MEMORY_EVAL_ISOLATED"),
            database = db.Database.GetDbConnection().Database,
            qdrantCollection = configuration["AEGIS_MEMORY_QDRANT_COLLECTION"] ?? "aegis_memory_semantic_large_v1",
            neo4jDatabase = configuration["AEGIS_MEMORY_NEO4J_DATABASE"] ?? "neo4j",
            memoryCount = await db.MemoryRecords.AsNoTracking().CountAsync(ct),
            extractionJobCount = await db.MemoryExtractionJobs.AsNoTracking().CountAsync(ct)
        });
    }

    [HttpPost("retrieval")]
    public async Task<IActionResult> Retrieval([FromBody] RetrievalRequest? request, CancellationToken ct)
    {
        if (!Enabled) return NotFound();
        if (request is null || string.IsNullOrWhiteSpace(request.Query) || request.Query.Length > 2000 ||
            request.Limit is < 1 or > 50 || MemorySecretGuard.ContainsSecret(request.Query)) return BadRequest();
        var result = await retrieval.SearchTracedAsync(request.Query, request.Limit, request.AsOf,
            request.Automatic, ct);
        var trace = result.Trace!;
        return Ok(new
        {
            semantic = new { threshold = trace.Semantic.Threshold, mode = trace.Semantic.Mode,
                fallbackReason = trace.Semantic.FallbackReason,
                candidates = trace.Semantic.Semantic },
            lexical = new { candidates = trace.Semantic.Lexical },
            entities = new { exactMentions = trace.ExactMentions.Select(x => new
                { x.Id, name = Safe(x.CanonicalName), x.EntityType }) },
            graph = new
            {
                strongSeeds = trace.ExactMentions.Select(x => x.Id),
                evidenceSeeds = trace.EvidenceAnchors.SelectMany(x => new[] { x.Subject.Id, x.Object.Id }).Distinct(),
                anchorRelations = trace.EvidenceAnchors.Select(x => new { x.Relation.Id, x.Relation.Predicate,
                    subject = Safe(x.Subject.CanonicalName), @object = Safe(x.Object.CanonicalName) }),
                traversalSeeds = trace.TraversalSeeds,
                traversalPaths = trace.TraversalPaths.Select(x => new
                {
                    entities = x.Entities.Select(e => new { e.Id, name = Safe(e.CanonicalName) }),
                    relations = x.Relations.Select(r => new { r.Id, r.Predicate })
                }),
                supportingMemoryIds = trace.SupportingMemoryIds,
                error = trace.GraphError
            },
            final = new { mode = result.Mode,
                memories = result.Memories.Select(x => new { x.Id, content = Safe(x.Content), status = x.Status.ToString(),
                    x.ValidFrom, x.ValidUntil }),
                corrections = (result.Corrections ?? []).Select(x => new
                    { incorrectStatement = Safe(x.IncorrectContent), correctedStatement = Safe(x.ReplacementContent) }),
                relations = result.Paths.SelectMany(x => x.Relations).DistinctBy(x => x.Id).Select(x => new
                    { x.Id, x.Predicate }) }
        });
    }

    [HttpGet("messages/{messageId:guid}")]
    public async Task<IActionResult> Message(Guid messageId, CancellationToken ct)
    {
        if (!Enabled) return NotFound();
        var source = await db.ChatMessages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == messageId, ct);
        if (source is null) return NotFound();
        if (source.Role == "assistant")
        {
            var userId = await db.LlmRequestAudits.AsNoTracking()
                .Where(x => x.AssistantMessageId == messageId && x.Success)
                .Select(x => (Guid?)x.UserMessageId).FirstOrDefaultAsync(ct);
            if (userId is null) return NotFound();
            source = await db.ChatMessages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, ct);
            if (source is null) return NotFound();
        }
        if (source.Role != "user") return NotFound();
        var job = await db.MemoryExtractionJobs.AsNoTracking().SingleOrDefaultAsync(x => x.UserMessageId == source.Id, ct);
        var priorFailures = await (from earlier in db.ChatMessages.AsNoTracking()
            join prior in db.MemoryExtractionJobs.AsNoTracking() on earlier.Id equals prior.UserMessageId
            where earlier.ConversationId == source.ConversationId && earlier.CreatedAt <= source.CreatedAt &&
                prior.Status == Aegis.Domain.Entities.MemoryExtractionStatus.Failed
            select new { earlier.Id, earlier.CreatedAt, prior.LastError }).ToListAsync(ct);
        var priorFailure = priorFailures.Where(x => x.CreatedAt < source.CreatedAt ||
                x.CreatedAt == source.CreatedAt && x.Id.CompareTo(source.Id) < 0)
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).FirstOrDefault();
        var memoryIds = await db.MemoryEvidences.AsNoTracking().Where(x => x.SourceMessageId == source.Id)
            .Select(x => x.MemoryId).Distinct().ToArrayAsync(ct);
        var memories = await db.MemoryRecords.AsNoTracking().Where(x => memoryIds.Contains(x.Id)).ToListAsync(ct);
        var relationIds = await db.MemoryRelationEvidences.AsNoTracking().Where(x => memoryIds.Contains(x.MemoryId))
            .Select(x => x.RelationId).Distinct().ToArrayAsync(ct);
        var relations = await db.MemoryRelations.AsNoTracking().Where(x => relationIds.Contains(x.Id)).ToListAsync(ct);
        var aggregateIds = memoryIds.Concat(relationIds).ToArray();
        var projections = await db.MemoryProjectionJobs.AsNoTracking()
            .Where(x => aggregateIds.Contains(x.AggregateId)).OrderBy(x => x.CreatedAt).ToListAsync(ct);
        var activity = await db.MemoryActivityEvents.AsNoTracking().Where(x => x.UserMessageId == source.Id)
            .OrderBy(x => x.OccurredAt).ToListAsync(ct);
        return Ok(new
        {
            source = new { conversationId = source.ConversationId, userMessageId = source.Id, source.CreatedAt },
            extractionJob = job is null ? null : new
            {
                job.Id, status = job.Status.ToString(), job.Attempt, candidates = job.CandidatesCount, created = job.CreatedCount,
                reinforced = job.ReinforcedCount, corrected = job.CorrectedCount,
                transitioned = job.TransitionedCount, graphMutations = job.GraphMutationsCount,
                skipped = job.SkippedCount,
                outcome = job.OutcomeJson is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(job.OutcomeJson),
                job.LastError, job.CreatedAt,
                job.ProcessingStartedAt, job.CompletedAt, priorTerminalFailure = priorFailure
            },
            memoriesFromThisMessage = memories.Select(x => new { x.Id, content = Safe(x.Content), status = x.Status.ToString(),
                x.ValidFrom, x.ValidUntil, x.Revision, x.SupersededById }),
            relationsFromTheseMemories = relations.Select(x => new { x.Id, x.SubjectEntityId,
                x.Predicate, x.ObjectEntityId, status = x.Status.ToString(), x.ValidFrom, x.ValidUntil, x.Revision }),
            projectionJobs = projections.Select(x => new { target = x.ProjectionTarget.ToString(),
                aggregateType = x.AggregateType.ToString(), x.AggregateId,
                revision = x.AggregateRevision, operation = x.Operation.ToString(), status = x.Status.ToString(), x.Attempt,
                x.LastError }),
            memoryActivity = activity.Select(x => new { kind = x.Kind.ToString(), source = x.Source.ToString(),
                targetType = x.TargetType.ToString(), x.TargetId,
                x.OccurredAt })
        });
    }

    [HttpGet("entities/aliases/{alias}")]
    public async Task<IActionResult> Alias(string alias, CancellationToken ct)
    {
        if (!Enabled) return NotFound();
        if (string.IsNullOrWhiteSpace(alias) || alias.Length > 200 || MemorySecretGuard.ContainsSecret(alias))
            return BadRequest();
        var normalized = Aegis.Domain.Entities.MemoryText.Normalize(alias, 200);
        var matches = await (from item in db.MemoryEntityAliases.AsNoTracking()
            join entity in db.MemoryEntities.AsNoTracking() on item.EntityId equals entity.Id
            where item.NormalizedAlias == normalized && entity.RetiredAt == null
            select new { entity.Id, entity.CanonicalName, entity.EntityType, item.Alias }).ToListAsync(ct);
        return Ok(matches.Select(x => new { x.Id, canonicalName = Safe(x.CanonicalName), x.EntityType,
            alias = Safe(x.Alias) }));
    }
}
