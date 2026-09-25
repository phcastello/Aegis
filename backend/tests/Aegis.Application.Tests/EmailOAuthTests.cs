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
        var service = new GmailService(db, http, Options.Create(new GmailOptions
        {
            ClientId = "fake-client", ClientSecret = "fake-secret"
        }), tokenProtector);

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
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri!.Host switch
            {
                "oauth2.googleapis.com" => "{\"access_token\":\"fake-access\",\"refresh_token\":\"fake-refresh\",\"expires_in\":3600}",
                "gmail.googleapis.com" => "{\"emailAddress\":\"name@gmail.com\"}",
                _ => throw new InvalidOperationException("Unexpected Google endpoint")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class TemporaryGoogleFailureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}
