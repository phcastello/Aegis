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
    IOptions<GmailOptions> gmailOptions,
    ILogger<EmailController> logger,
    IHostApplicationLifetime applicationLifetime) : ControllerBase
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
            logger.LogWarning(
                "Gmail OAuth callback failed. ErrorCode: {ErrorCode}. Message: {ErrorMessage}",
                "oauth_callback_error",
                message);
            return Redirect(BuildFailureRedirectUri(options.FailureRedirectPath, "oauth_callback_error", message));
        }

        try
        {
            // The browser may close the callback request as soon as Google redirects back.
            // Completing the authorization must not depend on that client connection staying open.
            await emailConnectionService.HandleOAuthCallbackAsync(
                code,
                state,
                applicationLifetime.ApplicationStopping);
            logger.LogInformation("Gmail OAuth callback completed successfully.");
            return Redirect(options.SuccessRedirectPath);
        }
        catch (HttpRequestException exception)
        {
            LogCallbackFailure("google_http_error", exception);
            return Redirect(BuildFailureRedirectUri(
                options.FailureRedirectPath,
                "google_http_error",
                exception.Message));
        }
        catch (InvalidOperationException exception)
        {
            LogCallbackFailure("oauth_invalid_operation", exception);
            return Redirect(BuildFailureRedirectUri(
                options.FailureRedirectPath,
                "oauth_invalid_operation",
                exception.Message));
        }
        catch (ArgumentException exception)
        {
            LogCallbackFailure("oauth_invalid_argument", exception);
            return Redirect(BuildFailureRedirectUri(
                options.FailureRedirectPath,
                "oauth_invalid_argument",
                exception.Message));
        }
        catch (Exception exception)
        {
            LogCallbackFailure("oauth_unknown_error", exception, LogLevel.Error);
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

    private void LogCallbackFailure(
        string errorCode,
        Exception exception,
        LogLevel logLevel = LogLevel.Warning)
    {
        logger.Log(
            logLevel,
            exception,
            "Gmail OAuth callback failed. ErrorCode: {ErrorCode}. ExceptionType: {ExceptionType}. Message: {ErrorMessage}",
            errorCode,
            exception.GetType().Name,
            exception.Message);
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
