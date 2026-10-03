using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
namespace Aegis.Api.Nodes;
public static class NodeApiServices
{
    public static IServiceCollection AddNodeApi(this IServiceCollection services, IConfiguration? configuration = null)
    {
        // ASP.NET otherwise promotes a single registered scheme to the implicit default.
        // Node credentials are opt-in through NodeManagement, never generic tool authorization.
        AppContext.SetSwitch("Microsoft.AspNetCore.Authentication.SuppressAutoDefaultScheme", true);
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, NodeAuthenticationHandler>(NodeAuthenticationHandler.SchemeName, _ => { });
        services.AddAuthorization(options => options.AddPolicy("NodeManagement", policy =>
            policy.AddAuthenticationSchemes(NodeAuthenticationHandler.SchemeName).RequireAuthenticatedUser()));
        services.AddScoped<NodeApiFilter>();
        services.AddOptions<Transport.NodeTransportOptions>().Configure(options => configuration?.GetSection("NodeTransport").Bind(options))
            .Validate(o => o.IsValid(), "Invalid NodeTransport limits.").ValidateOnStart();
        services.AddSingleton<Transport.NodeConnectionRegistry>();
        services.AddSingleton<Aegis.Application.Nodes.INodeConnections>(p => p.GetRequiredService<Transport.NodeConnectionRegistry>());
        services.AddSingleton<Aegis.Api.Controllers.NodeHandshakeCapacity>();
        services.AddHostedService<Transport.NodeConnectionWatchdog>();
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.OnRejected = async (context, ct) => {
                context.HttpContext.Response.Headers.RetryAfter = "60";
                await context.HttpContext.Response.WriteAsJsonAsync(new { code = "node_rate_limited", error = "Muitas tentativas. Aguarde um minuto antes de tentar novamente." }, ct);
            };
            options.AddPolicy("node-connect", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions {
                    PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.AddPolicy("node-pairing", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions {
                    PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.AddPolicy("node-codes", context => RateLimitPartition.GetFixedWindowLimiter(
                context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unauthenticated", _ => new FixedWindowRateLimiterOptions {
                    PermitLimit = 3, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            // A shared budget for anonymous establishment also prevents bypass by rotating IPs.
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                context.Request.Path.StartsWithSegments("/api/nodes/pair")
                    ? RateLimitPartition.GetFixedWindowLimiter("node-establishment", _ => new FixedWindowRateLimiterOptions {
                        PermitLimit = 100, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })
                    : context.Request.Path == "/api/nodes/connect"
                    ? RateLimitPartition.GetFixedWindowLimiter("node-handshake", _ => new FixedWindowRateLimiterOptions {
                        PermitLimit = 200, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })
                    : RateLimitPartition.GetNoLimiter("other"));
        });
        return services;
    }
}
