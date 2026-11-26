using Aegis.Application.Chat;
using Aegis.Application.Models;
using Aegis.Infrastructure.Chat;
using Microsoft.AspNetCore.Mvc;

namespace Aegis.Api.Controllers;

[ApiController]
[Route("api/chat/research-jobs")]
public sealed class ResearchJobsController(ResearchJobStore store, WebSearchOptions options) : ControllerBase
{
    [HttpPost]
    [RequestSizeLimit(128_000)]
    public async Task<ActionResult<ResearchJobSnapshot>> Create(CreateResearchJobRequest request, CancellationToken ct)
    {
        if (!options.Enabled)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { code = "web_search_disabled" });
        try
        {
            var job = await store.CreateAsync(request, ct);
            return AcceptedAtAction(nameof(Get), new { jobId = job.Id }, job);
        }
        catch (ArgumentException e) { return BadRequest(new { code = "invalid_research_request", error = e.Message }); }
        catch (ConversationNotFoundException) { return NotFound(new { code = "conversation_not_found" }); }
        catch (ResearchJobConflictException) { return Conflict(new { code = "research_job_conflict" }); }
    }

    [HttpGet("{jobId:guid}")]
    public async Task<ActionResult<ResearchJobSnapshot>> Get(Guid jobId, CancellationToken ct)
    {
        var job = await store.GetAsync(jobId, ct);
        return job is null ? NotFound() : Ok(job);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ResearchJobSnapshot>>> List([FromQuery] Guid conversationId, CancellationToken ct)
    {
        if (conversationId == Guid.Empty) return BadRequest(new { code = "conversation_required" });
        return Ok(await store.ListAsync(conversationId, ct));
    }

    [HttpDelete("{jobId:guid}")]
    public async Task<ActionResult<ResearchJobSnapshot>> Cancel(Guid jobId, CancellationToken ct)
    {
        var job = await store.CancelAsync(jobId, ct);
        return job is null ? NotFound() : Ok(job);
    }
}
