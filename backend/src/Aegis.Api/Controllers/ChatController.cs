using Aegis.Application.Chat;
using Aegis.Application.Llm;
using Aegis.Application.Turns;
using Aegis.Application.Voice;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aegis.Api.Controllers;

[ApiController]
[Route("api/chat")]
public sealed class ChatController(IChatService chatService, IVoiceService voiceService, ILogger<ChatController> logger) : ControllerBase
{
    private const string FriendlyFailureMessage = "Tive um problema para responder agora. Tenta de novo em alguns segundos.";

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
            logger.LogWarning(exception, "Chat model request failed.");
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                code = "model_unavailable",
                error = exception.Message
            });
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unexpected chat request failure.");
            return StatusCode(StatusCodes.Status500InternalServerError, new { code = "chat_failed", error = FriendlyFailureMessage });
        }
    }

    [HttpPost("messages/stream")]
    public async Task StreamMessage(
        [FromBody] SendMessageRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Content) || request.TurnId is not { } turnId || turnId == Guid.Empty)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsJsonAsync(
                new { error = "Message content and a valid turnId are required." },
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
        catch (OperationCanceledException)
        {
            // The client disconnected; there is no stream left to notify.
        }
        catch (ConversationNotFoundException exception)
        {
            await WriteStreamErrorAsync(exception.Message, "conversation_not_found", StatusCodes.Status404NotFound, cancellationToken);
        }
        catch (LlmRequestException exception)
        {
            logger.LogWarning(exception, "Chat stream model request failed.");
            await WriteStreamErrorAsync(exception.Message, "model_unavailable", StatusCodes.Status502BadGateway, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unexpected chat stream failure.");
            await WriteStreamErrorAsync(FriendlyFailureMessage, "chat_failed", StatusCodes.Status500InternalServerError, cancellationToken);
        }
    }

    [HttpDelete("turns/{turnId:guid}")]
    public async Task<ActionResult<CancelTurnResult>> CancelTurn(
        Guid turnId,
        CancellationToken cancellationToken)
    {
        var result = await voiceService.CancelTurnAsync(turnId, "user_stop", cancellationToken);
        return Ok(result);
    }

    [HttpPost("turns/{turnId:guid}/complete")]
    public IActionResult CompleteTurnWithoutSpeech(Guid turnId)
    {
        return voiceService.TryCompleteTurnWithoutSpeech(turnId)
            ? NoContent()
            : Conflict();
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
        string code,
        int statusCode,
        CancellationToken cancellationToken)
    {
        if (!Response.HasStarted)
        {
            Response.StatusCode = statusCode;
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            await WriteStreamEventAsync(new { type = "error", code, message }, cancellationToken);
        }
    }
}
