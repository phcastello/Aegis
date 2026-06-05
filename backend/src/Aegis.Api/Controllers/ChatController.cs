using Aegis.Application.Chat;
using Aegis.Application.Llm;
using Microsoft.AspNetCore.Mvc;

namespace Aegis.Api.Controllers;

[ApiController]
[Route("api/chat")]
public sealed class ChatController(IChatService chatService) : ControllerBase
{
    [HttpPost("messages")]
    public async Task<ActionResult<SendMessageResponse>> SendMessage(
        [FromBody] SendMessageRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Content))
        {
            return BadRequest(new { error = "Message content cannot be empty." });
        }

        try
        {
            var response = await chatService.SendMessageAsync(request, cancellationToken);
            return Ok(response);
        }
        catch (ConversationNotFoundException exception)
        {
            return NotFound(new { error = exception.Message });
        }
        catch (LlmRequestException exception)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                error = exception.Message,
                reason = exception.AuditData.FailureReason,
                durationMilliseconds = exception.AuditData.DurationMilliseconds,
                ollamaStatusCode = exception.AuditData.HttpStatusCode
            });
        }
    }

    [HttpGet("conversations/{conversationId:guid}")]
    public async Task<ActionResult<ConversationResponse>> GetConversation(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var conversation = await chatService.GetConversationAsync(conversationId, cancellationToken);
        if (conversation is null)
        {
            return NotFound(new { error = $"Conversation '{conversationId}' was not found." });
        }

        return Ok(conversation);
    }

    [HttpGet("conversations")]
    public async Task<ActionResult<IReadOnlyList<ConversationSummaryResponse>>> GetRecentConversations(
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var conversations = await chatService.GetRecentConversationsAsync(limit, cancellationToken);
        return Ok(conversations);
    }
}
