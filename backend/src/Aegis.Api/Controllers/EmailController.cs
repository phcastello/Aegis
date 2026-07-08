using Aegis.Application.Email;
using Aegis.Infrastructure.Email;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Aegis.Api.Controllers;

[ApiController]
[Route("api/email")]
public sealed class EmailController(
    IEmailConnectionService emailConnectionService,
    IOptions<GmailOptions> gmailOptions) : ControllerBase
{
    [HttpGet("status")]
    public async Task<ActionResult<EmailConnectionStatusResponse>> GetStatus(
        CancellationToken cancellationToken)
    {
        return Ok(await emailConnectionService.GetStatusAsync(cancellationToken));
    }

    [HttpGet("connect")]
    public async Task<ActionResult<EmailAuthorizationResponse>> Connect(
        [FromQuery] bool redirect = false,
        CancellationToken cancellationToken = default)
    {
        var response = await emailConnectionService.CreateAuthorizationUrlAsync(cancellationToken);
        if (redirect)
        {
            return Redirect(response.AuthorizationUrl);
        }

        return Ok(response);
    }

    [HttpGet("oauth/callback")]
    public async Task<IActionResult> OAuthCallback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        CancellationToken cancellationToken)
    {
        var options = gmailOptions.Value;
        if (!string.IsNullOrWhiteSpace(error) || string.IsNullOrWhiteSpace(code))
        {
            return Redirect(options.FailureRedirectPath);
        }

        try
        {
            await emailConnectionService.HandleOAuthCallbackAsync(code, state, cancellationToken);
            return Redirect(options.SuccessRedirectPath);
        }
        catch
        {
            return Redirect(options.FailureRedirectPath);
        }
    }

    [HttpPost("disconnect")]
    public async Task<IActionResult> Disconnect(CancellationToken cancellationToken)
    {
        await emailConnectionService.DisconnectAsync(cancellationToken);
        return NoContent();
    }
}
