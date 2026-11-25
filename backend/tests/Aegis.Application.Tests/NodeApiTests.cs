using System.Globalization;
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
        app.MapGet("/existing-chat-boundary", (Microsoft.AspNetCore.Http.HttpContext context) => new { authenticated = context.User.Identity?.IsAuthenticated ?? false }); await app.StartAsync();
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
        var boundary = await client.GetFromJsonAsync<JsonElement>("/existing-chat-boundary");
        Assert.False(boundary.GetProperty("authenticated").GetBoolean());
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
    [Fact]
    public async Task VoiceActivationAcceptsOnlyPairedNodesAndGrantsOneCandidateBeforeRecording()
    {
        var (app, client, registry) = await Host();
        await using var dispose = app;
        const string path = "/api/nodes/me/voice-activations/claim";
        var desktop = await Pair(registry);
        var phone = await Pair(registry);
        // No live socket is required for a potential listener to participate.
        Assert.Equal(new[] { desktop.Node.Id, phone.Node.Id }.Order(), (await registry.ListVoiceActivationParticipantsAsync())!.Order());
        var pcClaim = new VoiceActivationClaim(Guid.NewGuid(), 7, 0.98);
        var phoneClaim = new VoiceActivationClaim(Guid.NewGuid(), 22, 0.83);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(path, pcClaim)).StatusCode);
        using var pcHttp = app.GetTestClient();
        using var phoneHttp = app.GetTestClient();
        pcHttp.DefaultRequestHeaders.Add("Authorization", "AegisNode " + desktop.Secret);
        phoneHttp.DefaultRequestHeaders.Add("Authorization", "AegisNode " + phone.Secret);
        Assert.Equal(HttpStatusCode.BadRequest, (await pcHttp.PostAsJsonAsync(path, new {
            pcClaim.ActivationId, nodeId = phone.Node.Id
        })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await pcHttp.PostAsJsonAsync(path, new {
            pcClaim.ActivationId, samples = "not accepted"
        })).StatusCode);
        await Task.Delay(VoiceActivationArbiter.CatchUpWindow);
        var replies = await Task.WhenAll(pcHttp.PostAsJsonAsync(path, pcClaim), phoneHttp.PostAsJsonAsync(path, phoneClaim));
        Assert.All(replies, response => {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
            Assert.True(response.Headers.TryGetValues(NodeVoiceActivationController.ElectionWaitHeader, out var values));
            Assert.True(long.TryParse(Assert.Single(values), NumberStyles.None, CultureInfo.InvariantCulture, out _));
        });
        Assert.Equal(new(pcClaim.ActivationId, "suppressed"), await replies[0].Content.ReadFromJsonAsync<VoiceActivationDecision>());
        Assert.Equal(new(phoneClaim.ActivationId, "granted"), await replies[1].Content.ReadFromJsonAsync<VoiceActivationDecision>());
        await registry.SetEnabledAsync(null, phone.Node.Id, false);
        Assert.Equal(HttpStatusCode.Forbidden, (await phoneHttp.PostAsJsonAsync(path, phoneClaim)).StatusCode);
        Assert.Equal(new[] { desktop.Node.Id }, await registry.ListVoiceActivationParticipantsAsync());
    }

    [Fact]
    public async Task VoiceActivationDetectionAgeUsesStrictOptionalHeaderWithoutChangingTheJsonContract()
    {
        var (app, client, registry) = await Host();
        await using var dispose = app;
        const string path = "/api/nodes/me/voice-activations/claim";
        var phone = await Pair(registry);
        _ = await Pair(registry); // A possible second listener makes the timer necessary.
        var claim = new VoiceActivationClaim(Guid.NewGuid(), 22, 0.83);
        using (var unauthenticated = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(claim) })
        {
            Assert.True(unauthenticated.Headers.TryAddWithoutValidation(NodeVoiceActivationController.DetectionAgeHeader, "300"));
            using var denied = await client.SendAsync(unauthenticated);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            Assert.False(denied.Headers.Contains(NodeVoiceActivationController.ElectionWaitHeader));
        }
        client.DefaultRequestHeaders.Add("Authorization", "AegisNode " + phone.Secret);
        // HttpClient/TestServer omits an empty header entirely, so it is equivalent
        // to absent (legacy full window). Exercise malformed values sent on the wire.
        foreach (var invalidValues in new[] { new[] { "-1" }, new[] { "2001" }, new[] { "1.5" }, new[] { "+1" }, new[] { "NaN" }, new[] { "1", "2" } })
        {
            using var invalid = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(claim) };
            Assert.True(invalid.Headers.TryAddWithoutValidation(NodeVoiceActivationController.DetectionAgeHeader, invalidValues));
            using var reply = await client.SendAsync(invalid);
            Assert.True(reply.StatusCode == HttpStatusCode.BadRequest, $"Header [{string.Join(", ", invalidValues.Select(v => $"\"{v}\""))}] returned {reply.StatusCode}");
            Assert.Contains("invalid_voice_activation", await reply.Content.ReadAsStringAsync());
            Assert.False(reply.Headers.Contains(NodeVoiceActivationController.ElectionWaitHeader));
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, new { claim.ActivationId, detectionAgeMs = 300 })).StatusCode);
        await Task.Delay(VoiceActivationArbiter.CatchUpWindow);
        using var valid = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(claim) };
        Assert.True(valid.Headers.TryAddWithoutValidation(NodeVoiceActivationController.DetectionAgeHeader, "300"));
        using var granted = await client.SendAsync(valid);
        Assert.Equal(HttpStatusCode.OK, granted.StatusCode);
        Assert.True(granted.Headers.TryGetValues(NodeVoiceActivationController.ElectionWaitHeader, out var values));
        Assert.True(long.TryParse(Assert.Single(values), NumberStyles.None, CultureInfo.InvariantCulture, out var waitedMs));
        // This real HTTP test tolerates scheduler delays; deterministic tests check
        // the exact 350 ms deadline. A header that always reports zero must fail.
        Assert.True(waitedMs >= 300, $"Expected timer wait, reported {waitedMs} ms");
        using var body = JsonDocument.Parse(await granted.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "activationId", "decision" }, body.RootElement.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(claim.ActivationId, body.RootElement.GetProperty("activationId").GetGuid());
        Assert.Equal("granted", body.RootElement.GetProperty("decision").GetString());
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
