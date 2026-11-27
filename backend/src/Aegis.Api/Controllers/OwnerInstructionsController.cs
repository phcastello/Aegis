using Aegis.Application.Prompts;
using Aegis.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Aegis.Api.Controllers;

[ApiController]
[Route("api/nodes/settings/custom-instructions")]
[Authorize(Policy = "NodeManagement")]
[RequestSizeLimit(16384)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class OwnerInstructionsController(IOwnerInstructions settings) : ControllerBase
{
    public sealed record SaveResult(bool Applied, InstructionSettings Settings);

    [HttpGet]
    public Task<InstructionSettings> Get(CancellationToken ct) => settings.ReadAsync(ct);

    [HttpPut]
    public async Task<ActionResult<SaveResult>> Put(InstructionSettings request, CancellationToken ct)
    {
        if (request.Text is null || request.Text.Length > OwnerPreferences.MaximumInstructionsLength || request.Text.Contains('\0'))
            return BadRequest(new { error = "Informe instruções válidas de até 2.000 caracteres." });
        var updated = await settings.UpdateAsync(request.Text, request.Revision, ct);
        return Ok(new SaveResult(updated is not null, updated ?? await settings.ReadAsync(ct)));
    }
}
