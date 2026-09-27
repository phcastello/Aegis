using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Aegis.Application.Email;
using Aegis.Application.Google;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aegis.Infrastructure.Email;

public sealed class GoogleAccessTokenProvider(
    AegisDbContext dbContext,
    HttpClient httpClient,
    IOptions<GmailOptions> options,
    EmailTokenProtector tokenProtector) : IGoogleAccessTokenProvider
{
    public async Task<string> GetAccessTokenAsync(string requiredScope, CancellationToken cancellationToken = default)
    {
        var connection = await dbContext.EmailAccountConnections
            .Where(item =>
                item.Provider == EmailAccountConnection.GmailProvider &&
                item.DisconnectedAt == null)
            .OrderByDescending(item => item.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (connection is null)
        {
            throw new EmailNotConnectedException();
        }

        RequireScope(connection, requiredScope);

        if (connection.AccessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            try
            {
                return tokenProtector.Unprotect(connection.AccessTokenEncrypted);
            }
            catch (CryptographicException)
            {
                connection.Disconnect();
                await dbContext.SaveChangesAsync(cancellationToken);
                throw new EmailNotConnectedException();
            }
        }

        var token = await RefreshAccessTokenAsync(connection, cancellationToken);
        RequireScope(connection, requiredScope);
        return token;
    }

    private async Task<string> RefreshAccessTokenAsync(
        EmailAccountConnection connection,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connection.RefreshTokenEncrypted))
        {
            connection.Disconnect();
            await dbContext.SaveChangesAsync(cancellationToken);
            throw new EmailNotConnectedException();
        }

        var gmailOptions = options.Value;
        string refreshToken;
        try
        {
            refreshToken = tokenProtector.Unprotect(connection.RefreshTokenEncrypted);
        }
        catch (CryptographicException)
        {
            connection.Disconnect();
            await dbContext.SaveChangesAsync(cancellationToken);
            throw new EmailNotConnectedException();
        }

        using var response = await httpClient.PostAsync(
            "https://oauth2.googleapis.com/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = gmailOptions.ClientId ?? string.Empty,
                ["client_secret"] = gmailOptions.ClientSecret ?? string.Empty,
                ["refresh_token"] = refreshToken,
                ["grant_type"] = "refresh_token"
            }),
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                connection.Disconnect();
                await dbContext.SaveChangesAsync(cancellationToken);
                throw new EmailNotConnectedException();
            }

            throw new HttpRequestException("Google token refresh is temporarily unavailable.", null, response.StatusCode);
        }

        var tokens = await response.Content.ReadFromJsonAsync<GoogleTokenResponse>(
            cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Google returned an empty refresh response.");

        if (string.IsNullOrWhiteSpace(tokens.AccessToken))
        {
            connection.Disconnect();
            await dbContext.SaveChangesAsync(cancellationToken);
            throw new EmailNotConnectedException();
        }

        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, tokens.ExpiresIn));
        connection.ReplaceTokens(connection.EmailAddress, tokenProtector.Protect(tokens.AccessToken),
            string.IsNullOrWhiteSpace(tokens.RefreshToken) ? null : tokenProtector.Protect(tokens.RefreshToken),
            expiresAt, string.IsNullOrWhiteSpace(tokens.Scope) ? connection.Scopes : tokens.Scope);
        await dbContext.SaveChangesAsync(cancellationToken);

        return tokens.AccessToken;
    }

    private static void RequireScope(EmailAccountConnection connection, string scope)
    {
        if (!GoogleScopes.Contains(connection.Scopes, scope)) throw new GoogleScopeMissingException(scope);
    }

    private sealed class GoogleTokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; init; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }
        [JsonPropertyName("scope")] public string? Scope { get; init; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; init; }
    }
}
