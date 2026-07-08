using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Aegis.Application.Email;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aegis.Infrastructure.Email;

public sealed class GmailConnectionService(
    AegisDbContext dbContext,
    HttpClient httpClient,
    IOptions<GmailOptions> options,
    EmailTokenProtector tokenProtector,
    IDataProtectionProvider dataProtectionProvider) : IEmailConnectionService
{
    private readonly IDataProtector stateProtector =
        dataProtectionProvider.CreateProtector("Aegis.Email.GmailOAuthState.v1");

    public async Task<EmailConnectionStatusResponse> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var connection = await GetActiveConnectionAsync(cancellationToken);
        return connection is null
            ? new EmailConnectionStatusResponse(false, null, null, null, null)
            : new EmailConnectionStatusResponse(
                true,
                connection.Provider,
                connection.EmailAddress,
                connection.Scopes,
                connection.CreatedAt);
    }

    public Task<EmailAuthorizationResponse> CreateAuthorizationUrlAsync(
        CancellationToken cancellationToken = default)
    {
        var gmailOptions = GetConfiguredOptions();
        var state = CreateProtectedState();
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = gmailOptions.ClientId,
            ["redirect_uri"] = gmailOptions.RedirectUri,
            ["response_type"] = "code",
            ["scope"] = gmailOptions.Scopes,
            ["access_type"] = "offline",
            ["prompt"] = "consent",
            ["state"] = state
        };

        var queryString = string.Join(
            "&",
            query.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value ?? string.Empty)}"));
        return Task.FromResult(new EmailAuthorizationResponse(
            $"https://accounts.google.com/o/oauth2/v2/auth?{queryString}"));
    }

    public async Task HandleOAuthCallbackAsync(
        string code,
        string? state,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("OAuth code is required.", nameof(code));
        }

        ValidateProtectedState(state);
        var gmailOptions = GetConfiguredOptions();
        using var tokenResponse = await httpClient.PostAsync(
            "https://oauth2.googleapis.com/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = gmailOptions.ClientId!,
                ["client_secret"] = gmailOptions.ClientSecret!,
                ["redirect_uri"] = gmailOptions.RedirectUri!,
                ["code"] = code,
                ["grant_type"] = "authorization_code"
            }),
            cancellationToken);

        tokenResponse.EnsureSuccessStatusCode();
        var tokens = await tokenResponse.Content.ReadFromJsonAsync<GoogleTokenResponse>(
            cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Google returned an empty token response.");

        if (string.IsNullOrWhiteSpace(tokens.AccessToken))
        {
            throw new InvalidOperationException("Google did not return an access token.");
        }

        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, tokens.ExpiresIn));
        var accessTokenEncrypted = tokenProtector.Protect(tokens.AccessToken);
        var refreshTokenEncrypted = string.IsNullOrWhiteSpace(tokens.RefreshToken)
            ? null
            : tokenProtector.Protect(tokens.RefreshToken);
        var emailAddress = await GetEmailAddressAsync(tokens.AccessToken, cancellationToken);
        var scopes = string.IsNullOrWhiteSpace(tokens.Scope) ? gmailOptions.Scopes : tokens.Scope;

        var existing = await dbContext.EmailAccountConnections
            .Where(connection => connection.Provider == EmailAccountConnection.GmailProvider)
            .OrderByDescending(connection => connection.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is null)
        {
            dbContext.EmailAccountConnections.Add(new EmailAccountConnection(
                EmailAccountConnection.GmailProvider,
                emailAddress,
                accessTokenEncrypted,
                refreshTokenEncrypted,
                expiresAt,
                scopes));
        }
        else
        {
            existing.ReplaceTokens(
                emailAddress,
                accessTokenEncrypted,
                refreshTokenEncrypted,
                expiresAt,
                scopes);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        var connection = await GetActiveConnectionAsync(cancellationToken);
        if (connection is null)
        {
            return;
        }

        connection.Disconnect();
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<string?> GetEmailAddressAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://gmail.googleapis.com/gmail/v1/users/me/profile");
        request.Headers.Authorization = new("Bearer", accessToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var profile = await response.Content.ReadFromJsonAsync<GmailProfileResponse>(
            cancellationToken: cancellationToken);

        return profile?.EmailAddress;
    }

    private async Task<EmailAccountConnection?> GetActiveConnectionAsync(
        CancellationToken cancellationToken)
    {
        return await dbContext.EmailAccountConnections
            .Where(connection =>
                connection.Provider == EmailAccountConnection.GmailProvider &&
                connection.DisconnectedAt == null)
            .OrderByDescending(connection => connection.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private GmailOptions GetConfiguredOptions()
    {
        var gmailOptions = options.Value;
        if (string.IsNullOrWhiteSpace(gmailOptions.ClientId) ||
            string.IsNullOrWhiteSpace(gmailOptions.ClientSecret) ||
            string.IsNullOrWhiteSpace(gmailOptions.RedirectUri))
        {
            throw new InvalidOperationException(
                "Gmail OAuth is not configured. Set GOOGLE_CLIENT_ID, GOOGLE_CLIENT_SECRET and GOOGLE_REDIRECT_URI.");
        }

        if (string.IsNullOrWhiteSpace(gmailOptions.Scopes))
        {
            gmailOptions.Scopes = GmailOptions.DefaultScope;
        }

        return gmailOptions;
    }

    private string CreateProtectedState()
    {
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var payload = $"{DateTimeOffset.UtcNow:O}|{nonce}";
        return stateProtector.Protect(payload);
    }

    private void ValidateProtectedState(string? state)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            throw new InvalidOperationException("OAuth state is missing.");
        }

        string payload;
        try
        {
            payload = stateProtector.Unprotect(state);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("OAuth state is invalid.", exception);
        }

        var separatorIndex = payload.IndexOf('|', StringComparison.Ordinal);
        if (separatorIndex <= 0 ||
            !DateTimeOffset.TryParse(payload[..separatorIndex], out var createdAt) ||
            createdAt < DateTimeOffset.UtcNow.AddMinutes(-15))
        {
            throw new InvalidOperationException("OAuth state expired.");
        }
    }

    private sealed class GoogleTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; init; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; init; }

        [JsonPropertyName("scope")]
        public string? Scope { get; init; }
    }

    private sealed class GmailProfileResponse
    {
        [JsonPropertyName("emailAddress")]
        public string? EmailAddress { get; init; }
    }
}
