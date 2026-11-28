using Aegis.Api.Nodes;
using Aegis.Application.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Aegis.Api.Controllers;

[ApiController, Route("api/nodes/settings/wake-dataset"), Authorize(Policy = "NodeManagement")]
[ServiceFilter(typeof(NodeApiFilter)), RequestSizeLimit(4096)]
public sealed class WakeDatasetReviewController(WakeDatasetStore store) : ControllerBase
{
    [HttpGet]
    public Task<WakeDatasetPage> List(Guid? nodeId, string? filter, int offset, CancellationToken ct) =>
        store.ReviewAsync(nodeId, filter, offset, ct);

    [HttpPut("{nodeId:guid}/{eventId:guid}/labels")]
    public Task<WakeDatasetEntry> Label(Guid nodeId, Guid eventId, WakeDatasetLabels labels, CancellationToken ct) =>
        store.LabelAsync(nodeId, eventId, labels, ct);

    [HttpGet("{nodeId:guid}/{eventId:guid}/audio/{clip}")]
    public async Task<IActionResult> Audio(Guid nodeId, Guid eventId, string clip, CancellationToken ct)
    {
        if (clip is not ("trigger" or "command"))
            throw new NodeException("invalid_wake_clip", "Selecione o trecho de ativação ou do comando.");
        var wav = await store.AudioAsync(nodeId, eventId, clip == "command", ct);
        return Ok(new { wav });
    }

    [HttpDelete("{nodeId:guid}/{eventId:guid}")]
    public async Task<IActionResult> Delete(Guid nodeId, Guid eventId, CancellationToken ct)
    {
        await store.DeleteAsync(nodeId, eventId, ct);
        return NoContent();
    }
}
