using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using Aegis.Api.Nodes;
using Aegis.Application.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Aegis.Api.Controllers;

[ApiController, Route("api/nodes/me/voice-activations"), Authorize(Policy = "NodeManagement")]
[ServiceFilter(typeof(NodeApiFilter)), EnableRateLimiting("node-voice-activation"), RequestSizeLimit(1024)]
public sealed class NodeVoiceActivationController(VoiceActivationArbiter arbiter) : ControllerBase
{
    public const string DetectionAgeHeader = "X-Aegis-Detection-Age-Ms";
    public const string ElectionWaitHeader = "X-Aegis-Election-Wait-Ms";
    public const string NodeAuthHeader = "X-Aegis-Node-Auth-Ms";

    private Guid NodeId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpPost("claim")]
    public VoiceActivationDecision Claim(VoiceActivationClaim request, CancellationToken ct)
    {
        int? detectionAgeMs = null;
        if (Request.Headers.TryGetValue(DetectionAgeHeader, out var values))
        {
            if (values.Count != 1 || !int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var age)
                || age is < 0 or > VoiceActivationArbiter.MaximumDetectionAgeMs)
                throw new NodeException("invalid_voice_activation", "Os dados da ativação de voz são inválidos.");
            detectionAgeMs = age;
        }
        var started = Stopwatch.GetTimestamp();
        var decision = arbiter.Claim(NodeId, request, ct, detectionAgeMs);
        Response.Headers[ElectionWaitHeader] = ((long)Stopwatch.GetElapsedTime(started).TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
        if (HttpContext.Items.TryGetValue(NodeAuthenticationHandler.VoiceAuthenticationTimingKey, out var auth) && auth is long authMs)
            SetOptionalTiming(NodeAuthHeader, authMs);
        return decision;
    }

    // Do not report impossible times or synthesize a bounded value from them.
    // These measurements are diagnostic only and never affect the election.
    private void SetOptionalTiming(string header, long ms)
    {
        if (ms is >= 0 and <= VoiceActivationArbiter.MaximumDetectionAgeMs)
            Response.Headers[header] = ms.ToString(CultureInfo.InvariantCulture);
    }

    [HttpDelete("{activationId:guid}")]
    public IActionResult Abandon(Guid activationId)
    {
        arbiter.Abandon(NodeId, activationId);
        return NoContent();
    }

}
