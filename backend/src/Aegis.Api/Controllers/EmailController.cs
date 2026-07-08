using Aegis.Application.Email;
using Aegis.Infrastructure.Email;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Text;

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
            var message = string.IsNullOrWhiteSpace(error)
                ? "Google OAuth did not return an authorization code."
                : $"Google OAuth returned error: {error}.";
            return Redirect(BuildFailureRedirectUri(options.FailureRedirectPath, "oauth_callback_error", message));
        }

        try
        {
            await emailConnectionService.HandleOAuthCallbackAsync(code, state, cancellationToken);
            return Redirect(options.SuccessRedirectPath);
        }
        catch (HttpRequestException exception)
        {
            return Redirect(BuildFailureRedirectUri(
                options.FailureRedirectPath,
                "google_http_error",
                exception.Message));
        }
        catch (InvalidOperationException exception)
        {
            return Redirect(BuildFailureRedirectUri(
                options.FailureRedirectPath,
                "oauth_invalid_operation",
                exception.Message));
        }
        catch (ArgumentException exception)
        {
            return Redirect(BuildFailureRedirectUri(
                options.FailureRedirectPath,
                "oauth_invalid_argument",
                exception.Message));
        }
        catch
        {
            return Redirect(BuildFailureRedirectUri(
                options.FailureRedirectPath,
                "oauth_unknown_error",
                "Unexpected error while finishing Gmail connection."));
        }
    }

    [HttpPost("disconnect")]
    public async Task<IActionResult> Disconnect(CancellationToken cancellationToken)
    {
        await emailConnectionService.DisconnectAsync(cancellationToken);
        return NoContent();
    }

    private static string BuildFailureRedirectUri(
        string failureRedirectPath,
        string code,
        string message)
    {
        var separator = failureRedirectPath.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var builder = new StringBuilder(failureRedirectPath);
        builder.Append(separator);
        builder.Append("email_error_code=");
        builder.Append(Uri.EscapeDataString(code));
        builder.Append("&email_error_message=");
        builder.Append(Uri.EscapeDataString(message));
        return builder.ToString();
    }
}
