using Aegis.Api.Controllers;
using Aegis.Application.Email;
using Aegis.Infrastructure.Email;
using Aegis.Infrastructure.Persistence;
using Aegis.Domain.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class EmailOAuthTests
{
    [Fact]
    public async Task AuthorizationUrlUsesConfiguredApiCallback()
    {
        var options = Options.Create(new GmailOptions
        {
            ClientId = "fake-client", ClientSecret = "fake-secret",
            RedirectUri = "https://api.example.test/api/email/oauth/callback"
        });
        var protector = DataProtectionProvider.Create("Aegis.Tests");
        using var db = new AegisDbContext(new DbContextOptionsBuilder<AegisDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options);
        using var http = new HttpClient();
        var service = new GmailConnectionService(db, http, options, new EmailTokenProtector(protector), protector);

        var authorization = await service.CreateAuthorizationUrlAsync();
        var url = new Uri(authorization.AuthorizationUrl);
        Assert.Equal("accounts.google.com", url.Host);
        Assert.Contains("redirect_uri=https%3A%2F%2Fapi.example.test%2Fapi%2Femail%2Foauth%2Fcallback", url.Query);
        Assert.Contains("state=", url.Query);
    }

    [Fact]
    public async Task ProtectedStateTokenExchangeAndProfilePersistConnectedAccount()
    {
        var options = Options.Create(new GmailOptions
        {
            ClientId = "fake-client", ClientSecret = "fake-secret",
            RedirectUri = "https://api.example.test/api/email/oauth/callback",
            PublicAppUrl = "https://app.example.test"
        });
        var protector = DataProtectionProvider.Create("Aegis.Tests");
        using var db = new AegisDbContext(new DbContextOptionsBuilder<AegisDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        using var http = new HttpClient(new GoogleHandler());
        var service = new GmailConnectionService(db, http, options, new EmailTokenProtector(protector), protector);
        var link = await service.CreateAuthorizationUrlAsync();
        var statePart = new Uri(link.AuthorizationUrl).Query.TrimStart('?').Split('&')
            .Single(part => part.StartsWith("state=", StringComparison.Ordinal));
        var state = Uri.UnescapeDataString(statePart["state=".Length..]);

        await service.HandleOAuthCallbackAsync("fake-code", state);
        var status = await service.GetStatusAsync();

        Assert.True(status.IsConnected);
        Assert.Equal("name@gmail.com", status.EmailAddress);
        Assert.NotEqual("fake-access", db.EmailAccountConnections.Single().AccessTokenEncrypted);
        await Assert.ThrowsAsync<EmailConnectionException>(() => service.HandleOAuthCallbackAsync("fake-code", "bad-state"));
    }

    [Fact]
    public async Task TemporaryGoogleRefreshFailureDoesNotDisconnectAccount()
    {
        var protector = DataProtectionProvider.Create("Aegis.Tests");
        var tokenProtector = new EmailTokenProtector(protector);
        using var db = new AegisDbContext(new DbContextOptionsBuilder<AegisDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.EmailAccountConnections.Add(new EmailAccountConnection("gmail", "name@gmail.com",
            tokenProtector.Protect("access"), tokenProtector.Protect("refresh"),
            DateTimeOffset.UtcNow.AddMinutes(-1), GmailOptions.DefaultScope));
        await db.SaveChangesAsync();
        using var http = new HttpClient(new TemporaryGoogleFailureHandler());
        var gmailOptions = Options.Create(new GmailOptions { ClientId = "fake-client", ClientSecret = "fake-secret" });
        var service = new GmailService(http, gmailOptions, new GoogleAccessTokenProvider(db, http, gmailOptions, tokenProtector));

        await Assert.ThrowsAsync<HttpRequestException>(() => service.SearchEmailsAsync("test"));
        Assert.Null(db.EmailAccountConnections.Single().DisconnectedAt);
    }

    [Fact]
    public async Task CallbackReturnsToPublicPwaAndStatusShowsAddress()
    {
        var service = new FakeConnectionService();
        var controller = CreateController(service);

        var success = Assert.IsType<RedirectResult>(await controller.OAuthCallback("code", "state", null, CancellationToken.None));
        Assert.Equal("https://app.example.test/?email=connected", success.Url);
        Assert.True(service.Completed);
        var status = Assert.IsType<OkObjectResult>((await controller.GetStatus(CancellationToken.None)).Result);
        Assert.Equal("name@gmail.com", Assert.IsType<EmailConnectionStatusResponse>(status.Value).EmailAddress);
    }

    [Theory]
    [InlineData("access_denied", "authorization_cancelled")]
    [InlineData("invalid_request", "google_rejected")]
    public async Task CallbackFailureUsesSafeCodeWithoutRawException(string error, string code)
    {
        var controller = CreateController(new FakeConnectionService());
        var result = Assert.IsType<RedirectResult>(await controller.OAuthCallback(null, null, error, CancellationToken.None));
        Assert.Equal($"https://app.example.test/?email=connect_failed&email_error_code={code}", result.Url);
        Assert.DoesNotContain("email_error_message", result.Url);
    }

    [Fact]
    public async Task InvalidStateUsesSafeFailureCategory()
    {
        var service = new FakeConnectionService { RejectState = true };
        var controller = CreateController(service);
        var result = Assert.IsType<RedirectResult>(await controller.OAuthCallback("code", "bad", null, CancellationToken.None));
        Assert.Contains("email_error_code=oauth_state_invalid", result.Url);
        Assert.DoesNotContain("secret", result.Url);
    }

    [Fact]
    public async Task PublicAppPathIsPreservedBehindReverseProxy()
    {
        var service = new FakeConnectionService();
        var controller = new EmailController(service, Options.Create(new GmailOptions
        {
            PublicAppUrl = "https://example.test/aegis/"
        }), NullLogger<EmailController>.Instance, new FakeLifetime());
        var result = Assert.IsType<RedirectResult>(await controller.OAuthCallback("code", "state", null, CancellationToken.None));
        Assert.Equal("https://example.test/aegis/?email=connected", result.Url);
    }

    [Theory]
    [InlineData("https://api.example.test/api/email/oauth/callback", "https://api.example.test/api/email/connect?redirect=true")]
    [InlineData("https://example.test/aegis/api/email/oauth/callback", "https://example.test/aegis/api/email/connect?redirect=true")]
    public async Task ChatConnectionToolsReturnShortEntryLinksWithoutProtectedState(string callback, string expected)
    {
        var options = Options.Create(new GmailOptions { ClientId = "fake-client", ClientSecret = "fake-secret", RedirectUri = callback });
        var protector = DataProtectionProvider.Create("Aegis.Tests");
        using var db = new AegisDbContext(new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        using var http = new HttpClient(new GoogleHandler());
        var connection = new GmailConnectionService(db, http, options, new EmailTokenProtector(protector), protector);
        var context = new Aegis.Application.Tools.ToolExecutionContext(Guid.NewGuid(), Guid.NewGuid(), "autorize o Calendar");
        var args = System.Text.Json.JsonSerializer.SerializeToElement(new { });
        var tools = new Aegis.Application.Tools.IAegisTool[]
        {
            new Aegis.Application.Email.Tools.EmailCreateConnectLinkTool(connection),
            new Aegis.Application.Calendar.Tools.CalendarCreateConnectLinkTool(connection)
        };
        foreach (var tool in tools)
        {
            var result = await tool.ExecuteAsync(args, context);
            Assert.True(result.Success);
            var url = System.Text.Json.JsonDocument.Parse(result.Content).RootElement.GetProperty("authorizationUrl").GetString();
            Assert.Equal(expected, url);
            Assert.DoesNotContain("state=", result.Content);
            Assert.DoesNotContain("client_id=", result.Content);
            Assert.DoesNotContain("scope=", result.Content);
        }
        Assert.Empty(db.EmailAccountConnections);
    }

    [Fact]
    public async Task EntryEndpointGeneratesFreshStatesOnEveryClick()
    {
        var options = Options.Create(new GmailOptions { ClientId = "fake-client", ClientSecret = "fake-secret", RedirectUri = "https://api.example.test/api/email/oauth/callback" });
        var protector = DataProtectionProvider.Create("Aegis.Tests");
        using var db = new AegisDbContext(new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        using var http = new HttpClient(new GoogleHandler());
        var connection = new GmailConnectionService(db, http, options, new EmailTokenProtector(protector), protector);
        var controller = CreateControllerWithConnection(connection);
        var first = Assert.IsType<RedirectResult>((await controller.Connect(true)).Result);
        var second = Assert.IsType<RedirectResult>((await controller.Connect(true)).Result);
        Assert.Equal("accounts.google.com", new Uri(first.Url).Host);
        Assert.NotEqual(StateFrom(first.Url), StateFrom(second.Url));
        Assert.Contains(Uri.EscapeDataString(Aegis.Application.Google.GoogleScopes.CalendarList), first.Url);
    }

    [Fact]
    public async Task OAuthStateCreatedBeforeRestartIsAcceptedWithThePersistedKeys()
    {
        var directory = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aegis-oauth-restart-" + Guid.NewGuid()));
        directory.Create();
        try
        {
            var options = Options.Create(new GmailOptions { ClientId = "fake-client", ClientSecret = "fake-secret", RedirectUri = "https://api.example.test/api/email/oauth/callback" });
            using var db = new AegisDbContext(new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            using var http = new HttpClient(new GoogleHandler());
            string state;
            using (var first = PersistentProtection(directory))
            {
                var protection = first.GetRequiredService<IDataProtectionProvider>();
                var connection = new GmailConnectionService(db, http, options, new EmailTokenProtector(protection), protection);
                state = StateFrom((await connection.CreateAuthorizationUrlAsync()).AuthorizationUrl);
            }
            using (var restarted = PersistentProtection(directory))
            {
                var protection = restarted.GetRequiredService<IDataProtectionProvider>();
                var connection = new GmailConnectionService(db, http, options, new EmailTokenProtector(protection), protection);
                await connection.HandleOAuthCallbackAsync("fake-code", state);
                Assert.True((await connection.GetStatusAsync()).IsConnected);
                Assert.True(Aegis.Application.Google.GoogleScopes.Contains(db.EmailAccountConnections.Single().Scopes, Aegis.Application.Google.GoogleScopes.CalendarList));
            }
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task PersistedGoogleTokensScopesAndRefreshSurviveRecreatedServicesWithoutLogin()
    {
        var directory = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aegis-token-restart-" + Guid.NewGuid()));
        directory.Create();
        try
        {
            var dbOptions = new DbContextOptionsBuilder<AegisDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString(), new Microsoft.EntityFrameworkCore.Storage.InMemoryDatabaseRoot()).Options;
            Guid connectionId;
            using (var first = PersistentProtection(directory))
            using (var db = new AegisDbContext(dbOptions))
            {
                var tokenProtector = new EmailTokenProtector(first.GetRequiredService<IDataProtectionProvider>());
                var connection = new EmailAccountConnection("gmail", "name@gmail.com", tokenProtector.Protect("old-access"), tokenProtector.Protect("saved-refresh"),
                    DateTimeOffset.UtcNow.AddMinutes(-1), Aegis.Application.Google.GoogleScopes.Combined);
                connectionId = connection.Id;
                db.AddEmailAccountConnection(connection);
                await db.SaveChangesAsync();
            }
            using (var restarted = PersistentProtection(directory))
            using (var db = new AegisDbContext(dbOptions))
            using (var http = new HttpClient(new GoogleHandler()))
            {
                var protection = restarted.GetRequiredService<IDataProtectionProvider>();
                var tokenProtector = new EmailTokenProtector(protection);
                var options = Options.Create(new GmailOptions { ClientId = "fake-client", ClientSecret = "fake-secret" });
                var tokens = new GoogleAccessTokenProvider(db, http, options, tokenProtector);
                var connection = new GmailConnectionService(db, http, options, tokenProtector, protection);
                var calendar = new Aegis.Infrastructure.Calendar.GoogleCalendarService(http, tokens, connection,
                    NullLogger<Aegis.Infrastructure.Calendar.GoogleCalendarService>.Instance,
                    Options.Create(new Aegis.Infrastructure.Calendar.GoogleCalendarOptions()));
                Assert.True((await connection.GetStatusAsync()).IsConnected);
                Assert.True((await calendar.GetStatusAsync()).CalendarAuthorized);
                Assert.Equal("fake-access", await tokens.GetAccessTokenAsync(Aegis.Application.Google.GoogleScopes.CalendarList));
                Assert.Equal("fake-access", await tokens.GetAccessTokenAsync(Aegis.Application.Google.GoogleScopes.Gmail));
                var saved = Assert.Single(db.EmailAccountConnections);
                Assert.Equal(connectionId, saved.Id);
                Assert.Equal(Aegis.Application.Google.GoogleScopes.Combined, saved.Scopes);
                Assert.Equal("saved-refresh", tokenProtector.Unprotect(saved.RefreshTokenEncrypted!));
                Assert.Null(saved.DisconnectedAt);
            }
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task TamperedStateIsRejectedBeforeExchangingTokensAndPreservesExistingConnection()
    {
        var protector = DataProtectionProvider.Create("Aegis.Tests");
        var tokenProtector = new EmailTokenProtector(protector);
        using var db = new AegisDbContext(new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var saved = new EmailAccountConnection("gmail", "name@gmail.com", tokenProtector.Protect("saved-access"), tokenProtector.Protect("saved-refresh"),
            DateTimeOffset.UtcNow.AddHours(1), Aegis.Application.Google.GoogleScopes.Combined);
        db.AddEmailAccountConnection(saved);
        await db.SaveChangesAsync();
        using var http = new HttpClient(new GoogleHandler());
        var options = Options.Create(new GmailOptions { ClientId = "fake-client", ClientSecret = "fake-secret", RedirectUri = "https://api.example.test/api/email/oauth/callback" });
        var connection = new GmailConnectionService(db, http, options, tokenProtector, protector);
        var state = StateFrom((await connection.CreateAuthorizationUrlAsync()).AuthorizationUrl);
        var middle = state.Length / 2;
        var tampered = state[..middle] + (state[middle] == 'a' ? 'b' : 'a') + state[(middle + 1)..];
        Assert.Equal("oauth_state_invalid", (await Assert.ThrowsAsync<EmailConnectionException>(() => connection.HandleOAuthCallbackAsync("fake-code", tampered))).Code);
        Assert.Equal("saved-access", tokenProtector.Unprotect(saved.AccessTokenEncrypted));
        Assert.Equal("saved-refresh", tokenProtector.Unprotect(saved.RefreshTokenEncrypted!));
        Assert.Null(saved.DisconnectedAt);
    }

    private static Microsoft.Extensions.DependencyInjection.ServiceProvider PersistentProtection(DirectoryInfo directory)
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddDataProtection().SetApplicationName("Aegis").PersistKeysToFileSystem(directory);
        return services.BuildServiceProvider();
    }

    private static string StateFrom(string url) => Uri.UnescapeDataString(new Uri(url).Query.TrimStart('?').Split('&')
        .Single(part => part.StartsWith("state=", StringComparison.Ordinal))["state=".Length..]);

    private static EmailController CreateControllerWithConnection(IEmailConnectionService connection) =>
        new(connection, Options.Create(new GmailOptions { PublicAppUrl = "https://app.example.test" }), NullLogger<EmailController>.Instance, new FakeLifetime());

    private static EmailController CreateController(FakeConnectionService service) =>
        new(service, Options.Create(new GmailOptions
        {
            PublicAppUrl = "https://app.example.test",
            SuccessRedirectPath = "/?email=connected",
            FailureRedirectPath = "/?email=connect_failed"
        }), NullLogger<EmailController>.Instance, new FakeLifetime());

    private sealed class FakeConnectionService : IEmailConnectionService
    {
        public bool Completed { get; private set; }
        public bool RejectState { get; set; }
        public Task<EmailConnectionStatusResponse> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmailConnectionStatusResponse(Completed, "gmail", Completed ? "name@gmail.com" : null, null, null));
        public Task<EmailAuthorizationResponse> CreateConnectLinkAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmailAuthorizationResponse("https://api.example.test/api/email/connect?redirect=true"));
        public Task<EmailAuthorizationResponse> CreateAuthorizationUrlAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmailAuthorizationResponse("https://accounts.google.com/test"));
        public Task HandleOAuthCallbackAsync(string code, string? state, CancellationToken cancellationToken = default)
        {
            if (RejectState) throw new EmailConnectionException("oauth_state_invalid", "secret invalid state detail");
            Completed = true;
            return Task.CompletedTask;
        }
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private sealed class GoogleHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var refreshing = request.Content is not null && (await request.Content.ReadAsStringAsync(cancellationToken)).Contains("grant_type=refresh_token");
            var body = request.RequestUri!.Host switch
            {
                "oauth2.googleapis.com" => refreshing ? "{\"access_token\":\"fake-access\",\"expires_in\":3600}" : "{\"access_token\":\"fake-access\",\"refresh_token\":\"fake-refresh\",\"expires_in\":3600}",
                "gmail.googleapis.com" => "{\"emailAddress\":\"name@gmail.com\"}",
                _ => throw new InvalidOperationException("Unexpected Google endpoint")
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class TemporaryGoogleFailureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}
