using Aegis.Application.Chat;
using Aegis.Application.Models;
using Aegis.Application.Voice;
using Aegis.Infrastructure.Chat;
using Microsoft.AspNetCore.Mvc;

namespace Aegis.Api.Controllers;

[ApiController]
[Route("api/chat/research-jobs")]
public sealed class ResearchJobsController(ResearchJobStore store, WebSearchOptions options) : ControllerBase
{
    public sealed record ResearchVoiceRequest(Guid VoiceSessionId);

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

    [HttpPost("{jobId:guid}/speech")]
    [RequestSizeLimit(1024)]
    public async Task Speak(Guid jobId, ResearchVoiceRequest request, [FromServices] IAegisSpeechClient speech, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, HttpContext.RequestAborted);
        var result = await store.GetVoiceResultAsync(jobId, request.VoiceSessionId, lifetime.Token);
        if (result is null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (result.Text is null)
        {
            Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        // Follow-up speech has its own request lifetime. Opening it must not replace a
        // regular turn or cancel speech from another device in the same conversation.
        var requestId = Guid.NewGuid();
        try
        {
            foreach (var unit in FinalSpeechSegments.SplitText(result.Text))
                await WriteSpeechUnit(jobId, requestId, result.ConversationId, unit, speech, lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            if (Response.HasStarted) HttpContext.Abort();
        }
        catch (TimeoutException) { FailSpeech(StatusCodes.Status504GatewayTimeout); }
        catch (HttpRequestException) { FailSpeech(StatusCodes.Status503ServiceUnavailable); }
        catch (InvalidOperationException) { FailSpeech(StatusCodes.Status503ServiceUnavailable); }
    }

    private async Task WriteSpeechUnit(Guid jobId, Guid requestId, Guid conversationId, string text,
        IAegisSpeechClient speech, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var nativeId = NativeSpeechRequestId.Create();
        var completed = false;
        try
        {
            await using var upstream = await speech.StreamSpeechAsync(
                new SpeechRequest(jobId, Guid.NewGuid(), nativeId, text, conversationId), ct);
            var format = upstream.Format;
            if (!Response.HasStarted)
            {
                Response.ContentType = "application/octet-stream";
                Response.Headers["X-Accel-Buffering"] = "no";
                Response.Headers["X-Aegis-Speech-Request-Id"] = requestId.ToString();
                Response.Headers["X-Aegis-Audio-Format"] = format.Format;
                Response.Headers["X-Aegis-Sample-Rate"] = format.SampleRate.ToString();
                Response.Headers["X-Aegis-Channels"] = format.Channels.ToString();
                Response.Headers["X-Aegis-Voice-Profile"] = format.VoiceProfile;
            }
            var buffer = new byte[16 * 1024];
            var bytes = 0L;
            while (true)
            {
                var read = await upstream.Stream.ReadAsync(buffer, ct);
                if (read == 0) break;
                bytes += read;
                if (bytes > 24_000L * 2 * 180) throw new HttpRequestException("Excessive PCM response for one unit.");
                await Response.Body.WriteAsync(buffer.AsMemory(0, read), ct);
                await Response.Body.FlushAsync(ct);
            }
            if (bytes == 0 || bytes % 2 != 0) throw new HttpRequestException("Incomplete PCM response.");
            completed = true;
        }
        finally
        {
            if (!completed) await speech.CancelAsync(nativeId, CancellationToken.None);
        }
    }

    private void FailSpeech(int status)
    {
        if (Response.HasStarted) HttpContext.Abort();
        else Response.StatusCode = status;
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
