using Aegis.Application.Chat;
using Aegis.Application.Llm;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aegis.Api.Controllers;

[ApiController]
[Route("api/chat")]
public sealed class ChatController(IChatService chatService) : ControllerBase
{
    private static readonly JsonSerializerOptions StreamJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

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

    [HttpPost("messages/stream")]
    public async Task StreamMessage(
        [FromBody] SendMessageRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Content))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsJsonAsync(
                new { error = "Message content cannot be empty." },
                cancellationToken);
            return;
        }

        Response.ContentType = "application/x-ndjson";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Append("X-Accel-Buffering", "no");

        try
        {
            await foreach (var streamEvent in chatService.StreamMessageAsync(request, cancellationToken))
            {
                await WriteStreamEventAsync(streamEvent, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The client disconnected; there is no stream left to notify.
        }
        catch (ConversationNotFoundException exception)
        {
            await WriteStreamErrorAsync(exception.Message, StatusCodes.Status404NotFound, cancellationToken);
        }
        catch (LlmRequestException exception)
        {
            await WriteStreamErrorAsync(exception.Message, StatusCodes.Status502BadGateway, cancellationToken);
        }
        catch (Exception exception)
        {
            await WriteStreamErrorAsync(exception.Message, StatusCodes.Status500InternalServerError, cancellationToken);
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
    public async Task<ActionResult<ConversationPageResponse>> GetRecentConversations(
        [FromQuery] int limit = 30,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var conversations = await chatService.GetRecentConversationsAsync(limit, cursor, cancellationToken);
            return Ok(conversations);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    [HttpPatch("conversations/{conversationId:guid}/title")]
    public async Task<ActionResult<ConversationSummaryResponse>> RenameConversation(
        Guid conversationId,
        [FromBody] RenameConversationTitleRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new { error = "Title is required." });
        }

        try
        {
            var conversation = await chatService.RenameConversationAsync(
                conversationId,
                request.Title,
                cancellationToken);
            if (conversation is null)
            {
                return NotFound(new { error = $"Conversation '{conversationId}' was not found." });
            }

            return Ok(conversation);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    [HttpDelete("conversations/{conversationId:guid}")]
    public async Task<IActionResult> DeleteConversation(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var deleted = await chatService.DeleteConversationAsync(conversationId, cancellationToken);
        if (!deleted)
        {
            return NotFound(new { error = $"Conversation '{conversationId}' was not found." });
        }

        return NoContent();
    }

    private async Task WriteStreamEventAsync(object streamEvent, CancellationToken cancellationToken)
    {
        await Response.WriteAsync(
            JsonSerializer.Serialize(streamEvent, StreamJsonOptions) + "\n",
            cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }

    private async Task WriteStreamErrorAsync(
        string message,
        int statusCode,
        CancellationToken cancellationToken)
    {
        if (!Response.HasStarted)
        {
            Response.StatusCode = statusCode;
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            await WriteStreamEventAsync(new { type = "error", message }, cancellationToken);
        }
    }
}
