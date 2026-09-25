using Aegis.Application.Email;
using Aegis.Infrastructure.Email;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

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
        try
        {
            var response = await emailConnectionService.CreateAuthorizationUrlAsync(cancellationToken);
            if (redirect)
            {
                return Redirect(response.AuthorizationUrl);
            }

            return Ok(response);
        }
        catch (EmailConnectionException exception)
        {
            logger.LogWarning(exception, "Gmail connection link could not be created.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                code = exception.Code,
                error = "A conexão Gmail não está disponível neste servidor."
            });
        }
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
            var failureCode = string.Equals(error, "access_denied", StringComparison.Ordinal) ? "authorization_cancelled" : "google_rejected";
            return Redirect(BuildRedirectUri(options, options.FailureRedirectPath, failureCode));
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
            return Redirect(BuildRedirectUri(options, options.SuccessRedirectPath));
        }
        catch (HttpRequestException exception)
        {
            LogCallbackFailure("google_http_error", exception);
            var failureCode = (int?)exception.StatusCode is >= 500 or null ? "oauth_temporary_error" : "google_rejected";
            return Redirect(BuildRedirectUri(options, options.FailureRedirectPath, failureCode));
        }
        catch (EmailConnectionException exception)
        {
            LogCallbackFailure(exception.Code, exception);
            return Redirect(BuildRedirectUri(options, options.FailureRedirectPath, exception.Code));
        }
        catch (InvalidOperationException exception)
        {
            LogCallbackFailure("oauth_invalid_operation", exception);
            return Redirect(BuildRedirectUri(options, options.FailureRedirectPath, "oauth_temporary_error"));
        }
        catch (ArgumentException exception)
        {
            LogCallbackFailure("oauth_invalid_argument", exception);
            return Redirect(BuildRedirectUri(options, options.FailureRedirectPath, "oauth_state_invalid"));
        }
        catch (Exception exception)
        {
            LogCallbackFailure("oauth_unknown_error", exception, LogLevel.Error);
            return Redirect(BuildRedirectUri(options, options.FailureRedirectPath, "oauth_temporary_error"));
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

    private static string BuildRedirectUri(GmailOptions options, string path, string? code = null)
    {
        if (!Uri.TryCreate(options.PublicAppUrl, UriKind.Absolute, out var appUri) ||
            appUri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(appUri.Query) || !string.IsNullOrEmpty(appUri.Fragment) ||
            !path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Public app URL or OAuth return path is invalid.");
        }

        var appBase = new Uri(appUri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");
        var destination = new Uri(appBase, path.TrimStart('/')).ToString();
        if (code is null) return destination;
        return destination + (destination.Contains('?', StringComparison.Ordinal) ? "&" : "?") +
            "email_error_code=" + Uri.EscapeDataString(code);
    }
}
