using System.Security.Claims;
using Aegis.Api.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Aegis.Api.Controllers;

[ApiController, Route("api/nodes/me/wake-samples"), Authorize(Policy = "NodeManagement")]
[ServiceFilter(typeof(NodeApiFilter)), EnableRateLimiting("node-wake-dataset"), RequestSizeLimit(1024 * 1024)]
public sealed class NodeWakeDatasetController(WakeDatasetStore store) : ControllerBase
{
    [HttpPut("{eventId:guid}")]
    public async Task<IActionResult> Put(Guid eventId, WakeSample sample, CancellationToken ct)
    {
        var nodeId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        await store.SaveAsync(nodeId, eventId, sample, ct);
        return NoContent();
    }
}
