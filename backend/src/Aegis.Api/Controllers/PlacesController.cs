using System.Security.Claims;
using Aegis.Api.Nodes;
using Aegis.Application.Places;
using Aegis.Infrastructure.Places;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Aegis.Api.Controllers;

[ApiController, Route("api/nodes/places"), Authorize(Policy = "NodeManagement"), ServiceFilter(typeof(NodeApiFilter)), RequestSizeLimit(65536)]
public sealed class PlacesController(IPlaceService places, PlaceMaps maps) : ControllerBase
{
    [HttpGet] public async Task<IActionResult> List(CancellationToken ct) => Ok(await places.ListAsync(ct));
    [HttpPost] public async Task<IActionResult> Create(PlaceWrite request, CancellationToken ct) => Ok(await places.SaveAsync(null, request, ct));
    [HttpPut("{id:guid}")] public async Task<IActionResult> Update(Guid id, PlaceWrite request, CancellationToken ct) => Ok(await places.SaveAsync(id, request, ct));
    [HttpDelete("{id:guid}")] public async Task<IActionResult> Delete(Guid id, [FromQuery] int expectedRevision, CancellationToken ct) { await places.DeleteAsync(id, expectedRevision, ct); return NoContent(); }
    [HttpGet("config")] public IActionResult MapConfig() => Ok(maps.Configuration());
    public sealed record SearchRequest(string Query);
    [HttpPost("search"), RequestSizeLimit(2048)]
    public async Task<IActionResult> Search([FromBody] SearchRequest request, CancellationToken ct) => Ok(await maps.SearchAsync(request.Query, ct));
}

[ApiController, Route("api/nodes/location"), Authorize(Policy = "NodeManagement"), ServiceFilter(typeof(NodeApiFilter)), RequestSizeLimit(4096)]
public sealed class LocationController(IPlaceService places) : ControllerBase
{
    private Guid Actor => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    [HttpGet("settings")] public async Task<IActionResult> Settings(CancellationToken ct) => Ok(await places.SettingsAsync(ct));
    [HttpPut("settings")] public async Task<IActionResult> Configure(LocationSettingsWrite request, CancellationToken ct) => Ok(await places.ConfigureAsync(request, ct));
    [HttpPost("observations")] public async Task<IActionResult> Observe(LocationObservationWrite request, CancellationToken ct) { await places.ObserveAsync(Actor, request, ct); return Ok(new { received = true }); }
    [HttpGet("current")] public async Task<IActionResult> Current(CancellationToken ct) { var settings = await places.SettingsAsync(ct); return Ok(new { sourceNodeId = settings.SourceNodeId, latest = settings.Latest }); }
    [HttpPost("current")] public async Task<IActionResult> Refresh(CancellationToken ct) => Ok(await places.RefreshAsync(ct));
}

[ApiController, Route("api/nodes/geofences"), Authorize(Policy = "NodeManagement"), ServiceFilter(typeof(NodeApiFilter)), RequestSizeLimit(65536)]
public sealed class GeofencesController(IPlaceService places) : ControllerBase
{
    private Guid Actor => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    public sealed record ActionRequest(int ExpectedRevision, string Action);
    [HttpGet] public async Task<IActionResult> List(CancellationToken ct) => Ok(await places.RulesAsync(ct));
    [HttpPost] public async Task<IActionResult> Create(GeographicRuleWrite request, CancellationToken ct) => Ok(await places.SaveRuleAsync(null, request, ct: ct));
    [HttpPut("{id:guid}")] public async Task<IActionResult> Update(Guid id, GeographicRuleWrite request, CancellationToken ct) => Ok(await places.SaveRuleAsync(id, request, ct: ct));
    [HttpPost("{id:guid}/actions")] public async Task<IActionResult> Action(Guid id, ActionRequest request, CancellationToken ct) => Ok(await places.ActOnRuleAsync(id, request.ExpectedRevision, request.Action, ct));
    [HttpGet("manifest")] public async Task<IActionResult> Manifest(CancellationToken ct) => Ok(await places.ManifestAsync(Actor, ct));
    [HttpPost("sync-request")] public async Task<IActionResult> RequestSync(CancellationToken ct) => Ok(await places.RequestGeofenceSyncAsync(ct));
    [HttpPost("sync")] public async Task<IActionResult> Confirm(GeographicSyncWrite request, CancellationToken ct) => Ok(await places.ConfirmAsync(Actor, request, ct));
    [HttpPost("events")] public async Task<IActionResult> Event(GeographicEventWrite request, CancellationToken ct) => Ok(await places.EventAsync(Actor, request, ct));
}
