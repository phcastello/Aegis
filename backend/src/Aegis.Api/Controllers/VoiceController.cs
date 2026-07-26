using Aegis.Application.Voice;
using Microsoft.AspNetCore.Mvc;

namespace Aegis.Api.Controllers;

[ApiController]
[Route("api/voice")]
public sealed class VoiceController(IVoiceService voiceService) : ControllerBase
{
    [HttpGet("status")]
    public async Task<ActionResult<SpeechServiceStatus>> GetStatus(CancellationToken cancellationToken)
    {
        return Ok(await voiceService.GetStatusAsync(cancellationToken));
    }

    [HttpPost("speech")]
    public async Task Speech([FromBody] StartSpeechRequest? request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        try
        {
            await using var stream = await voiceService.StartSpeechAsync(request, cancellationToken);
            var format = stream.Upstream.Format;
            Response.ContentType = "application/octet-stream";
            Response.Headers.Append("X-Aegis-Speech-Request-Id", stream.SpeechRequestId.ToString());
            Response.Headers.Append("X-Aegis-Audio-Format", format.Format);
            Response.Headers.Append("X-Aegis-Sample-Rate", format.SampleRate.ToString());
            Response.Headers.Append("X-Aegis-Channels", format.Channels.ToString());
            Response.Headers.Append("X-Aegis-Voice-Profile", format.VoiceProfile);
            Response.Headers.CacheControl = "no-store";

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, HttpContext.RequestAborted);
            var completed = false;
            try
            {
                await stream.Upstream.Stream.CopyToAsync(Response.Body, 64 * 1024, linked.Token);
                completed = true;
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                // Client disconnect and explicit turn cancellation are both expected.
            }
            finally
            {
                if (completed)
                {
                    voiceService.CompleteSpeech(stream.SpeechRequestId);
                }
                else
                {
                    await voiceService.CancelSpeechAsync(stream.SpeechRequestId, CancellationToken.None);
                }
            }
        }
        catch (KeyNotFoundException)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
        }
        catch (InvalidOperationException)
        {
            Response.StatusCode = StatusCodes.Status409Conflict;
        }
        catch (HttpRequestException)
        {
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        }
        catch (OperationCanceledException)
        {
            // Do not turn intentional cancellation into a textual or audio error.
        }
    }

    [HttpDelete("speech/{speechRequestId:guid}")]
    public async Task<IActionResult> CancelSpeech(Guid speechRequestId, CancellationToken cancellationToken)
    {
        var result = await voiceService.CancelSpeechAsync(speechRequestId, cancellationToken);
        return Ok(result);
    }
}
