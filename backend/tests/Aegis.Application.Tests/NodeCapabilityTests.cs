using Aegis.Application.Nodes;
using Aegis.Api.Nodes.Transport;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Nodes;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
namespace Aegis.Application.Tests;
public sealed class NodeCapabilityTests
{
    private static readonly NodeCapability Output = new("audio.output", 1), Input = new("audio.input", 1);
    [Fact] public void CatalogIgnoresUnknownButRetainsKnownInStableOrder()
    {
        var accepted = NodeCapabilityCatalog.Validate([Output, new("future.feature", 3), Input], out var ignored);
        Assert.Equal(1, ignored); Assert.Equal(new[] { Input, Output }, accepted);
        Assert.False(NodeCapabilityCatalog.IsKnown("notification.show"));
    }
    [Theory] [InlineData("audio.output", 0)] [InlineData("audio.output", -1)] [InlineData("Audio.Output", 1)]
    [InlineData("audio", 1)] [InlineData("audio..input", 1)] [InlineData("audio.output\n", 1)]
    public void InvalidCapabilitiesRejected(string name, int version) => Assert.Throws<NodeException>(() => NodeCapabilityCatalog.Validate([new(name, version)], out _));
    [Fact] public void OversizedAndDuplicateSetsRejected()
    {
        Assert.Throws<NodeException>(() => NodeCapabilityCatalog.Validate([new(new string('a', 65), 1)], out _));
        Assert.Throws<NodeException>(() => NodeCapabilityCatalog.Validate([Output, Output], out _));
        Assert.Throws<NodeException>(() => NodeCapabilityCatalog.Validate([Output, Output with { Version = 2 }], out _));
        Assert.Throws<NodeException>(() => NodeCapabilityCatalog.Validate(Enumerable.Range(0, 33).Select(i => new NodeCapability("future.x" + i, 1)), out _));
    }
    private sealed class Fixture : IDisposable
    {
        public AegisDbContext Db { get; } = new(new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public NodeConnectionRegistry Live { get; } = new(TimeProvider.System, Options.Create(new NodeTransportOptions()), NullLogger<NodeConnectionRegistry>.Instance);
        public NodeRegistry Nodes { get; }
        public NodeTargetResolver Resolver { get; }
        public Guid Actor { get; } = Guid.NewGuid();
        public Guid A { get; } = Guid.Parse("00000000-0000-0000-0000-000000000010");
        public Guid B { get; } = Guid.Parse("00000000-0000-0000-0000-000000000020");
        public Fixture(bool bOlder = false)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var id in new[] { Actor, A, B }) { Db.Nodes.Add(new(id, id == A ? "A" : "B", id == A ? NodePlatform.Android : NodePlatform.Windows, "0.7.0-stage.7", 1, id == B && bOlder ? now.AddMinutes(-1) : now)); Db.NodeCredentials.Add(new(id, new byte[32], now)); }
            Db.SaveChanges(); Nodes = new(Db, TimeProvider.System, Live); Resolver = new(Nodes, Live);
        }
        public Task<NodeTargetResult> Resolve(int version = 1, Guid? preferred = null, bool multiple = false) => Resolver.ResolveAsync(Actor,
            new(multiple ? [new("audio.input", 1), new("audio.output", version)] : [new("audio.output", version)], preferred));
        public void Dispose() { Live.Dispose(); Db.Dispose(); }
    }
    [Theory]
    [InlineData("capability")] [InlineData("offline")] [InlineData("disabled")] [InlineData("revoked")]
    [InlineData("priority")] [InlineData("tie")] [InlineData("preferred")] [InlineData("preferred_offline")]
    [InlineData("none")] [InlineData("version")] [InlineData("version_higher")] [InlineData("multiple")]
    [InlineData("preferred_disabled")] [InlineData("preferred_revoked")] [InlineData("preferred_missing_capability")]
    public async Task ResolutionHonorsAllFiltersBeforePreferenceAndDeterministicOrdering(string scenario)
    {
        using var f = new Fixture(); f.Live.Register(f.A, capabilities: [Output]); f.Live.Register(f.B, capabilities: [Output]);
        Guid? expected = f.A, preferred = null; var version = 1; var multiple = false;
        switch (scenario)
        {
            case "capability": f.Live.Register(f.B, capabilities: [Input]); break;
            case "offline": f.Live.Disconnect(f.A, "test"); expected = f.B; break;
            case "disabled": await f.Nodes.SetEnabledAsync(f.Actor, f.A, false); expected = f.B; break;
            case "revoked": await f.Nodes.RevokeAsync(f.Actor, f.A); expected = f.B; break;
            case "priority": await f.Nodes.SetTargetPriorityAsync(f.Actor, f.A, 10); await f.Nodes.SetTargetPriorityAsync(f.Actor, f.B, 5); break;
            case "preferred": await f.Nodes.SetTargetPriorityAsync(f.Actor, f.A, 10); preferred = f.B; expected = f.B; break;
            case "preferred_offline": preferred = f.B; f.Live.Disconnect(f.B, "test"); break;
            case "preferred_disabled": preferred = f.B; await f.Nodes.SetEnabledAsync(f.Actor, f.B, false); break;
            case "preferred_revoked": preferred = f.B; await f.Nodes.RevokeAsync(f.Actor, f.B); break;
            case "preferred_missing_capability": preferred = f.B; f.Live.Register(f.B, capabilities: [Input]); break;
            case "none": f.Live.Register(f.A, capabilities: [Input]); f.Live.Register(f.B, capabilities: [Input]); expected = null; break;
            case "version": version = 2; expected = null; break;
            case "version_higher": version = 2; f.Live.Register(f.B, capabilities: [Output with { Version = 3 }]); expected = f.B; break;
            case "multiple": multiple = true; f.Live.Register(f.B, capabilities: [Input, Output]); expected = f.B; break;
        }
        for (var i = 0; i < 5; i++) {
            var result = await f.Resolve(version, preferred, multiple); Assert.Equal(expected, result.Node?.Id);
            Assert.Equal(expected is null ? "no_eligible_node" : null, result.Code);
        }
        // Caller has no automatic preference, and historical capabilities never create presence.
        var callerResult = await f.Resolver.ResolveAsync(scenario is "preferred_disabled" or "preferred_revoked" ? f.Actor : f.B, new([new("audio.output", version)], preferred));
        if (!multiple) Assert.Equal(expected, callerResult.Node?.Id);
    }
    [Fact] public async Task OlderCreationWinsBeforeIdAndPlatformDoesNotBreakTie()
    {
        using var f = new Fixture(bOlder: true); f.Live.Register(f.A, capabilities: [Output]); f.Live.Register(f.B, capabilities: [Output]);
        Assert.Equal(f.B, (await f.Resolve()).Node!.Id);
    }
    [Fact] public async Task SnapshotReplacementPersistsOfflineButLeaseRemainsSourceOfEligibility()
    {
        using var f = new Fixture(); var history = new NodeTransportHistory(f.Db); var now = DateTimeOffset.UtcNow;
        await history.AnnouncedAsync(f.A, "0.7.0-stage.7", now, [Input, Output], default);
        Assert.Equal(new[] { Input, Output }, (await f.Nodes.MeAsync(f.A)).Capabilities);
        Assert.Null((await f.Resolve()).Node); // persisted metadata is not Online
        f.Live.Register(f.A, capabilities: [Input]); Assert.Null((await f.Resolve()).Node); // not persisted Output
        await history.AnnouncedAsync(f.A, "0.7.0-stage.8", now.AddSeconds(1), [Output with { Version = 2 }], default);
        Assert.Equal(new[] { Output with { Version = 2 } }, (await f.Nodes.MeAsync(f.A)).Capabilities);
        await history.AnnouncedAsync(f.A, "0.7.0-stage.6", now.AddSeconds(2), [], default);
        Assert.Empty((await f.Nodes.MeAsync(f.A)).Capabilities); // old hello clears snapshot, not union
    }
    [Fact] public async Task PriorityRangeAndInvalidResolutionAreExplicit()
    {
        using var f = new Fixture();
        foreach (var priority in new[] { -1001, 1001 }) await Assert.ThrowsAsync<NodeException>(() => f.Nodes.SetTargetPriorityAsync(f.Actor, f.A, priority));
        foreach (var priority in new[] { -1000, 0, 1000 }) Assert.Equal(priority, (await f.Nodes.SetTargetPriorityAsync(f.Actor, f.A, priority)).TargetPriority);
        foreach (var requirements in new IReadOnlyList<RequiredCapability>[] { [], [new("unknown.feature", 1)], [new("audio.output", 0)], [new("audio.output", 1), new("audio.output", 2)] })
            await Assert.ThrowsAsync<NodeException>(() => f.Resolver.ResolveAsync(f.Actor, new(requirements)));
    }
    [NodePostgresTests.PostgresFact]
    public Task RealPostgresAtomicSnapshotAndPriority() => NodePostgresTests.WithDatabase(async create => {
        await using var db = create(); var registry = new NodeRegistry(db, TimeProvider.System);
        var request = NodeIdentityTests.Request((await registry.CreateCodeAsync(null)).Code); var receipt = await registry.PairAsync(request);
        var node = await registry.FinalizeAsync(new(request.AttemptId, receipt.Credential)); var history = new NodeTransportHistory(db);
        await history.AnnouncedAsync(node.Id, "0.7.0-stage.7", DateTimeOffset.UtcNow, [Input, Output], default);
        Assert.Equal(2, (await registry.MeAsync(node.Id)).Capabilities.Count);
        await history.AnnouncedAsync(node.Id, "0.7.0-stage.7", DateTimeOffset.UtcNow, [Output with { Version = 2 }], default);
        Assert.Single((await registry.MeAsync(node.Id)).Capabilities);
        Assert.Equal(17, (await registry.SetTargetPriorityAsync(node.Id, node.Id, 17)).TargetPriority);
        await history.AnnouncedAsync(node.Id, "0.7.0-stage.6", DateTimeOffset.UtcNow, [], default); Assert.Empty((await registry.MeAsync(node.Id)).Capabilities);
        Assert.Equal(17, (await registry.MeAsync(node.Id)).TargetPriority);
    });
}
