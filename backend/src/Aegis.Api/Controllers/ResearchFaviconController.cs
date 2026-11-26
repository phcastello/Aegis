using Aegis.Infrastructure.Chat;
using Microsoft.AspNetCore.Mvc;

namespace Aegis.Api.Controllers;

[ApiController]
[Route("api/chat/research-favicon")]
public sealed class ResearchFaviconController(ResearchFaviconDiscovery discovery) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string? host, CancellationToken ct)
    {
        var result = await discovery.FindAsync(host, ct);
        Response.Headers.XContentTypeOptions = "nosniff";
        if (result.Icon is { } icon)
        {
            Response.Headers.CacheControl = "public, max-age=43200";
            return File(icon.Bytes, icon.ContentType);
        }
        if (result.Status == ResearchFaviconDiscovery.Status.Missing)
        {
            Response.Headers.CacheControl = "public, max-age=900";
            return NotFound();
        }
        Response.Headers.CacheControl = "no-store";
        if (result.Status == ResearchFaviconDiscovery.Status.InvalidHost) return BadRequest();
        Response.Headers.RetryAfter = "5";
        return StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
}
