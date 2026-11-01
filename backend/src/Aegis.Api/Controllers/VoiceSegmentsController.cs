using Aegis.Application.Voice;
using Microsoft.AspNetCore.Mvc;

namespace Aegis.Api.Controllers;

[ApiController]
[Route("api/voice/speech/segments")]
public sealed class VoiceSegmentsController(IVoiceService voice) : ControllerBase
{
    [HttpPost]
    public async Task Speech([FromBody] StartSegmentSpeechRequest request, CancellationToken cancellationToken)
    {
        var completed = false;
        var acquired = false;
        try
        {
            await using var stream = await voice.StartSegmentSpeechAsync(request, cancellationToken);
            acquired = true;
            var format = stream.Upstream.Format;
            Response.ContentType = "application/octet-stream";
            Response.Headers.CacheControl = "no-store";
            Response.Headers["X-Accel-Buffering"] = "no";
            Response.Headers["X-Aegis-Speech-Request-Id"] = request.SpeechRequestId.ToString();
            Response.Headers["X-Aegis-Audio-Format"] = format.Format;
            Response.Headers["X-Aegis-Sample-Rate"] = format.SampleRate.ToString();
            Response.Headers["X-Aegis-Channels"] = format.Channels.ToString();
            Response.Headers["X-Aegis-Voice-Profile"] = format.VoiceProfile;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stream.TurnCancellationToken);
            var bytes = 0L;
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var count = await stream.Upstream.Stream.ReadAsync(buffer, linked.Token);
                if (count == 0) break;
                bytes += count;
                if (bytes > 24_000L * 2 * 180) throw new InvalidOperationException("Audio duration exceeded.");
                await Response.Body.WriteAsync(buffer.AsMemory(0, count), linked.Token);
                await Response.Body.FlushAsync(linked.Token);
            }
            if (bytes == 0 || bytes % 2 != 0) throw new HttpRequestException("Incomplete PCM response.");
            voice.CompleteSpeech(request.SpeechRequestId);
            completed = true;
        }
        catch (ArgumentException) { if (!Response.HasStarted) Response.StatusCode = 400; else HttpContext.Abort(); }
        catch (KeyNotFoundException) { if (!Response.HasStarted) Response.StatusCode = 404; else HttpContext.Abort(); }
        catch (InvalidOperationException) { if (!Response.HasStarted) Response.StatusCode = 409; else HttpContext.Abort(); }
        catch (HttpRequestException) { if (!Response.HasStarted) Response.StatusCode = 503; else HttpContext.Abort(); }
        catch (TimeoutException) { if (!Response.HasStarted) Response.StatusCode = 504; else HttpContext.Abort(); }
        catch (OperationCanceledException) { if (Response.HasStarted) HttpContext.Abort(); }
        finally
        {
            if (acquired && !completed)
                await voice.CancelSpeechAsync(request.SpeechRequestId, CancellationToken.None);
        }
    }
}
