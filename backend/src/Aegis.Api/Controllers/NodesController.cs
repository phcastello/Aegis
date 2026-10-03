using System.Security.Claims;
using Aegis.Api.Nodes;
using Aegis.Application.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
namespace Aegis.Api.Controllers;

[ApiController, Route("api/nodes"), Authorize(Policy = "NodeManagement"), ServiceFilter(typeof(NodeApiFilter)), RequestSizeLimit(4096)]
public sealed class NodesController(INodeRegistry registry) : ControllerBase
{
    private Guid CurrentNode => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    public sealed record RenameRequest(string Name);
    [HttpGet] public async Task<IActionResult> List(CancellationToken ct) => Ok(await registry.ListAsync(CurrentNode, ct));
    [HttpGet("me")] public async Task<IActionResult> Me(CancellationToken ct) => Ok(await registry.MeAsync(CurrentNode, ct));
    [HttpPatch("{id:guid}")] public async Task<IActionResult> Rename(Guid id, RenameRequest request, CancellationToken ct) => Ok(await registry.RenameAsync(CurrentNode, id, request.Name, ct));
    [HttpPost("{id:guid}/disable")] public async Task<IActionResult> Disable(Guid id, CancellationToken ct) => Ok(await registry.SetEnabledAsync(CurrentNode, id, false, ct));
    [HttpPost("{id:guid}/enable")] public async Task<IActionResult> Enable(Guid id, CancellationToken ct) => Ok(await registry.SetEnabledAsync(CurrentNode, id, true, ct));
    [HttpPost("{id:guid}/revoke")] public async Task<IActionResult> Revoke(Guid id, CancellationToken ct) => Ok(await registry.RevokeAsync(CurrentNode, id, ct));
    [HttpPost("pairing-codes"), EnableRateLimiting("node-codes")]
    public async Task<IActionResult> CreateCode(CancellationToken ct) => Ok(await registry.CreateCodeAsync(CurrentNode, ct));
}

[ApiController, Route("api/nodes/pair"), AllowAnonymous, ServiceFilter(typeof(NodeApiFilter)), RequestSizeLimit(4096), EnableRateLimiting("node-pairing")]
public sealed class NodePairingController(INodeRegistry registry) : ControllerBase
{
    [HttpPost] public async Task<IActionResult> Pair(PairNodeRequest request, CancellationToken ct) => Ok(await registry.PairAsync(request, ct));
    [HttpPost("finalize")] public async Task<IActionResult> FinalizePair(FinalizeNodeRequest request, CancellationToken ct) => Ok(await registry.FinalizeAsync(request, ct));
}
