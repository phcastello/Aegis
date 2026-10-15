using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aegis.Api.Controllers;
using Aegis.Api.Nodes;
using Aegis.Application.Nodes;
using Aegis.Application.Places;
using Aegis.Application.Reminders;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Nodes;
using Aegis.Infrastructure.Persistence;
using Aegis.Infrastructure.Places;
using Aegis.Infrastructure.Reminders;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aegis.Application.Tests;

// TestServer executes production authorization, controllers and domain services.
// No native credentials belonging to the owner, public probes or external map services.
public sealed class PlaceApiTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-10-15T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Signals : INodeDomainSignals
    {
        public Guid? LastNode; public string? LastCapability;
        public Task<string> SendAsync(Guid nodeId, string capability, object input, CancellationToken ct = default)
        { LastNode = nodeId; LastCapability = capability; return Task.FromResult("unavailable"); }
    }
    private sealed class Host : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required HttpClient Client { get; init; }
        public required DbContextOptions<AegisDbContext> DbOptions { get; init; }
        public required Clock Clock { get; init; }
        public required Signals Signals { get; init; }
        public AegisDbContext Db() => new(DbOptions);
        public static async Task<Host> Create()
        {
            var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
            var database = Guid.NewGuid().ToString();
            var options = new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(database).Options;
            var clock = new Clock(); var signals = new Signals();
            builder.Services.AddControllers().AddApplicationPart(typeof(PlacesController).Assembly);
            builder.Services.AddNodeApi(); builder.Services.AddSingleton<TimeProvider>(clock);
            builder.Services.AddDbContext<AegisDbContext>(o => o.UseInMemoryDatabase(database));
            builder.Services.AddScoped<INodeRegistry, NodeRegistry>(); builder.Services.AddSingleton<INodeDomainSignals>(signals);
            builder.Services.AddOptions<LocationContextOptions>(); builder.Services.AddOptions<PlaceMapOptions>();
            builder.Services.AddScoped<IPlaceService, PlaceService>(); builder.Services.AddScoped<PlaceMaps>();
            builder.Services.AddScoped<ReminderStore>(); builder.Services.AddScoped<IReminderStore>(p => p.GetRequiredService<ReminderStore>());
            builder.Services.AddScoped<ReminderSeriesStore>(); builder.Services.AddScoped<IReminderSeriesStore>(p => p.GetRequiredService<ReminderSeriesStore>());
            builder.Services.AddScoped<ReminderSeriesService>(); builder.Services.AddScoped<ReminderActionService>();
            builder.Services.AddScoped<IReminderActionStore, ReminderActionStore>(); builder.Services.AddScoped<IReminderActionTokens, ReminderActionTokens>();
            var app = builder.Build(); app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter(); app.MapControllers(); await app.StartAsync();
            return new Host { App = app, Client = app.GetTestClient(), DbOptions = options, Clock = clock, Signals = signals };
        }
        public async Task<(Guid Id, string Credential)> Pair(string platform = "android", bool geofenceSupport = true)
        {
            await using var db = Db(); var registry = new NodeRegistry(db, Clock);
            var request = NodeIdentityTests.Request((await registry.CreateCodeAsync(null)).Code, platform);
            var receipt = await registry.PairAsync(request); var node = await registry.FinalizeAsync(new(request.AttemptId, receipt.Credential));
            db.NodeCapabilities.Add(new(node.Id, "notification.show", 1));
            if (platform == "android" && geofenceSupport) { db.NodeCapabilities.Add(new(node.Id, "geofence.monitor", 1)); db.NodeCapabilities.Add(new(node.Id, "location.current", 1)); }
            await db.SaveChangesAsync(); return (node.Id, receipt.Credential);
        }
        public void Authenticate(string credential) { Client.DefaultRequestHeaders.Remove("Authorization"); Client.DefaultRequestHeaders.Add("Authorization", "AegisNode " + credential); }
        public async ValueTask DisposeAsync() { Client.Dispose(); await App.DisposeAsync(); }
    }
    private static PlaceWrite Place(string name = "Synthetic API place", int count = 1, int seed = 0) => new(name, "property", null,
        Enumerable.Range(0, count).Select(i => new PlaceCircle(Guid.NewGuid(), 10 + (seed + i) * .000123456789, 20 + (seed + i) * .000987654321, 123.123456789 + i)).ToArray(), []);
    private static async Task<JsonElement> Read(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    { Assert.Equal(expected, response.StatusCode); return (await response.Content.ReadFromJsonAsync<JsonElement>()); }

    [Fact]
    public async Task AnonymousNativeRoutesRejectManagementLocationManifestAndInteractionsWithoutMutatingState()
    {
        await using var host = await Host.Create(); var id = Guid.NewGuid();
        var calls = new[] {
            new HttpRequestMessage(HttpMethod.Get, "/api/nodes/places"),
            new HttpRequestMessage(HttpMethod.Post, "/api/nodes/places") { Content = JsonContent.Create(Place()) },
            new HttpRequestMessage(HttpMethod.Get, "/api/nodes/reminders"),
            new HttpRequestMessage(HttpMethod.Post, "/api/nodes/reminders/reconcile") { Content = JsonContent.Create(new { items = new[] { new { reminderId = id, revision = 1, generation = 1 } } }) },
            new HttpRequestMessage(HttpMethod.Post, "/api/nodes/reminders") { Content = JsonContent.Create(new { text = "Synthetic", dueAt = "2026-10-15T13:00:00Z" }) },
            new HttpRequestMessage(HttpMethod.Post, $"/api/nodes/reminders/{id}/actions") { Content = JsonContent.Create(new ReminderActionRequest(Guid.NewGuid(), 1, 1, "acknowledge")) },
            new HttpRequestMessage(HttpMethod.Get, "/api/nodes/reminder-series"),
            new HttpRequestMessage(HttpMethod.Get, "/api/nodes/location/settings"),
            new HttpRequestMessage(HttpMethod.Get, "/api/nodes/location/current"),
            new HttpRequestMessage(HttpMethod.Post, "/api/nodes/location/current"),
            new HttpRequestMessage(HttpMethod.Post, "/api/nodes/location/observations") { Content = JsonContent.Create(new LocationObservationWrite(10, 20, 10, host.Clock.Now, "fused")) },
            new HttpRequestMessage(HttpMethod.Get, "/api/nodes/geofences"),
            new HttpRequestMessage(HttpMethod.Get, "/api/nodes/geofences/manifest"),
            new HttpRequestMessage(HttpMethod.Post, "/api/nodes/geofences/events") { Content = JsonContent.Create(new GeographicEventWrite(Guid.NewGuid(), Guid.NewGuid(), id, 1, "exit", host.Clock.Now, 1)) },
            new HttpRequestMessage(HttpMethod.Post, "/api/nodes/geofences/sync") { Content = JsonContent.Create(new GeographicSyncWrite("fixture", [])) }
        };
        foreach (var request in calls) {
            using (request) using (var response = await host.Client.SendAsync(request)) {
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
            }
        }
        var scopedWithoutProof = await host.Client.PostAsJsonAsync($"/api/reminder-actions/{id}", new { token = "not-valid-proof", actionId = Guid.NewGuid(), expectedRevision = 1, generation = 1, action = "acknowledge" });
        Assert.Equal(HttpStatusCode.NotFound, scopedWithoutProof.StatusCode);
        await using var db = host.Db(); Assert.Empty(db.Reminders); Assert.Empty(db.Set<Place>()); Assert.Empty(db.Set<GeographicReminderRule>()); Assert.Empty(db.Set<GeographicReminderEvent>()); Assert.Empty(db.Set<ReminderActionReceipt>());
    }

    [Fact]
    public async Task NativeCredentialCanCreateRulesFetchLargeManifestAndAcknowledgeOnlyItsOwnInstallation()
    {
        await using var host = await Host.Create(); var phone = await host.Pair(); var other = await host.Pair(); host.Authenticate(phone.Credential);
        await Read(await host.Client.PutAsJsonAsync("/api/nodes/location/settings", new LocationSettingsWrite(phone.Id, phone.Id, "dynamic")));
        var ids = new List<Guid>();
        for (var i = 0; i < 4; i++) { var place = await Read(await host.Client.PostAsJsonAsync("/api/nodes/places", Place("Synthetic region " + i, 25, i * 25))); ids.Add(place.GetProperty("id").GetGuid()); }
        var saved = await Read(await host.Client.PostAsJsonAsync("/api/nodes/geofences", new GeographicRuleWrite(new string('a', 500), ids, phone.Id, "exit", true)));
        Assert.Equal("pending_sync", saved.GetProperty("syncStatus").GetString());
        var manifestResponse = await host.Client.GetAsync("/api/nodes/geofences/manifest"); Assert.Equal(HttpStatusCode.OK, manifestResponse.StatusCode);
        var manifestText = await manifestResponse.Content.ReadAsStringAsync(); Assert.True(System.Text.Encoding.UTF8.GetByteCount(manifestText) > 10 * 1024); Assert.DoesNotContain(phone.Credential, manifestText);
        var manifest = JsonSerializer.Deserialize<JsonElement>(manifestText); Assert.Equal(phone.Id, manifest.GetProperty("observerNodeId").GetGuid()); Assert.Equal(4, manifest.GetProperty("places").GetArrayLength());
        Assert.Equal(100, manifest.GetProperty("places").EnumerateArray().Sum(p => p.GetProperty("circles").GetArrayLength()));
        var id = saved.GetProperty("id").GetGuid(); var revision = saved.GetProperty("revision").GetInt32();
        var confirmation = new GeographicSyncWrite(manifest.GetProperty("revision").GetString()!, [new(id, revision, "waiting_initial")]);
        await Read(await host.Client.PostAsJsonAsync("/api/nodes/geofences/sync", confirmation));
        host.Authenticate(other.Credential);
        var foreign = await Read(await host.Client.PostAsJsonAsync("/api/nodes/geofences/sync", confirmation)); Assert.False(foreign.GetProperty("synchronized").GetBoolean());
        var foreignEvent = await host.Client.PostAsJsonAsync("/api/nodes/geofences/events", new GeographicEventWrite(Guid.NewGuid(), Guid.NewGuid(), id, revision, "exit", host.Clock.Now.AddSeconds(1), 1)); Assert.Equal(HttpStatusCode.Forbidden, foreignEvent.StatusCode);
        host.Authenticate(phone.Credential);
        var rules = await host.Client.GetFromJsonAsync<JsonElement>("/api/nodes/geofences"); Assert.Equal("waiting_initial", rules[0].GetProperty("syncStatus").GetString());
        Assert.Equal(phone.Id, host.Signals.LastNode); Assert.Equal("geofence.monitor", host.Signals.LastCapability);
    }

    [Fact]
    public async Task WindowsCannotForgeObservationAndAndroidWithoutImplementedGeofenceCannotCreateRule()
    {
        await using var host = await Host.Create(); var windows = await host.Pair("windows"); var phone = await host.Pair(geofenceSupport: false);
        host.Authenticate(windows.Credential);
        var forged = await host.Client.PostAsJsonAsync("/api/nodes/location/observations", new LocationObservationWrite(10, 20, 10, host.Clock.Now, "fused")); Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        await Read(await host.Client.PutAsJsonAsync("/api/nodes/location/settings", new LocationSettingsWrite(phone.Id, phone.Id, "dynamic")));
        var place = await Read(await host.Client.PostAsJsonAsync("/api/nodes/places", Place()));
        var rejected = await Read(await host.Client.PostAsJsonAsync("/api/nodes/geofences", new GeographicRuleWrite("Synthetic task", [place.GetProperty("id").GetGuid()], phone.Id, "enter")), HttpStatusCode.Conflict);
        Assert.Equal("geofence_unsupported", rejected.GetProperty("code").GetString());
        var refresh = await Read(await host.Client.PostAsync("/api/nodes/location/current", null)); Assert.Equal("source_unreachable", refresh.GetProperty("status").GetString());
        Assert.Equal(phone.Id, host.Signals.LastNode); Assert.Equal("location.current", host.Signals.LastCapability);
        await using var db = host.Db(); Assert.Empty(db.Set<GeographicReminderRule>()); Assert.DoesNotContain(await db.Set<NodeLocationProfile>().ToArrayAsync(), p => p.NodeId == windows.Id && p.Latitude is not null);
    }
    [Fact]
    public async Task ReconnectInventoryWithdrawsAcknowledgedCancelledAndSupersededCopiesButKeepsUncertainPosts()
    {
        await using var host = await Host.Create(); var phone = await host.Pair(); host.Authenticate(phone.Credential);
        var now = host.Clock.Now;
        var active = new Reminder("Synthetic active", now.AddHours(1), "UTC", null, now);
        var acknowledged = new Reminder("Synthetic acknowledged", now.AddHours(1), "UTC", null, now);
        var snoozed = new Reminder("Synthetic snoozed", now.AddHours(1), "UTC", null, now);
        var failed = new Reminder("Synthetic unknown outcome", now.AddHours(1), "UTC", null, now);
        var cancelled = new Reminder("Synthetic cancelled", now.AddHours(2), "UTC", null, now);
        await using (var seed = host.Db()) {
            seed.Reminders.AddRange(active, acknowledged, snoozed, failed, cancelled); await seed.SaveChangesAsync();
            host.Clock.Now += TimeSpan.FromHours(1);
            foreach (var r in new[] { active, acknowledged, snoozed, failed }) { r.Claim(Guid.NewGuid(), host.Clock.Now); r.BeginTrigger(host.Clock.Now); r.Finish(null, r != failed, host.Clock.Now); }
            acknowledged.Acknowledge(host.Clock.Now); snoozed.Snooze(host.Clock.Now, 10); cancelled.Cancel(host.Clock.Now); await seed.SaveChangesAsync();
        }
        var missing = Guid.NewGuid();
        var request = new { items = new[] { active, acknowledged, snoozed, failed, cancelled }.Select(r => new { reminderId = r.Id, revision = 1, generation = 1 }).Append(new { reminderId = missing, revision = 1, generation = 1 }).ToArray() };
        var response = await Read(await host.Client.PostAsJsonAsync("/api/nodes/reminders/reconcile", request));
        var removed = response.GetProperty("withdraw").EnumerateArray().Select(v => v.GetProperty("reminderId").GetGuid()).ToHashSet();
        Assert.Equal(4, removed.Count); Assert.Contains(acknowledged.Id, removed); Assert.Contains(cancelled.Id, removed); Assert.Contains(snoozed.Id, removed); Assert.Contains(missing, removed); Assert.DoesNotContain(active.Id, removed); Assert.DoesNotContain(failed.Id, removed);
        Assert.All(response.GetProperty("withdraw").EnumerateArray(), p => Assert.Equal(1, p.GetProperty("generation").GetInt32()));
        var ahead = await Read(await host.Client.PostAsJsonAsync("/api/nodes/reminders/reconcile", new { items = new[] { new { reminderId = acknowledged.Id, revision = 99, generation = 2 } } })); Assert.Empty(ahead.GetProperty("withdraw").EnumerateArray());
        await using var db = host.Db(); Assert.Empty(db.Set<ReminderActionReceipt>()); Assert.Null((await db.Reminders.SingleAsync(r => r.Id == active.Id)).AcknowledgedAt);
    }

    [Fact]
    public async Task ReconnectInventoryRejectsUnboundedDuplicateAndInvalidReferences()
    {
        await using var host = await Host.Create(); var phone = await host.Pair(); host.Authenticate(phone.Credential);
        var many = Enumerable.Range(0, 101).Select(_ => new { reminderId = Guid.NewGuid(), revision = 1, generation = 1 }).ToArray();
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PostAsJsonAsync("/api/nodes/reminders/reconcile", new { items = many })).StatusCode);
        var id = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PostAsJsonAsync("/api/nodes/reminders/reconcile", new { items = new[] { new { reminderId = id, revision = 1, generation = 1 }, new { reminderId = id, revision = 2, generation = 2 } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PostAsJsonAsync("/api/nodes/reminders/reconcile", new { items = new[] { new { reminderId = Guid.Empty, revision = 0, generation = 0 } } })).StatusCode);
        var empty = await Read(await host.Client.PostAsJsonAsync("/api/nodes/reminders/reconcile", new { items = Array.Empty<object>() })); Assert.Empty(empty.GetProperty("withdraw").EnumerateArray());
    }

}
