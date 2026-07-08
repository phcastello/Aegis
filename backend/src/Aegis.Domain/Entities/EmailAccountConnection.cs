namespace Aegis.Domain.Entities;

public sealed class EmailAccountConnection : AuditableEntity
{
    public const string GmailProvider = "gmail";

    private EmailAccountConnection()
    {
    }

    public EmailAccountConnection(
        string provider,
        string? emailAddress,
        string accessTokenEncrypted,
        string? refreshTokenEncrypted,
        DateTimeOffset accessTokenExpiresAt,
        string scopes)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            throw new ArgumentException("Provider is required.", nameof(provider));
        }

        if (string.IsNullOrWhiteSpace(accessTokenEncrypted))
        {
            throw new ArgumentException("Access token is required.", nameof(accessTokenEncrypted));
        }

        InitializeAudit();
        Provider = provider.Trim().ToLowerInvariant();
        EmailAddress = NormalizeOptional(emailAddress);
        AccessTokenEncrypted = accessTokenEncrypted;
        RefreshTokenEncrypted = NormalizeOptional(refreshTokenEncrypted);
        AccessTokenExpiresAt = accessTokenExpiresAt;
        Scopes = scopes.Trim();
    }

    public string Provider { get; private set; } = string.Empty;

    public string? EmailAddress { get; private set; }

    public string AccessTokenEncrypted { get; private set; } = string.Empty;

    public string? RefreshTokenEncrypted { get; private set; }

    public DateTimeOffset AccessTokenExpiresAt { get; private set; }

    public string Scopes { get; private set; } = string.Empty;

    public DateTimeOffset? DisconnectedAt { get; private set; }

    public bool IsConnected => DisconnectedAt is null;

    public void ReplaceTokens(
        string? emailAddress,
        string accessTokenEncrypted,
        string? refreshTokenEncrypted,
        DateTimeOffset accessTokenExpiresAt,
        string scopes)
    {
        if (string.IsNullOrWhiteSpace(accessTokenEncrypted))
        {
            throw new ArgumentException("Access token is required.", nameof(accessTokenEncrypted));
        }

        EmailAddress = NormalizeOptional(emailAddress) ?? EmailAddress;
        AccessTokenEncrypted = accessTokenEncrypted;
        RefreshTokenEncrypted = NormalizeOptional(refreshTokenEncrypted) ?? RefreshTokenEncrypted;
        AccessTokenExpiresAt = accessTokenExpiresAt;
        Scopes = scopes.Trim();
        DisconnectedAt = null;
        Touch();
    }

    public void UpdateAccessToken(
        string accessTokenEncrypted,
        DateTimeOffset accessTokenExpiresAt)
    {
        if (string.IsNullOrWhiteSpace(accessTokenEncrypted))
        {
            throw new ArgumentException("Access token is required.", nameof(accessTokenEncrypted));
        }

        AccessTokenEncrypted = accessTokenEncrypted;
        AccessTokenExpiresAt = accessTokenExpiresAt;
        Touch();
    }

    public void Disconnect(DateTimeOffset? now = null)
    {
        DisconnectedAt ??= now ?? DateTimeOffset.UtcNow;
        Touch(DisconnectedAt);
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
