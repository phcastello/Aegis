using Aegis.Application.Nodes;
using Aegis.Application.Places;
using Aegis.Application.Tools;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Nodes;
using Aegis.Infrastructure.Persistence;
using Aegis.Infrastructure.Places;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class PlaceCompletionTests
{
    // All coordinates are synthetic fixtures. These are not the owner's places.
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.Parse("2026-10-15T12:00:00Z"); public override DateTimeOffset GetUtcNow() => Now; }
    private readonly Clock clock = new();
    private readonly AegisDbContext db = new(new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private PlaceService Service => new(db, clock, Options.Create(new LocationContextOptions()));
    private static PlaceCircle Circle(double longitude = 0, double radius = 500) => new(Guid.NewGuid(), 0, longitude, radius);
    private async Task<PlaceView> Place(string name = "Lugar de teste", Guid? parent = null, IReadOnlyList<PlaceCircle>? circles = null, string[]? aliases = null) =>
        await Service.SaveAsync(null, new(name, parent is null ? "property" : "room", parent, circles ?? [Circle()], aliases ?? []));
    private async Task<AegisNode> Phone()
    {
        var node = new AegisNode(Guid.NewGuid(), "Telefone de teste", NodePlatform.Android, "0.7.0-unstable.12", 1, clock.Now);
        db.Nodes.Add(node); db.NodeCapabilities.Add(new(node.Id, "geofence.monitor", 1)); await db.SaveChangesAsync();
        await Service.ConfigureAsync(new(node.Id, node.Id, "dynamic")); return node;
    }
    [Fact] public async Task HierarchyRejectsCycleAndOrphanAndPreservesStableId()
    {
        var parent = await Place(); var room = await Place("Quarto de teste", parent.Id, []);
        Assert.Empty(room.Circles);
        var changed = await Service.SaveAsync(parent.Id, new("Nome novo", "property", null, parent.Circles, ["casa"], parent.Revision));
        Assert.Equal(parent.Id, changed.Id); Assert.Equal(2, changed.Revision);
        Assert.Equal("place_cycle", (await Assert.ThrowsAsync<NodeException>(() => Service.SaveAsync(parent.Id, new("Ciclo", "property", room.Id, parent.Circles, [], changed.Revision)))).Code);
        Assert.Equal("place_in_use", (await Assert.ThrowsAsync<NodeException>(() => Service.DeleteAsync(parent.Id, changed.Revision))).Code);
        await Assert.ThrowsAsync<NodeException>(() => Service.SaveAsync(null, new("Órfão", "room", Guid.NewGuid(), [], [])));
    }
    [Fact] public async Task ReferencedPlaceCannotBeDeletedOrLoseInheritedArea()
    {
        var parent = await Place(); var room = await Place("Sala de teste", parent.Id, []); var phone = await Phone();
        Assert.Equal("indoor_location_unsupported", (await Assert.ThrowsAsync<NodeException>(() => Service.SaveRuleAsync(null, new("Tarefa de teste", [room.Id], phone.Id, "exit")))).Code);
        await Service.SaveRuleAsync(null, new("Tarefa de teste", [parent.Id], phone.Id, "exit"));
        Assert.Equal("place_in_use", (await Assert.ThrowsAsync<NodeException>(() => Service.SaveAsync(parent.Id, new(parent.Name, parent.Type, null, [], [], parent.Revision)))).Code);
    }
    [Fact] public void UnionKeepsPresenceBetweenOverlappingCirclesAndAccuracyIsUncertain()
    {
        var circles = new[] { Circle(0, 500), Circle(.005, 500) };
        Assert.Equal("inside", PlaceGeometry.Classify(circles, 0, .006, 10));
        Assert.Equal("outside", PlaceGeometry.Classify(circles, 0, .02, 10));
        Assert.Equal("uncertain", PlaceGeometry.Classify(circles, 0, .0043, 1000));
        Assert.Equal("uncertain", PlaceGeometry.Classify([circles[0]], 0, .0044, 20));
    }
    private LocationObservationView Observation(double longitude, DateTimeOffset? at = null, double accuracy = 10) => new(Guid.NewGuid(), 0, longitude, accuracy, at ?? clock.Now, clock.Now, "fused", null, (clock.Now - (at ?? clock.Now)).TotalSeconds);
    [Fact] public void PresenceRequiresRealTransitionAndIgnoresRepeatedOldOrStaleObservations()
    {
        var circles = new[] { Circle() };
        var first = GeofencePresenceMachine.Observe(new(), circles, Observation(.02), clock.Now);
        Assert.Equal("outside", first.State.Confirmed); Assert.Null(first.Transition);
        var inside = GeofencePresenceMachine.Observe(first.State, circles, Observation(0, clock.Now.AddSeconds(1)), clock.Now.AddSeconds(1));
        Assert.True(inside.NeedsConfirmation); Assert.Null(inside.Transition);
        var tooSoon = GeofencePresenceMachine.Observe(inside.State, circles, Observation(0, clock.Now.AddSeconds(10)), clock.Now.AddSeconds(10));
        Assert.Null(tooSoon.Transition);
        var confirmed = GeofencePresenceMachine.Observe(tooSoon.State, circles, Observation(0, clock.Now.AddSeconds(31)), clock.Now.AddSeconds(31));
        Assert.Equal("enter", confirmed.Transition);
        Assert.Null(GeofencePresenceMachine.Observe(confirmed.State, circles, Observation(0, clock.Now.AddSeconds(31)), clock.Now.AddSeconds(31)).Transition);
        Assert.Equal(confirmed.State, GeofencePresenceMachine.Observe(confirmed.State, circles, Observation(.02, clock.Now.AddSeconds(20)), clock.Now.AddSeconds(40)).State);
        Assert.Equal(confirmed.State, GeofencePresenceMachine.Observe(confirmed.State, circles, Observation(.02, clock.Now.AddSeconds(50)), clock.Now.AddMinutes(10)).State);
    }
    [Fact] public async Task HomeAliasUsesSelectedPhoneAndExplicitNameWinsOverRdp()
    {
        var a = await Place("Casa SP fictícia", circles: [Circle()], aliases: ["casa"]);
        var b = await Place("Casa Guarapuava fictícia", circles: [Circle(8)], aliases: ["casa"]);
        var phone = await Phone();
        var pc = new AegisNode(Guid.NewGuid(), "PC remoto de teste", NodePlatform.Windows, "0.7.0-unstable.12", 1, clock.Now); db.Nodes.Add(pc); await db.SaveChangesAsync();
        await Service.ConfigureAsync(new(NodeId: pc.Id, Mode: "fixed", FixedPlaceId: a.Id));
        await Service.ObserveAsync(phone.Id, new(0, 8, 15, clock.Now, "fused"));
        Assert.Equal(b.Id, (await Service.ResolveAsync("casa", false)).PlaceId);
        Assert.Equal(a.Id, (await Service.ResolveAsync(a.Name, false)).PlaceId);
        Assert.Equal(2, (await Service.ResolveAsync("casa", true)).Candidates.Count);
        clock.Now += TimeSpan.FromHours(1);
        Assert.Equal("clarification_required", (await Service.ResolveAsync("casa", false)).Status);
    }
    [Fact] public void ContextRefusesThirdCityFarCandidatesAndCloseCompetingCandidates()
    {
        var a = new PlaceView(Guid.NewGuid(), "Casa A de teste", "property", null, 1, [Circle()], ["casa"]);
        var b = new PlaceView(Guid.NewGuid(), "Casa B de teste", "property", null, 1, [Circle(8)], ["casa"]);
        var options = new LocationContextOptions();
        Assert.Null(PlaceGeometry.ResolveContext([a, b], Observation(4), options));
        Assert.Null(PlaceGeometry.ResolveContext([a, b with { Circles = [Circle(.1)] }], Observation(.05), options));
        Assert.Equal(a.Id, PlaceGeometry.ResolveContext([a, b], Observation(.2), options));
        Assert.Equal(a.Id, PlaceGeometry.ResolveContext([a, b], Observation(0, clock.Now.AddSeconds(-901)), options, clock.Now));
    }
    [Fact] public async Task DynamicObservationCannotOverwriteFixedConfigurationAndOlderReadingCannotReplaceNew()
    {
        var phone = await Phone(); await Service.ObserveAsync(phone.Id, new(0, 1, 15, clock.Now, "fused"));
        await Service.ObserveAsync(phone.Id, new(0, 2, 15, clock.Now.AddMinutes(-1), "fused"));
        Assert.Equal(1d, (await Service.SettingsAsync()).Latest!.Longitude);
        var place = await Place(); await Service.ConfigureAsync(new(NodeId: phone.Id, Mode: "fixed", FixedPlaceId: place.Id));
        await Assert.ThrowsAsync<NodeException>(() => Service.ObserveAsync(phone.Id, new(0, 3, 15, clock.Now, "fused")));
        Assert.Null((await Service.SettingsAsync()).Latest);
    }
    [Fact] public async Task GeofenceSavedIsPendingUntilCorrectManifestAckAndCancelIsPending()
    {
        var phone = await Phone(); var place = await Place();
        var rule = await Service.SaveRuleAsync(null, new("Passar na padaria (texto, não alvo)", [place.Id], phone.Id, "exit"));
        Assert.Equal("pending_sync", rule.SyncStatus);
        var manifest = JsonSerializer.SerializeToElement(await Service.ManifestAsync(phone.Id), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await Service.ConfirmAsync(phone.Id, new(manifest.GetProperty("revision").GetString()!, [new(rule.Id, rule.Revision, "waiting_initial")]));
        Assert.Equal("waiting_initial", (await Service.RulesAsync()).Single().SyncStatus);
        // Installing Android regions and arming a transition are distinct receipts.
        await Service.ConfirmAsync(phone.Id, new(manifest.GetProperty("revision").GetString()!, [new(rule.Id, rule.Revision, "installed")]));
        Assert.Equal("installed", (await Service.RulesAsync()).Single().SyncStatus);
        await Service.ConfirmAsync(phone.Id, new(manifest.GetProperty("revision").GetString()!, [new(rule.Id, rule.Revision, "active")]));
        Assert.Equal("active", (await Service.RulesAsync()).Single().SyncStatus);
        var cancelled = await Service.ActOnRuleAsync(rule.Id, rule.Revision, "cancel");
        Assert.Equal("cancelled", cancelled.Status); Assert.Equal("pending_sync", cancelled.SyncStatus);
        await Service.ConfirmAsync(phone.Id, new(manifest.GetProperty("revision").GetString()!, []));
        Assert.Equal("pending_sync", (await Service.RulesAsync()).Single().SyncStatus);
        var fresh = JsonSerializer.SerializeToElement(await Service.ManifestAsync(phone.Id), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await Service.ConfirmAsync(phone.Id, new(fresh.GetProperty("revision").GetString()!, []));
        Assert.Equal("removed", (await Service.RulesAsync()).Single().SyncStatus);
    }
    [Fact] public async Task GeographicEventIsIdempotentScopedAndOldEventCannotBroadcastLate()
    {
        var phone = await Phone(); var place = await Place(); var rule = await Service.SaveRuleAsync(null, new("Tarefa", [place.Id], phone.Id, "exit", true));
        var request = new GeographicEventWrite(Guid.NewGuid(), Guid.NewGuid(), rule.Id, rule.Revision, "exit", clock.Now.AddSeconds(1), 1, clock.Now.AddSeconds(1)); clock.Now += TimeSpan.FromSeconds(2);
        await Service.EventAsync(phone.Id, request); await Service.EventAsync(phone.Id, request);
        Assert.Single(db.Reminders); Assert.Single(db.Set<GeographicReminderEvent>());
        await Assert.ThrowsAsync<NodeException>(() => Service.EventAsync(Guid.NewGuid(), request));
        clock.Now += TimeSpan.FromHours(1);
        await Service.EventAsync(phone.Id, request with { EventId = Guid.NewGuid(), OccurrenceId = Guid.NewGuid(), Sequence = 2, OccurredAt = request.OccurredAt.AddMinutes(1), PostedAt = request.PostedAt!.Value.AddMinutes(1) });
        Assert.Equal("expired", (await db.Set<GeographicReminderEvent>().OrderByDescending(e => e.Sequence).FirstAsync()).Result);
        Assert.All(await db.Reminders.ToArrayAsync(), r => Assert.True(r.DeliveryUntil < clock.Now));
    }
    [Fact] public async Task GeographicToolsRejectInventedIdsAndNeverReturnPreciseCoordinates()
    {
        var conversation = Guid.NewGuid(); var context = new ToolExecutionContext(conversation, Guid.NewGuid(), "pedido de teste");
        var phone = await Phone(); var place = await Place(aliases: ["casa"]);
        var result = await new PlaceListTool(Service).ExecuteAsync(JsonSerializer.SerializeToElement(new { }), context);
        Assert.True(result.Success); Assert.DoesNotContain("latitude", result.Content, StringComparison.OrdinalIgnoreCase);
        var create = new GeographicReminderCreateTool(Service);
        var invalid = await create.ExecuteAsync(JsonSerializer.SerializeToElement(new { text = "Tarefa", placeIds = new[] { Guid.NewGuid() }, transition = "exit" }), context);
        Assert.False(invalid.Success); Assert.Empty(db.Set<GeographicReminderRule>());
        var valid = await create.ExecuteAsync(JsonSerializer.SerializeToElement(new { text = "Passar na padaria", reference = place.Name, transition = "exit" }), context);
        Assert.True(valid.Success); Assert.Single(db.Set<GeographicReminderRule>());
    }
    [Fact] public async Task RemovingNodeErasesOperationalRegistrationAndPreservesReminderHistory()
    {
        var registry = new NodeRegistry(db, clock); var actor = await Phone();
        var target = new AegisNode(Guid.NewGuid(), "Descartável comprovado de teste", NodePlatform.Windows, "0.7.0-unstable.12", 1, clock.Now);
        db.Nodes.Add(target); db.NodeCredentials.Add(new(target.Id, new byte[32], clock.Now));
        var reminder = new Reminder("Histórico preservado", clock.Now.AddMinutes(1), "UTC", null, clock.Now); db.Reminders.Add(reminder);
        db.Set<ReminderRecipient>().Add(new(reminder.Id, 1, target.Id, clock.Now)); await db.SaveChangesAsync();
        await registry.RemoveAsync(actor.Id, target.Id);
        Assert.DoesNotContain(await registry.ListAsync(actor.Id), n => n.Id == target.Id);
        Assert.False(await db.NodeCredentials.AnyAsync(n => n.NodeId == target.Id)); Assert.Single(db.Reminders);
        Assert.Null((await db.Set<ReminderRecipient>().SingleAsync()).NodeId);
    }
}
