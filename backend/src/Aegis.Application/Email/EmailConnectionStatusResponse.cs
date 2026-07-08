namespace Aegis.Application.Email;

public sealed record EmailConnectionStatusResponse(
    bool IsConnected,
    string? Provider,
    string? EmailAddress,
    string? Scopes,
    DateTimeOffset? ConnectedAt);
