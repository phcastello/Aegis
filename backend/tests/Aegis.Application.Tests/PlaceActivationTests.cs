using Aegis.Application.Nodes;
using Aegis.Application.Places;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Persistence;
using Aegis.Infrastructure.Places;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class PlaceActivationTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-10-15T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public readonly Clock Clock = new();
        public readonly AegisDbContext Db = new(new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public PlaceService Service => new(Db, Clock, Options.Create(new LocationContextOptions()));
        public Guid PhoneId;
        public async Task Initialize()
        {
            var phone = new AegisNode(Guid.NewGuid(), "Synthetic activation phone", NodePlatform.Android, "0.7.0-unstable.12", 1, Clock.Now);
            PhoneId = phone.Id; Db.Nodes.Add(phone); Db.NodeCapabilities.Add(new(phone.Id, "geofence.monitor", 1)); await Db.SaveChangesAsync();
            await Service.ConfigureAsync(new(NodeId: phone.Id, Mode: "dynamic"));
        }
        public Task<PlaceView> Place(int regions = 1) => Service.SaveAsync(null, new("Synthetic activation area", "property", null,
            Enumerable.Range(0, regions).Select(i => new PlaceCircle(Guid.NewGuid(), 10, 20 + i * .001, 100)).ToArray(), []));
        public Task<GeographicRuleView> Rule(Guid place, DateTimeOffset? endsAt = null) => Service.SaveRuleAsync(null, new("Synthetic activation task", [place], PhoneId, "exit", true, EndsAt: endsAt));
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    [Theory]
    [InlineData("receive", "location_source_required")]
    [InlineData("disabled", "location_source_required")]
    [InlineData("revoked", "location_source_required")]
    [InlineData("mode", "location_source_required")]
    [InlineData("capability", "geofence_unsupported")]
    public async Task ResumeRevalidatesObserverAndPreservesPausedRevisionWhenRejected(string mutation, string code)
    {
        await using var f = new Fixture(); await f.Initialize(); var place = await f.Place(); var rule = await f.Rule(place.Id);
        var paused = await f.Service.ActOnRuleAsync(rule.Id, rule.Revision, "pause");
        if (mutation == "mode") await f.Service.ConfigureAsync(new(NodeId: f.PhoneId, Mode: "none"));
        else {
            var node = await f.Db.Nodes.SingleAsync();
            if (mutation == "receive") node.SetReceiveReminders(false, f.Clock.Now);
            else if (mutation == "disabled") node.SetEnabled(false, f.Clock.Now);
            else if (mutation == "revoked") node.Revoke(f.Clock.Now);
            else f.Db.NodeCapabilities.RemoveRange(await f.Db.NodeCapabilities.ToArrayAsync());
            await f.Db.SaveChangesAsync();
        }
        Assert.Equal(code, (await Assert.ThrowsAsync<NodeException>(() => f.Service.ActOnRuleAsync(paused.Id, paused.Revision, "resume"))).Code);
        var persisted = await f.Db.Set<GeographicReminderRule>().AsNoTracking().SingleAsync(); Assert.Equal("paused", persisted.Status); Assert.Equal(paused.Revision, persisted.Revision);
    }

    [Theory]
    [InlineData(false, "geofence_rule_limit")]
    [InlineData(true, "geofence_region_limit")]
    public async Task ResumeCannotExceedLimitsAfterOtherRulesWereActivatedDuringPause(bool regionLimit, string code)
    {
        await using var f = new Fixture(); await f.Initialize(); var target = await f.Place(); var rule = await f.Rule(target.Id);
        var paused = await f.Service.ActOnRuleAsync(rule.Id, rule.Revision, "pause");
        var activePlaces = new List<Guid>();
        if (regionLimit) for (var i = 0; i < 4; i++) activePlaces.Add((await f.Place(25)).Id);
        else activePlaces.Add(target.Id);
        for (var i = 0; i < (regionLimit ? 1 : 100); i++)
            f.Db.Set<GeographicReminderRule>().Add(new("Synthetic capacity task", activePlaces, f.PhoneId, "enter", true, null, null, "UTC", f.Clock.Now));
        await f.Db.SaveChangesAsync();
        Assert.Equal(code, (await Assert.ThrowsAsync<NodeException>(() => f.Service.ActOnRuleAsync(paused.Id, paused.Revision, "resume"))).Code);
        var persisted = await f.Db.Set<GeographicReminderRule>().AsNoTracking().SingleAsync(r => r.Id == rule.Id); Assert.Equal("paused", persisted.Status); Assert.Equal(paused.Revision, persisted.Revision);
    }

    [Fact]
    public async Task ExpiredRuleCannotResumeAndValidResumeRequiresFreshNativeConfirmation()
    {
        await using var f = new Fixture(); await f.Initialize(); var place = await f.Place(); var rule = await f.Rule(place.Id, f.Clock.Now.AddHours(1));
        var paused = await f.Service.ActOnRuleAsync(rule.Id, rule.Revision, "pause");
        var resumed = await f.Service.ActOnRuleAsync(rule.Id, paused.Revision, "resume"); Assert.Equal("active", resumed.Status); Assert.Equal("pending_sync", resumed.SyncStatus); Assert.Equal(paused.Revision + 1, resumed.Revision);
        paused = await f.Service.ActOnRuleAsync(rule.Id, resumed.Revision, "pause"); f.Clock.Now += TimeSpan.FromHours(1);
        Assert.Equal("rule_expired", (await Assert.ThrowsAsync<NodeException>(() => f.Service.ActOnRuleAsync(rule.Id, paused.Revision, "resume"))).Code);
        Assert.Equal("paused", (await f.Db.Set<GeographicReminderRule>().AsNoTracking().SingleAsync()).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReferencedPlaceCannotBecomeRoomAndRejectedEditCannotLeakIntoNextSave(bool paused)
    {
        await using var f = new Fixture(); await f.Initialize(); var place = await f.Place(); var rule = await f.Rule(place.Id);
        if (paused) rule = await f.Service.ActOnRuleAsync(rule.Id, rule.Revision, "pause");
        Assert.Equal("indoor_location_unsupported", (await Assert.ThrowsAsync<NodeException>(() => f.Service.SaveAsync(place.Id, new(place.Name, "room", null, place.Circles, [], place.Revision)))).Code);
        var saved = await f.Service.SaveAsync(place.Id, new("Synthetic renamed property", "property", null, place.Circles, [], place.Revision));
        Assert.Equal("property", saved.Type); Assert.Equal(place.Circles, saved.Circles);
        Assert.Equal(rule.Revision + 1, (await f.Db.Set<GeographicReminderRule>().AsNoTracking().SingleAsync()).Revision);
    }
}
