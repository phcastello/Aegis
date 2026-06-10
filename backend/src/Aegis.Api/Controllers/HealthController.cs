using Microsoft.AspNetCore.Mvc;

namespace Aegis.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class HealthController : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        return Ok(new
        {
            status = "ok",
            service = "Aegis.Api",
            version = "0.1.3",
            codename = "Finally, It’s Raining!"
        });
    }
}
