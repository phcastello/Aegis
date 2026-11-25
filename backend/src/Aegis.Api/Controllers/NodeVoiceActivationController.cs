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
public sealed class NodeVoiceActivationController(VoiceActivationArbiter arbiter, INodeRegistry nodes) : ControllerBase
{
    public const string DetectionAgeHeader = "X-Aegis-Detection-Age-Ms";
    public const string ElectionWaitHeader = "X-Aegis-Election-Wait-Ms";

    private Guid NodeId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpPost("claim")]
    public async Task<VoiceActivationDecision> Claim(VoiceActivationClaim request, CancellationToken ct)
    {
        var priority = int.Parse(User.FindFirstValue(NodeAuthenticationHandler.TargetPriorityClaim)!, CultureInfo.InvariantCulture);
        int? detectionAgeMs = null;
        if (Request.Headers.TryGetValue(DetectionAgeHeader, out var values))
        {
            if (values.Count != 1 || !int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var age)
                || age is < 0 or > VoiceActivationArbiter.MaximumDetectionAgeMs)
                throw new NodeException("invalid_voice_activation", "Os dados da ativação de voz são inválidos.");
            detectionAgeMs = age;
        }
        var participants = await nodes.ListVoiceActivationParticipantsAsync(ct);
        var started = Stopwatch.GetTimestamp();
        var decision = await arbiter.ClaimAsync(NodeId, request, priority, ct, participants, detectionAgeMs);
        Response.Headers[ElectionWaitHeader] = ((long)Stopwatch.GetElapsedTime(started).TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
        return decision;
    }

    [HttpDelete("{activationId:guid}")]
    public IActionResult Abandon(Guid activationId)
    {
        arbiter.Abandon(NodeId, activationId);
        return NoContent();
    }
}
