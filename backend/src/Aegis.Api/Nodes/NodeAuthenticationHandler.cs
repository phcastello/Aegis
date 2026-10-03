using System.Security.Claims;
using System.Text.Encodings.Web;
using Aegis.Application.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
namespace Aegis.Api.Nodes;

public sealed class NodeAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, INodeRegistry nodes) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "AegisNode";
    private string failure = "node_authentication_required";
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Path.StartsWithSegments("/api/nodes/connect"))
        {
            using var admission = Context.RequestServices.GetRequiredService<Aegis.Api.Controllers.NodeHandshakeCapacity>().Attempts.AttemptAcquire();
            if (!admission.IsAcquired) { failure = "node_rate_limited"; return AuthenticateResult.Fail("Node handshake rate limited."); }
        }
        var header = Request.Headers.Authorization.ToString();
        if (header.Length > 180 || !header.StartsWith(SchemeName + " ", StringComparison.Ordinal)) return AuthenticateResult.NoResult();
        var authentication = await nodes.AuthenticateAsync(header[(SchemeName.Length + 1)..], Context.RequestAborted);
        if (!authentication.Authenticated) { failure = authentication.Error!; return AuthenticateResult.Fail("Node authentication rejected."); }
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, authentication.Node!.Id.ToString()) };
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName));
    }
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = failure == "node_rate_limited" ? 429 : failure == "node_disabled" ? 403 : 401;
        Response.Headers["X-Aegis-Node-Error"] = failure;
        Response.Headers.CacheControl = "no-store";
        Response.Headers.WWWAuthenticate = SchemeName;
        return Response.WriteAsJsonAsync(new { code = failure, error = failure switch {
            "node_disabled" => "Este Node está desativado.", "node_revoked" => "A identidade deste Node foi revogada.",
            _ => "Autenticação de Node necessária." } });
    }
}
