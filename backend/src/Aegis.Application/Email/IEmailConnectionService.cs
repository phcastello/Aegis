namespace Aegis.Application.Email;

public interface IEmailConnectionService
{
    Task<EmailConnectionStatusResponse> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<EmailAuthorizationResponse> CreateAuthorizationUrlAsync(CancellationToken cancellationToken = default);

    Task HandleOAuthCallbackAsync(
        string code,
        string? state,
        CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);
}
