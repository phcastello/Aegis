using System.Text.Json;
using Aegis.Application.Nodes;
using Aegis.Application.Places;
using Aegis.Application.Tools;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Persistence;
using Aegis.Infrastructure.Places;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class HybridMonitoringEnvelopeTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-19T12:00:00Z");
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private readonly AegisDbContext db = new(new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private PlaceService Service => new(db, new Clock(), Options.Create(new LocationContextOptions()));
    private static PlaceCircle Circle(double longitude = 0, double radius = 50) => new(Guid.NewGuid(), 0, longitude, radius);
    private async Task<PlaceView> Place(IReadOnlyList<PlaceCircle>? circles = null) => await Service.SaveAsync(null, new("Casa de teste", "property", null, circles ?? [Circle()], []));
    private Task<PlaceView> Approve(PlaceView place, double padding = 150) => Service.SaveAsync(place.Id,
        new(place.Name, place.Type, place.ParentId, place.Circles, place.Aliases, place.Revision, new(true, padding, true, place.MonitoringEnvelope!.Revision)));
    private async Task<Guid> Phone()
    {
        var node = new AegisNode(Guid.NewGuid(), "Celular de teste", NodePlatform.Android, "0.7.0-unstable.18", 1, Now);
        db.Nodes.Add(node); db.NodeCapabilities.Add(new(node.Id, "geofence.monitor", 1)); await db.SaveChangesAsync();
        await Service.ConfigureAsync(new(node.Id, node.Id, "dynamic")); return node.Id;
    }
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Fact] public async Task LegacyGeometryStaysRealUntilExplicitIndependentApproval()
    {
        var place = await Place();
        Assert.False(place.MonitoringEnvelope!.Enabled); Assert.True(place.MonitoringEnvelope.ApprovalRequired); Assert.Empty(place.MonitoringEnvelope.Circles);
        Assert.Equal(150, place.MonitoringEnvelope.PaddingMeters);
        var accepted = await Approve(place);
        Assert.Equal(place.Revision, accepted.Revision); Assert.Equal(place.Circles, accepted.Circles); Assert.Equal(place.Id, accepted.Id);
        var operational = Assert.Single(accepted.MonitoringEnvelope!.Circles);
        Assert.Equal(200, operational.RadiusMeters); Assert.NotEqual(place.Circles[0].Id, operational.Id);
        Assert.Equal(1, accepted.MonitoringEnvelope.Revision); Assert.False(accepted.MonitoringEnvelope.ApprovalRequired);
        var resized = await Approve(accepted, 175);
        Assert.Equal(operational.Id, Assert.Single(resized.MonitoringEnvelope!.Circles).Id);
        Assert.Equal(place.Circles[0].Id, resized.Circles[0].Id); Assert.Equal(place.Revision, resized.Revision);
    }
    [Fact] public async Task ConfirmationAndEnvelopeRevisionAreRequiredAndGeometryChangeInvalidatesApproval()
    {
        var place = await Place();
        Assert.Equal("monitoring_approval_required", (await Assert.ThrowsAsync<NodeException>(() => Service.SaveAsync(place.Id,
            new(place.Name, place.Type, null, place.Circles, [], place.Revision, new(true, 150, false))))).Code);
        place = await Approve(place);
        Assert.Equal("revision_conflict", (await Assert.ThrowsAsync<NodeException>(() => Service.SaveAsync(place.Id,
            new(place.Name, place.Type, null, place.Circles, [], place.Revision, new(true, 150, true, 0))))).Code);
        var changed = await Service.SaveAsync(place.Id, new(place.Name, place.Type, null, [place.Circles[0] with { RadiusMeters = 65 }], [], place.Revision));
        Assert.Equal(65, changed.Circles[0].RadiusMeters); Assert.True(changed.MonitoringEnvelope!.ApprovalRequired); Assert.Empty(changed.MonitoringEnvelope.Circles);
        Assert.Equal(1, changed.MonitoringEnvelope.Revision); Assert.Equal(2, changed.Revision);
        var reapproved = await Approve(changed, 135);
        Assert.Equal(200, Assert.Single(reapproved.MonitoringEnvelope!.Circles).RadiusMeters); Assert.Equal(2, reapproved.Revision);
    }
    [Fact] public async Task CompoundUnionHasIndependentStableRegionsAndPreservesRealGaps()
    {
        var place = await Place([Circle(), Circle(.01, 65)]); var accepted = await Approve(place);
        Assert.Equal(2, accepted.MonitoringEnvelope!.Circles.Count); Assert.Equal(4, accepted.Circles.Concat(accepted.MonitoringEnvelope.Circles).Select(c => c.Id).Distinct().Count());
        Assert.Equal("outside", PlaceGeometry.Classify(accepted.Circles, 0, .005, 13));
        Assert.Equal(2, accepted.Circles.Count); Assert.Equal(65, accepted.Circles[1].RadiusMeters);
        Assert.Equal(135, MonitoringEnvelopeGeometry.SuggestedPadding([Circle(radius: 65)]));
        Assert.Equal(75, MonitoringEnvelopeGeometry.SuggestedPadding([Circle(radius: 500)]));
    }
    [Fact] public async Task EnvelopeOnlyChangesManifestWithoutResettingRuleRevisionAndSharedRegionsCountOnce()
    {
        var phone = await Phone(); var place = await Place();
        var rule = await Service.SaveRuleAsync(null, new("Tarefa A", [place.Id], phone, "enter"));
        var before = Json(await Service.ManifestAsync(phone));
        await Service.ConfirmAsync(phone, new(before.GetProperty("revision").GetString()!, [new(rule.Id, rule.Revision, "active")]));
        var approved = await Approve(place);
        var after = Json(await Service.ManifestAsync(phone));
        Assert.NotEqual(before.GetProperty("revision").GetString(), after.GetProperty("revision").GetString());
        Assert.Equal(rule.Revision, (await Service.RulesAsync()).Single().Revision);
        Assert.Equal("pending_sync", (await Service.RulesAsync()).Single().SyncStatus);
        for (var i = 0; i < 20; i++) await Service.SaveRuleAsync(null, new("Outra tarefa", [place.Id], phone, "exit"));
        Assert.Single(after.GetProperty("places").EnumerateArray());
        Assert.Equal(1, after.GetProperty("places")[0].GetProperty("monitoringEnvelope").GetProperty("circles").GetArrayLength());
    }
    [Fact] public async Task CapacityCountsBothPhysicalSetsAndRejectsExcessInsteadOfDroppingRegions()
    {
        var phone = await Phone(); var ids = new List<Guid>();
        for (var i = 0; i < 2; i++) {
            var place = await Place(Enumerable.Range(0, 25).Select(index => Circle(index * .01)).ToArray());
            place = await Approve(place); ids.Add(place.Id);
        }
        await Service.SaveRuleAsync(null, new("Cem regiões compartilhadas", ids, phone, "enter"));
        var extra = await Approve(await Place());
        Assert.Equal("geofence_region_limit", (await Assert.ThrowsAsync<NodeException>(() => Service.SaveRuleAsync(null, new("Excesso", [extra.Id], phone, "exit")))).Code);
    }
    [Fact] public async Task OperationalGeometryNeverAppearsAsPersonalPlaceContextForTools()
    {
        await Approve(await Place()); var context = new ToolExecutionContext(Guid.NewGuid(), Guid.NewGuid(), "teste");
        var result = await new PlaceListTool(Service).ExecuteAsync(Json(new { }), context);
        Assert.DoesNotContain("monitoringEnvelope", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("paddingMeters", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("latitude", result.Content, StringComparison.OrdinalIgnoreCase);
    }
    [Fact] public async Task FailedNativeReplacementCannotAcknowledgeCancellationRemoval()
    {
        var phone = await Phone(); var place = await Place(); var rule = await Service.SaveRuleAsync(null, new("Tarefa", [place.Id], phone, "exit"));
        await Service.ActOnRuleAsync(rule.Id, rule.Revision, "cancel");
        var manifest = Json(await Service.ManifestAsync(phone)).GetProperty("revision").GetString()!;
        await Service.ConfirmAsync(phone, new(manifest, [], InstallationComplete: false));
        Assert.Equal("pending_sync", (await Service.RulesAsync()).Single().SyncStatus);
        await Service.ConfirmAsync(phone, new(manifest, [], InstallationComplete: true));
        Assert.Equal("removed", (await Service.RulesAsync()).Single().SyncStatus);
    }
    [Fact] public async Task InheritedRealGeometryKeepsOwnOperationalApprovalWithoutCopyingParentCircles()
    {
        var parent = await Place();
        var child = await Service.SaveAsync(null, new("Área externa herdada", "outdoor", parent.Id, [], []));
        Assert.Empty(child.Circles);
        child = await Approve(child);
        Assert.Empty(child.Circles); Assert.Equal(200, Assert.Single(child.MonitoringEnvelope!.Circles).RadiusMeters);
        var listed = (await Service.ListAsync()).Single(place => place.Id == child.Id);
        Assert.Empty(listed.Circles); Assert.False(listed.MonitoringEnvelope!.ApprovalRequired);
        Assert.Equal(200, Assert.Single(listed.MonitoringEnvelope.Circles).RadiusMeters);
    }
    [Fact] public async Task OperationalCircleOverNativeRadiusLimitIsRejectedWithoutChangingRealArea()
    {
        var place = await Place([Circle(radius: 100000)]);
        Assert.Equal("monitoring_region_too_large", (await Assert.ThrowsAsync<NodeException>(() => Approve(place)) ).Code);
        var retained = Assert.Single(await Service.ListAsync());
        Assert.Equal(place.Circles, retained.Circles); Assert.False(retained.MonitoringEnvelope!.Enabled);
    }

}
