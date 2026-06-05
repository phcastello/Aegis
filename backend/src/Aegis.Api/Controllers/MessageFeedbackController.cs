using Aegis.Application.Feedback;
using Microsoft.AspNetCore.Mvc;

namespace Aegis.Api.Controllers;

[ApiController]
[Route("api/chat")]
public sealed class MessageFeedbackController(IMessageFeedbackService feedbackService) : ControllerBase
{
    [HttpPost("messages/{messageId:guid}/feedback")]
    public async Task<ActionResult<MessageFeedbackResponse>> SubmitFeedback(
        Guid messageId,
        [FromBody] SubmitMessageFeedbackRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Rating))
        {
            return BadRequest(new { error = "Feedback rating is required." });
        }

        try
        {
            var response = await feedbackService.SubmitFeedbackAsync(messageId, request, cancellationToken);
            return CreatedAtAction(nameof(GetFeedbackDetail), new { feedbackId = response.Id }, response);
        }
        catch (MessageFeedbackTargetNotFoundException exception)
        {
            return NotFound(new { error = exception.Message });
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    [HttpGet("feedback/recent")]
    public async Task<ActionResult<IReadOnlyList<FeedbackSummaryResponse>>> GetRecentFeedback(
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var feedback = await feedbackService.GetRecentFeedbackAsync(limit, cancellationToken);
        return Ok(feedback);
    }

    [HttpGet("feedback/{feedbackId:guid}")]
    public async Task<ActionResult<FeedbackDetailResponse>> GetFeedbackDetail(
        Guid feedbackId,
        CancellationToken cancellationToken = default)
    {
        var feedback = await feedbackService.GetFeedbackDetailAsync(feedbackId, cancellationToken);
        if (feedback is null)
        {
            return NotFound(new { error = $"Feedback '{feedbackId}' was not found." });
        }

        return Ok(feedback);
    }
}
