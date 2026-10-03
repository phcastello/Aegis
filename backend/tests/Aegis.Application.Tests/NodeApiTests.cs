using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aegis.Api.Controllers;
using Aegis.Api.Nodes;
using Aegis.Application.Nodes;
using Aegis.Infrastructure.Nodes;
using Aegis.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace Aegis.Application.Tests;
public sealed class NodeApiTests
{
    private static async Task<(WebApplication App, HttpClient Client, NodeRegistry Registry)> Host()
    {
        var name = Guid.NewGuid().ToString(); var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Services.AddControllers().AddApplicationPart(typeof(NodesController).Assembly);
        builder.Services.AddNodeApi(); builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddDbContext<AegisDbContext>(o => o.UseInMemoryDatabase(name)); builder.Services.AddScoped<INodeRegistry, NodeRegistry>();
        var app = builder.Build(); app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter(); app.MapControllers();
        app.MapGet("/existing-chat-boundary", () => "chat unchanged"); await app.StartAsync();
        var db = new AegisDbContext(new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(name).Options);
        return (app, app.GetTestClient(), new(db, TimeProvider.System));
    }
    private static async Task<(NodeView Node, string Secret)> Pair(NodeRegistry registry)
    {
        var request = NodeIdentityTests.Request((await registry.CreateCodeAsync(null)).Code);
        var receipt = await registry.PairAsync(request); return (await registry.FinalizeAsync(new(request.AttemptId, receipt.Credential)), receipt.Credential);
    }
    [Fact] public async Task PublicCodeCreationAndManagementAreDeniedChatRemainsIndependent()
    {
        var (app, client, _) = await Host(); await using var dispose = app;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/nodes/pairing-codes", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/nodes")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/existing-chat-boundary")).StatusCode);
    }
    [Fact] public async Task RealHttpPairingAndManagementNeverReturnCredentialAfterEstablishment()
    {
        var (app, client, registry) = await Host(); await using var dispose = app;
        var request = NodeIdentityTests.Request((await registry.CreateCodeAsync(null)).Code);
        var response = await client.PostAsJsonAsync("/api/nodes/pair", request); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        var receipt = (await response.Content.ReadFromJsonAsync<PairNodeReceipt>())!;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/nodes/pair/finalize", new FinalizeNodeRequest(request.AttemptId, receipt.Credential))).StatusCode);
        client.DefaultRequestHeaders.Add("Authorization", "AegisNode " + receipt.Credential);
        var list = await client.GetAsync("/api/nodes"); Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.DoesNotContain(receipt.Credential, await list.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync("/api/nodes/" + receipt.NodeId, new { name = "PC renamed" })).StatusCode);
        Assert.Equal("PC renamed", (await client.GetFromJsonAsync<NodeView>("/api/nodes/me"))!.Name);
    }
    [Fact] public async Task DisabledAndRevokedCredentialsProduceDistinctSafeResponses()
    {
        var (app, client, registry) = await Host(); await using var dispose = app;
        var pc = await Pair(registry); client.DefaultRequestHeaders.Add("Authorization", "AegisNode " + pc.Secret);
        await registry.SetEnabledAsync(pc.Node.Id, pc.Node.Id, false);
        var disabled = await client.GetAsync("/api/nodes/me"); Assert.Equal(HttpStatusCode.Forbidden, disabled.StatusCode); Assert.Contains("node_disabled", await disabled.Content.ReadAsStringAsync());
        await registry.SetEnabledAsync(null, pc.Node.Id, true); await registry.RevokeAsync(pc.Node.Id, pc.Node.Id);
        var revoked = await client.GetAsync("/api/nodes/me"); Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode); Assert.Contains("node_revoked", await revoked.Content.ReadAsStringAsync());
    }
    [Fact] public async Task AnonymousPairingIsRateLimitedAndDoesNotTrustForwardedHeader()
    {
        var (app, client, _) = await Host(); await using var dispose = app;
        for (var i = 0; i < 20; i++) { client.DefaultRequestHeaders.Remove("X-Forwarded-For"); client.DefaultRequestHeaders.Add("X-Forwarded-For", "192.0.2." + i); await client.PostAsJsonAsync("/api/nodes/pair", NodeIdentityTests.Request("invalid")); }
        var limited = await client.PostAsJsonAsync("/api/nodes/pair", NodeIdentityTests.Request("invalid")); Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode); Assert.NotNull(limited.Headers.RetryAfter);
    }
    [Fact] public async Task CodeRateLimitIsPartitionedByAuthenticatedNode()
    {
        var (app, client, registry) = await Host(); await using var dispose = app;
        var first = await Pair(registry); var second = await Pair(registry);
        client.DefaultRequestHeaders.Add("Authorization", "AegisNode " + first.Secret);
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/nodes/pairing-codes", null)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsync("/api/nodes/pairing-codes", null)).StatusCode);
        client.DefaultRequestHeaders.Remove("Authorization"); client.DefaultRequestHeaders.Add("Authorization", "AegisNode " + second.Secret);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/nodes/pairing-codes", null)).StatusCode);
    }
}
