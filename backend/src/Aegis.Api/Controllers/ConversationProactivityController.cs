using Aegis.Application.Chat;
using Microsoft.AspNetCore.Mvc;
namespace Aegis.Api.Controllers;
// Same trusted single-owner chat boundary as ChatController; no Node credentials exposed.
[ApiController]
[Route("api/chat")]
public sealed class ConversationProactivityController(IConversationProactivity service) : ControllerBase
{
    [HttpGet("proactive-activity")]
    public async Task<ActionResult<IReadOnlyList<ConversationProactiveActivity>>> Activity(CancellationToken ct) => Ok(await service.ActivityAsync(ct));
    [HttpGet("conversations/{id:guid}/proactive-messages")]
    public async Task<ActionResult<IReadOnlyList<ChatMessageResponse>>> Messages(Guid id, [FromQuery] long after = 0, CancellationToken ct = default) {
        if (after < 0) return BadRequest();
        var messages = await service.MessagesAsync(id, after, ct); return messages is null ? NotFound() : Ok(messages);
    }
    public sealed record ReadRequest(long Sequence);
    [HttpPost("conversations/{id:guid}/proactive-read")]
    public async Task<IActionResult> Read(Guid id, ReadRequest request, CancellationToken ct) {
        if (request.Sequence < 0) return BadRequest();
        return await service.MarkReadAsync(id, request.Sequence, ct) ? NoContent() : NotFound();
    }
}
