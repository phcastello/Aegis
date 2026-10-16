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
[Collection("Place map provider")]
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
    private sealed class Geocoder : HttpMessageHandler
    {
        public List<Uri> Requested { get; } = [];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requested.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent("{\"features\":[]}", System.Text.Encoding.UTF8, "application/json") });
        }
    }
    private sealed class Host : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required HttpClient Client { get; init; }
        public required DbContextOptions<AegisDbContext> DbOptions { get; init; }
        public required Clock Clock { get; init; }
        public required Signals Signals { get; init; }
        public AegisDbContext Db() => new(DbOptions);
        public static async Task<Host> Create(Geocoder? geocoder = null)
        {
            var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
            var database = Guid.NewGuid().ToString();
            var options = new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(database).Options;
            var clock = new Clock(); var signals = new Signals();
            builder.Services.AddControllers().AddApplicationPart(typeof(PlacesController).Assembly);
            builder.Services.AddNodeApi(); builder.Services.AddSingleton<TimeProvider>(clock);
            builder.Services.AddDbContext<AegisDbContext>(o => o.UseInMemoryDatabase(database));
            builder.Services.AddScoped<INodeRegistry, NodeRegistry>(); builder.Services.AddSingleton<INodeDomainSignals>(signals);
            builder.Services.AddOptions<LocationContextOptions>();
            builder.Services.AddOptions<PlaceMapOptions>().Configure(o => o.GeocoderUrl = geocoder is null ? "" : "https://synthetic-geocoder.invalid/api/");
            if (geocoder is not null) builder.Services.AddHttpClient("place-geocoder").ConfigurePrimaryHttpMessageHandler(() => geocoder).RemoveAllLoggers();
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
    public async Task WithdrawalReceiptsRequireNativeAuthenticationAndOnlyCloseTheAuthenticatedRecipientsRequiredCopies()
    {
        const string path = "/api/nodes/reminders/withdrawals/receipts";
        await using var host = await Host.Create();
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.PostAsJsonAsync(path, new { items = Array.Empty<object>() })).StatusCode);
        var actor = await host.Pair("windows"); var other = await host.Pair("windows"); host.Authenticate(actor.Credential);
        Guid acknowledgedId, currentId, unrelatedId;
        await using (var db = host.Db())
        {
            var now = host.Clock.Now;
            Reminder Create(bool ack) {
                var reminder = new Reminder("Synthetic receipt scope", now, "UTC", null, now.AddMinutes(-1));
                reminder.Claim(Guid.NewGuid(), now); reminder.BeginTrigger(now); reminder.Finish(null, true, now); if (ack) reminder.Acknowledge(now);
                db.Reminders.Add(reminder); return reminder;
            }
            var acknowledged = Create(true); acknowledgedId = acknowledged.Id;
            var current = Create(false); currentId = current.Id;
            var unrelated = Create(true); unrelatedId = unrelated.Id;
            var accepted = ReminderRecipient.LocalPosted(acknowledged.Id, 1, actor.Id, now); accepted.BeginWithdrawal(now); accepted.QueueWithdrawal(now);
            db.Set<ReminderRecipient>().AddRange(accepted, ReminderRecipient.LocalPosted(current.Id, 1, actor.Id, now), ReminderRecipient.LocalPosted(unrelated.Id, 1, other.Id, now));
            await db.SaveChangesAsync(); Assert.Null(accepted.WithdrawnAt);
        }
        var items = new[] { new { reminderId = acknowledgedId, generation = 1 }, new { reminderId = currentId, generation = 1 }, new { reminderId = unrelatedId, generation = 1 }, new { reminderId = Guid.NewGuid(), generation = 1 } };
        var response = await Read(await host.Client.PostAsJsonAsync(path, new { items }));
        Assert.Equal(acknowledgedId, Assert.Single(response.GetProperty("received").EnumerateArray()).GetProperty("reminderId").GetGuid());
        DateTimeOffset? withdrawn;
        await using (var db = host.Db())
        {
            var recipients = await db.Set<ReminderRecipient>().AsNoTracking().ToArrayAsync();
            withdrawn = recipients.Single(r => r.ReminderId == acknowledgedId).WithdrawnAt; Assert.NotNull(withdrawn);
            Assert.Null(recipients.Single(r => r.ReminderId == currentId).WithdrawnAt); Assert.Null(recipients.Single(r => r.ReminderId == unrelatedId).WithdrawnAt);
        }
        host.Clock.Now = host.Clock.Now.AddMinutes(10); // Retry after a lost HTTP response remains idempotent.
        var replay = await Read(await host.Client.PostAsJsonAsync(path, new { items })); Assert.Single(replay.GetProperty("received").EnumerateArray());
        await using (var db = host.Db()) Assert.Equal(withdrawn, (await db.Set<ReminderRecipient>().AsNoTracking().SingleAsync(r => r.ReminderId == acknowledgedId)).WithdrawnAt);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("paused")]
    [InlineData("older_generation")]
    public async Task WithdrawalReceiptsCloseCancelledPausedAndSupersededCopies(string state)
    {
        await using var host = await Host.Create(); var actor = await host.Pair("windows"); host.Authenticate(actor.Credential); Guid id;
        await using (var db = host.Db())
        {
            var now = host.Clock.Now; var reminder = new Reminder("Synthetic withdrawn state", now.AddMinutes(1), "UTC", null, now);
            if (state == "cancelled") reminder.Cancel(now);
            else if (state == "paused") reminder.PauseFutureForSeries(now);
            else { host.Clock.Now = now.AddMinutes(1); reminder.Claim(Guid.NewGuid(), host.Clock.Now); reminder.BeginTrigger(host.Clock.Now); reminder.Finish(null, true, host.Clock.Now); reminder.Snooze(host.Clock.Now, 10); }
            id = reminder.Id; db.Reminders.Add(reminder); db.Set<ReminderRecipient>().Add(ReminderRecipient.LocalPosted(id, 1, actor.Id, now)); await db.SaveChangesAsync();
        }
        var result = await Read(await host.Client.PostAsJsonAsync("/api/nodes/reminders/withdrawals/receipts", new { items = new[] { new { reminderId = id, generation = 1 } } }));
        Assert.Equal(id, Assert.Single(result.GetProperty("received").EnumerateArray()).GetProperty("reminderId").GetGuid());
        await using var verify = host.Db(); Assert.NotNull((await verify.Set<ReminderRecipient>().SingleAsync()).WithdrawnAt);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"items\":null}")]
    [InlineData("{\"items\":[{\"reminderId\":\"00000000-0000-0000-0000-000000000000\",\"generation\":1}]}")]
    [InlineData("{\"items\":[{\"reminderId\":\"72bd2a5d-b51b-4491-94b4-1f6854ad9306\",\"generation\":0}]}")]
    [InlineData("{\"items\":[],\"nodeId\":\"72bd2a5d-b51b-4491-94b4-1f6854ad9306\"}")]
    public async Task WithdrawalReceiptsRejectMalformedOrClientSelectedScope(string json)
    {
        await using var host = await Host.Create(); var actor = await host.Pair("windows"); host.Authenticate(actor.Credential);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PostAsync("/api/nodes/reminders/withdrawals/receipts", content)).StatusCode);
        await using var db = host.Db(); Assert.Empty(db.Set<ReminderRecipient>());
    }

    [Fact]
    public async Task WithdrawalReceiptBatchIsBoundedAndRejectsDuplicates()
    {
        await using var host = await Host.Create(); var actor = await host.Pair("windows"); host.Authenticate(actor.Credential);
        const string path = "/api/nodes/reminders/withdrawals/receipts";
        var items = Enumerable.Range(0, 100).Select(_ => new { reminderId = Guid.NewGuid(), generation = 1 }).ToArray();
        Assert.Empty((await Read(await host.Client.PostAsJsonAsync(path, new { items }))).GetProperty("received").EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PostAsJsonAsync(path, new { items = items.Append(new { reminderId = Guid.NewGuid(), generation = 1 }) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PostAsJsonAsync(path, new { items = new[] { items[0], items[0] } })).StatusCode);
    }

    [Fact]
    public async Task AddressSearchUsesAuthenticatedPostBodyWithoutQueryAndPreservesPacingAndSafeProviderErrors()
    {
        using var geocoder = new Geocoder(); await using var host = await Host.Create(geocoder);
        const string path = "/api/nodes/places/search";
        var anonymous = await host.Client.PostAsJsonAsync(path, new { query = "Synthetic private address" });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode); Assert.Empty(geocoder.Requested);
        var actor = await host.Pair("windows"); host.Authenticate(actor.Credential);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await host.Client.GetAsync(path + "?query=SyntheticLegacyAddress")).StatusCode);
        try
        {
            await Task.Delay(1050); // Share the production one-second provider limiter with the parser tests.
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(new { query = new string('x', 200) }) };
            var response = await host.Client.SendAsync(request); var result = await Read(response);
            Assert.Equal("", request.RequestUri!.Query); Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
            Assert.True(result.GetProperty("configured").GetBoolean()); Assert.Empty(result.GetProperty("results").EnumerateArray()); Assert.Single(geocoder.Requested);
            var limited = await Read(await host.Client.PostAsJsonAsync(path, new { query = "Synthetic rate-limited address" }), HttpStatusCode.TooManyRequests);
            Assert.Equal("address_search_rate_limited", limited.GetProperty("code").GetString()); Assert.Single(geocoder.Requested);
            await Task.Delay(1050); geocoder.Status = HttpStatusCode.ServiceUnavailable;
            const string privateQuery = "Synthetic confidential address to omit from errors";
            var unavailable = await Read(await host.Client.PostAsJsonAsync(path, new { query = privateQuery }), HttpStatusCode.ServiceUnavailable);
            Assert.Equal("address_search_unavailable", unavailable.GetProperty("code").GetString()); Assert.DoesNotContain(privateQuery, unavailable.GetRawText()); Assert.Equal(2, geocoder.Requested.Count);
        }
        finally { await Task.Delay(1050); }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"query\":null}")]
    [InlineData("{\"query\":42}")]
    [InlineData("{\"query\":\"\"}")]
    [InlineData("{\"query\":\"   \"}")]
    [InlineData("{\"query\":\"Synthetic\\nprivate address\"}")]
    public async Task AddressSearchRejectsMissingWrongTypeAndUnsafeInputWithoutEchoingIt(string json)
    {
        await using var host = await Host.Create(); var actor = await host.Pair("windows"); host.Authenticate(actor.Credential);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var response = await host.Client.PostAsync("/api/nodes/places/search", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.DoesNotContain("Synthetic", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AddressSearchRejectsOverlongBodyQueryAndDoesNotReadLegacyQueryParametersAsInput()
    {
        await using var host = await Host.Create(); var actor = await host.Pair("windows"); host.Authenticate(actor.Credential);
        var query = new string('x', 201); var response = await host.Client.PostAsJsonAsync("/api/nodes/places/search", new { query });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.DoesNotContain(query, await response.Content.ReadAsStringAsync());
        var legacyOnly = await host.Client.PostAsJsonAsync("/api/nodes/places/search?query=SyntheticLegacyAddress", new { });
        Assert.Equal(HttpStatusCode.BadRequest, legacyOnly.StatusCode); Assert.DoesNotContain("SyntheticLegacyAddress", await legacyOnly.Content.ReadAsStringAsync());
    }

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

    private static async Task<(Guid ReminderId, string Token)> ScopedPresentation(Host host, Guid recipientId)
    {
        var reminder = new Reminder("Synthetic scoped action", host.Clock.Now.AddMinutes(1), "UTC", null, host.Clock.Now);
        await using (var db = host.Db()) {
            db.Reminders.Add(reminder); await db.SaveChangesAsync(); host.Clock.Now += TimeSpan.FromMinutes(1);
            reminder.Claim(Guid.NewGuid(), host.Clock.Now); reminder.BeginTrigger(host.Clock.Now); reminder.Finish(null, true, host.Clock.Now); await db.SaveChangesAsync();
        }
        await using var scope = host.App.Services.CreateAsyncScope();
        var token = scope.ServiceProvider.GetRequiredService<IReminderActionTokens>().Create(reminder, host.Clock.Now.AddHours(1), recipientId);
        return (reminder.Id, token);
    }

    [Fact]
    public async Task RestrictedProofAuthorizesOnlyItsSignedOccurrenceRevisionGenerationAndActiveRecipient()
    {
        await using var host = await Host.Create(); var recipient = await host.Pair(); var presentation = await ScopedPresentation(host, recipient.Id);
        // No permanent Node credential crosses this background request.
        Assert.False(host.Client.DefaultRequestHeaders.Contains("Authorization"));
        var wrongRevision = await host.Client.PostAsJsonAsync($"/api/reminder-actions/{presentation.ReminderId}", new { token = presentation.Token, actionId = Guid.NewGuid(), expectedRevision = 2, generation = 1, action = "acknowledge" }); Assert.Equal(HttpStatusCode.NotFound, wrongRevision.StatusCode);
        var wrongOccurrence = await host.Client.PostAsJsonAsync($"/api/reminder-actions/{Guid.NewGuid()}", new { token = presentation.Token, actionId = Guid.NewGuid(), expectedRevision = 1, generation = 1, action = "acknowledge" }); Assert.Equal(HttpStatusCode.NotFound, wrongOccurrence.StatusCode);
        var applied = await Read(await host.Client.PostAsJsonAsync($"/api/reminder-actions/{presentation.ReminderId}", new { token = presentation.Token, actionId = Guid.NewGuid(), expectedRevision = 1, generation = 1, action = "acknowledge" })); Assert.True(applied.GetProperty("applied").GetBoolean());
        await using var db = host.Db(); Assert.NotNull((await db.Reminders.SingleAsync()).AcknowledgedAt); Assert.Single(await db.Set<ReminderActionReceipt>().ToArrayAsync());
    }

    [Theory]
    [InlineData("disable")]
    [InlineData("revoke")]
    [InlineData("remove")]
    public async Task RestrictedProofStopsAuthorizingActionsWhenItsRecipientIsDisabledRevokedOrRemoved(string operation)
    {
        await using var host = await Host.Create(); var actor = await host.Pair("windows"); var recipient = await host.Pair();
        var presentation = await ScopedPresentation(host, recipient.Id);
        await using (var db = host.Db()) {
            var registry = new NodeRegistry(db, host.Clock);
            if (operation == "disable") await registry.SetEnabledAsync(actor.Id, recipient.Id, false);
            else if (operation == "revoke") await registry.RevokeAsync(actor.Id, recipient.Id);
            else await registry.RemoveAsync(actor.Id, recipient.Id);
        }
        // The encrypted proof is intact and unexpired; current recipient administration is decisive.
        var response = await host.Client.PostAsJsonAsync($"/api/reminder-actions/{presentation.ReminderId}", new { token = presentation.Token, actionId = Guid.NewGuid(), expectedRevision = 1, generation = 1, action = "snooze", snoozeMinutes = 10 });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await using var final = host.Db(); var reminder = await final.Reminders.SingleAsync(); Assert.Equal(1, reminder.Generation); Assert.Null(reminder.AcknowledgedAt); Assert.Empty(final.Set<ReminderActionReceipt>());
    }

    [Fact]
    public async Task ReminderPagesKeepLegacyArraysExposeTheHundredAndFirstSeriesAndAuthenticateFocus()
    {
        await using var host = await Host.Create(); var actor = await host.Pair("windows"); var now = host.Clock.Now;
        Guid focusId; Guid lastSeriesId; DateTimeOffset lastSeriesDue;
        await using (var db = host.Db())
        {
            for (var i = 0; i < 201; i++) db.Reminders.Add(new("Synthetic upcoming " + i, now.AddMinutes(i + 1), "UTC", null, now));
            var history = new Reminder("Synthetic old focus", now.AddDays(-1), "UTC", null, now.AddDays(-2));
            history.Claim(Guid.NewGuid(), now.AddDays(-1)); history.BeginTrigger(now.AddDays(-1)); history.Finish(null, true, now.AddDays(-1)); history.Acknowledge(now.AddDays(-1));
            db.Reminders.Add(history); focusId = history.Id;
            for (var i = 0; i < 101; i++) db.Set<ReminderSeries>().Add(new("Synthetic series " + i, "UTC", new() { StartDate = "2030-01-01", LocalTime = "14:00" }, null, now));
            await db.SaveChangesAsync();
            var last = await db.Set<ReminderSeries>().OrderBy(s => s.CreatedAt).ThenBy(s => s.Id).LastAsync();
            var instant = last.Next(now)!; lastSeriesDue = instant.DueAt; lastSeriesId = last.Id;
            db.Reminders.Add(Reminder.ForSeries(last, instant, now)); last.Materialized(instant, now); await db.SaveChangesAsync();
        }
        var unauthenticated = await host.Client.GetAsync($"/api/nodes/reminders?focusId={focusId}");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode); Assert.DoesNotContain("Synthetic old focus", await unauthenticated.Content.ReadAsStringAsync());
        host.Authenticate(actor.Credential);
        var first = await Read(await host.Client.GetAsync("/api/nodes/reminders"));
        Assert.Equal(200, first.GetProperty("reminders").GetArrayLength()); Assert.Equal(100, first.GetProperty("series").GetArrayLength());
        Assert.Equal(JsonValueKind.Array, first.GetProperty("deliveries").ValueKind); Assert.Equal(JsonValueKind.Null, first.GetProperty("focusedReminder").ValueKind);
        Assert.True(first.GetProperty("hasMore").GetBoolean()); Assert.Equal(200, first.GetProperty("nextOffset").GetInt32());
        Assert.True(first.GetProperty("seriesHasMore").GetBoolean()); Assert.Equal(100, first.GetProperty("nextSeriesOffset").GetInt32());
        var again = await Read(await host.Client.GetAsync("/api/nodes/reminders"));
        Assert.Equal(first.GetProperty("reminders").GetRawText(), again.GetProperty("reminders").GetRawText()); Assert.Equal(first.GetProperty("series").GetRawText(), again.GetProperty("series").GetRawText());
        var focused = await Read(await host.Client.GetAsync($"/api/nodes/reminders?focusId={focusId}"));
        Assert.Equal(focusId, focused.GetProperty("focusedReminder").GetProperty("reminderId").GetGuid());
        Assert.DoesNotContain(focused.GetProperty("reminders").EnumerateArray(), r => r.GetProperty("reminderId").GetGuid() == focusId);
        var second = await Read(await host.Client.GetAsync("/api/nodes/reminders?offset=200&seriesOffset=100"));
        Assert.False(second.GetProperty("hasMore").GetBoolean()); Assert.Equal(JsonValueKind.Null, second.GetProperty("nextOffset").ValueKind);
        Assert.Equal(3, second.GetProperty("reminders").GetArrayLength()); Assert.False(second.GetProperty("seriesHasMore").GetBoolean());
        var lastSeries = Assert.Single(second.GetProperty("series").EnumerateArray()); Assert.Equal(lastSeriesId, lastSeries.GetProperty("seriesId").GetGuid());
        Assert.Equal(lastSeriesDue, lastSeries.GetProperty("nextDueAt").GetDateTimeOffset());
        var firstIds = first.GetProperty("reminders").EnumerateArray().Select(r => r.GetProperty("reminderId").GetGuid()).ToHashSet();
        Assert.DoesNotContain(second.GetProperty("reminders").EnumerateArray(), r => firstIds.Contains(r.GetProperty("reminderId").GetGuid()));
        var changed = await Read(await host.Client.PatchAsJsonAsync($"/api/nodes/reminder-series/{lastSeriesId}", new { expectedRevision = 1, text = "Synthetic edited later page" }));
        Assert.Equal("Synthetic edited later page", changed.GetProperty("text").GetString());
        var dedicated = await Read(await host.Client.GetAsync("/api/nodes/reminder-series?offset=100"));
        Assert.Single(dedicated.GetProperty("series").EnumerateArray()); Assert.False(dedicated.GetProperty("hasMore").GetBoolean()); Assert.Equal(JsonValueKind.Null, dedicated.GetProperty("nextOffset").ValueKind);
    }

    [Theory]
    [InlineData("/api/nodes/reminders?offset=-1")]
    [InlineData("/api/nodes/reminders?offset=1000001")]
    [InlineData("/api/nodes/reminders?seriesOffset=-1")]
    [InlineData("/api/nodes/reminders?seriesOffset=1000001")]
    [InlineData("/api/nodes/reminders?focusId=not-a-uuid")]
    [InlineData("/api/nodes/reminders?focusId=00000000-0000-0000-0000-000000000000")]
    [InlineData("/api/nodes/reminder-series?offset=-1")]
    [InlineData("/api/nodes/reminder-series?offset=1000001")]
    public async Task ReminderPaginationRejectsInvalidOffsetsAndFocusReferences(string path)
    {
        await using var host = await Host.Create(); var actor = await host.Pair("windows"); host.Authenticate(actor.Credential);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync(path)).StatusCode);
    }
}
