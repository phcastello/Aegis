using System.Globalization;
using System.Security.Claims;
using Aegis.Api.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Aegis.Api.Controllers;

[ApiController, Route("api/nodes/me/voice-activations"), Authorize(Policy = "NodeManagement")]
[ServiceFilter(typeof(NodeApiFilter)), EnableRateLimiting("node-voice-activation"), RequestSizeLimit(1024)]
public sealed class NodeVoiceActivationController(VoiceActivationArbiter arbiter) : ControllerBase
{
    private Guid NodeId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpPost("claim")]
    public Task<VoiceActivationDecision> Claim(VoiceActivationClaim request, CancellationToken ct)
    {
        var priority = int.Parse(User.FindFirstValue(NodeAuthenticationHandler.TargetPriorityClaim)!, CultureInfo.InvariantCulture);
        return arbiter.ClaimAsync(NodeId, request, priority, ct);
    }

    [HttpDelete("{activationId:guid}")]
    public IActionResult Abandon(Guid activationId)
    {
        arbiter.Abandon(NodeId, activationId);
        return NoContent();
    }
}
