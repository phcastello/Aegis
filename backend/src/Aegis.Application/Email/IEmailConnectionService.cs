// Copyright (C) 2026 Pedro Hasson Castello.
// GNU AGPL v3 only; attribution terms: /ADDITIONAL_TERMS.md.
namespace Aegis.Application.Email;

public interface IEmailConnectionService
{
    Task<EmailConnectionStatusResponse> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<EmailAuthorizationResponse> CreateConnectLinkAsync(CancellationToken cancellationToken = default);

    Task<EmailAuthorizationResponse> CreateAuthorizationUrlAsync(CancellationToken cancellationToken = default);

    Task HandleOAuthCallbackAsync(
        string code,
        string? state,
        CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);
}
