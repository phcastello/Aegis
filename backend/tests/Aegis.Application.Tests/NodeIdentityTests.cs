using System.Security.Cryptography;
using System.Text.Json;
using Aegis.Application.Nodes;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Nodes;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace Aegis.Application.Tests;

public sealed class NodeIdentityTests
{
    public sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private readonly Clock clock = new();
    private readonly AegisDbContext db = new(new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private NodeRegistry Registry => new(db, clock);
    public static PairNodeRequest Request(string code, string platform = "windows") => new(Guid.NewGuid(), code,
        NodeSecrets.Encode(RandomNumberGenerator.GetBytes(32)), "PC Pedro", platform, "0.7.0-stage.4", 1);
    private async Task<(NodeView Node, string Credential)> Paired(Guid? issuer = null)
    {
        var request = Request((await Registry.CreateCodeAsync(issuer)).Code);
        var receipt = await Registry.PairAsync(request);
        return (await Registry.FinalizeAsync(new(request.AttemptId, receipt.Credential)), receipt.Credential);
    }
    [Fact] public async Task IdentityExistsOnlyAfterSecureStorageConfirmation()
    {
        var request = Request((await Registry.CreateCodeAsync(null)).Code);
        var receipt = await Registry.PairAsync(request);
        Assert.Empty(db.Nodes); Assert.Single(db.NodePairingAttempts);
        Assert.False((await Registry.AuthenticateAsync(receipt.Credential)).Authenticated);
        var node = await Registry.FinalizeAsync(new(request.AttemptId, receipt.Credential));
        Assert.Equal(receipt.NodeId, node.Id); Assert.True(node.Enabled); Assert.Equal(1, node.ProtocolVersion);
        Assert.Empty(db.NodePairingAttempts); Assert.True((await Registry.AuthenticateAsync(receipt.Credential)).Authenticated);
        Assert.Equal(32, (await db.NodeCredentials.SingleAsync()).SecretHash.Length);
        Assert.Equal(NodeSecrets.Hash(receipt.Credential), (await db.NodeCredentials.SingleAsync()).SecretHash);
        var json = JsonSerializer.Serialize(await Registry.ListAsync(node.Id));
        Assert.DoesNotContain(receipt.Credential, json); Assert.DoesNotContain("SecretHash", json);
    }
    [Fact] public async Task LostPairAndFinalizeResponsesAreIdempotent()
    {
        var request = Request((await Registry.CreateCodeAsync(null)).Code);
        var first = await Registry.PairAsync(request); var retry = await Registry.PairAsync(request);
        Assert.Equal(first, retry); Assert.Single(db.NodePairingAttempts);
        var node = await Registry.FinalizeAsync(new(request.AttemptId, first.Credential));
        Assert.Equal(node, await Registry.FinalizeAsync(new(request.AttemptId, first.Credential)));
        Assert.Single(db.Nodes); Assert.Single(db.NodeCredentials);
    }
    [Fact] public async Task CodeCannotPairSecondDeviceOrBeUsedAfterFinalize()
    {
        var request = Request((await Registry.CreateCodeAsync(null)).Code);
        var receipt = await Registry.PairAsync(request);
        await Assert.ThrowsAsync<NodeException>(() => Registry.PairAsync(Request(request.Code)));
        await Registry.FinalizeAsync(new(request.AttemptId, receipt.Credential));
        await Assert.ThrowsAsync<NodeException>(() => Registry.PairAsync(request));
        Assert.Single(db.Nodes);
    }
    [Fact] public async Task ExpiredPendingAttemptLeavesNoOrphanNode()
    {
        var request = Request((await Registry.CreateCodeAsync(null)).Code);
        var receipt = await Registry.PairAsync(request); clock.Now += TimeSpan.FromMinutes(11);
        await Assert.ThrowsAsync<NodeException>(() => Registry.FinalizeAsync(new(request.AttemptId, receipt.Credential)));
        await Registry.CleanupAsync(); Assert.Empty(db.Nodes); Assert.Empty(db.NodePairingAttempts); Assert.Empty(db.NodePairingCodes);
    }
    [Fact] public async Task ExpiredCodeIsRejected()
    {
        var code = (await Registry.CreateCodeAsync(null)).Code; clock.Now += TimeSpan.FromMinutes(10);
        await Assert.ThrowsAsync<NodeException>(() => Registry.PairAsync(Request(code)));
    }
    [Fact] public async Task FiveWrongSecretsInvalidateKnownCode()
    {
        var code = (await Registry.CreateCodeAsync(null)).Code;
        var wrong = code[..^1] + (code[^1] == '0' ? '1' : '0');
        for (var i = 0; i < 5; i++) await Assert.ThrowsAsync<NodeException>(() => Registry.PairAsync(Request(wrong)));
        Assert.Equal(5, (await db.NodePairingCodes.SingleAsync()).FailedAttempts);
        await Assert.ThrowsAsync<NodeException>(() => Registry.PairAsync(Request(code))); Assert.Empty(db.Nodes);
    }
    [Fact] public async Task RetryCannotChangeRecoveryKeyOrRegistration()
    {
        var request = Request((await Registry.CreateCodeAsync(null)).Code); await Registry.PairAsync(request);
        await Assert.ThrowsAsync<NodeException>(() => Registry.PairAsync(request with { RecoveryKey = Request(request.Code).RecoveryKey }));
        await Assert.ThrowsAsync<NodeException>(() => Registry.PairAsync(request with { Name = "Different" }));
        await Assert.ThrowsAsync<NodeException>(() => Registry.PairAsync(request with { AppVersion = "0.7.0-stage.5" }));
    }
    [Theory] [InlineData("invalid")] [InlineData("")] [InlineData("0000-0000-0000-0000-0000-0000")]
    public async Task InvalidCodeHasGenericFailure(string code) => Assert.Equal("pairing_unavailable", (await Assert.ThrowsAsync<NodeException>(() => Registry.PairAsync(Request(code)))).Code);
    [Theory] [InlineData("linux")] [InlineData("ios")] [InlineData("macos")]
    public async Task UnsupportedPlatformRejected(string platform)
    { var code = (await Registry.CreateCodeAsync(null)).Code; await Assert.ThrowsAsync<NodeException>(() => Registry.PairAsync(Request(code, platform))); }
    [Fact] public async Task ProtocolAndNameValidated()
    {
        var request = Request((await Registry.CreateCodeAsync(null)).Code);
        await Assert.ThrowsAsync<NodeException>(() => Registry.PairAsync(request with { ProtocolVersion = 2 }));
        await Assert.ThrowsAsync<NodeException>(() => Registry.PairAsync(request with { Name = "\n" }));
    }
    [Fact] public async Task DisabledRetainsCredentialAndCanBeReenabled()
    {
        var pc = await Paired(); var mobile = await Paired(pc.Node.Id);
        await Registry.SetEnabledAsync(pc.Node.Id, mobile.Node.Id, false);
        Assert.Equal("node_disabled", (await Registry.AuthenticateAsync(mobile.Credential)).Error);
        await Assert.ThrowsAsync<NodeException>(() => Registry.ListAsync(mobile.Node.Id));
        await Registry.SetEnabledAsync(pc.Node.Id, mobile.Node.Id, true);
        Assert.True((await Registry.AuthenticateAsync(mobile.Credential)).Authenticated);
    }
    [Fact] public async Task RevokePermanentlyInvalidatesCredentialAndCannotBeReenabled()
    {
        var pc = await Paired(); var mobile = await Paired(pc.Node.Id);
        await Registry.RevokeAsync(pc.Node.Id, mobile.Node.Id);
        Assert.Equal("node_revoked", (await Registry.AuthenticateAsync(mobile.Credential)).Error);
        await Assert.ThrowsAsync<NodeException>(() => Registry.SetEnabledAsync(pc.Node.Id, mobile.Node.Id, true));
        await Assert.ThrowsAsync<NodeException>(() => Registry.RenameAsync(pc.Node.Id, mobile.Node.Id, "Pixel"));
        await Assert.ThrowsAsync<NodeException>(() => Registry.FinalizeAsync(new(Guid.NewGuid(), mobile.Credential)));
    }
    [Fact] public async Task LocalBootstrapRecoversLastDisabledOrRevokedNode()
    {
        var pc = await Paired(); await Registry.SetEnabledAsync(pc.Node.Id, pc.Node.Id, false);
        await Registry.SetEnabledAsync(null, pc.Node.Id, true);
        await Registry.RevokeAsync(pc.Node.Id, pc.Node.Id);
        var replacement = await Paired(); Assert.NotEqual(pc.Node.Id, replacement.Node.Id);
        Assert.True((await Registry.AuthenticateAsync(replacement.Credential)).Authenticated);
    }
    [Fact] public async Task InvalidCredentialNeverDisclosesRevokedOrDisabledState()
    {
        var pc = await Paired(); await Registry.RevokeAsync(pc.Node.Id, pc.Node.Id);
        Assert.Equal("node_authentication_required", (await Registry.AuthenticateAsync(NodeSecrets.NewCredential(pc.Node.Id))).Error);
        Assert.Equal("node_authentication_required", (await Registry.AuthenticateAsync(NodeSecrets.NewCredential(Guid.NewGuid()))).Error);
        Assert.Equal("node_authentication_required", (await Registry.AuthenticateAsync("bad")).Error);
    }
    [Fact] public async Task RenameIsBackendOwnedAndPersistsAcrossRegistryInstances()
    {
        var pc = await Paired(); var mobile = await Paired(pc.Node.Id);
        await Registry.RenameAsync(pc.Node.Id, mobile.Node.Id, " Pixel ");
        Assert.Equal("Pixel", (await Registry.MeAsync(mobile.Node.Id)).Name);
        Assert.Equal(2, (await Registry.ListAsync(pc.Node.Id)).Count);
    }
    [Fact] public async Task IssuerRevokedWhilePairingCannotFinalize()
    {
        var pc = await Paired(); var request = Request((await Registry.CreateCodeAsync(pc.Node.Id)).Code);
        var receipt = await Registry.PairAsync(request); await Registry.RevokeAsync(pc.Node.Id, pc.Node.Id);
        await Assert.ThrowsAsync<NodeException>(() => Registry.FinalizeAsync(new(request.AttemptId, receipt.Credential)));
        Assert.Single(db.Nodes);
    }
    [Fact] public async Task CodesAndPendingReceiptsDoNotContainPlaintextSecrets()
    {
        var code = (await Registry.CreateCodeAsync(null)).Code; var request = Request(code); var receipt = await Registry.PairAsync(request);
        var stored = await db.NodePairingCodes.SingleAsync(); Assert.Equal(32, stored.CodeHash.Length);
        var attempt = await db.NodePairingAttempts.SingleAsync();
        Assert.DoesNotContain(receipt.Credential, System.Text.Encoding.UTF8.GetString(attempt.EncryptedReceipt));
        Assert.Throws<NodeException>(() => NodeSecrets.DecryptReceipt(attempt.EncryptedReceipt, RandomNumberGenerator.GetBytes(32), request.AttemptId));
    }
    [Fact] public async Task IssuerCannotCreateUnlimitedOutstandingCodes()
    {
        var pc = await Paired(); for (var i = 0; i < 10; i++) await Registry.CreateCodeAsync(pc.Node.Id);
        Assert.Equal(429, (await Assert.ThrowsAsync<NodeException>(() => Registry.CreateCodeAsync(pc.Node.Id))).Status);
    }
}
