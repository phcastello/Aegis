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

// Synthetic equatorial fixtures make the distance/age boundaries deterministic.
public sealed class TemporalPlaceContextTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-10-28T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Signals : INodeDomainSignals
    {
        public int Refreshes;
        public Func<Guid, object, Task<string>>? Refresh;
        public Task<string> SendAsync(Guid nodeId, string capability, object input, CancellationToken ct = default)
        {
            if (capability != "location.current") return Task.FromResult("unavailable");
            Refreshes++;
            return Refresh?.Invoke(nodeId, input) ?? Task.FromResult("unavailable");
        }
    }
    private readonly Clock clock = new();
    private readonly Signals signals = new();
    private readonly LocationContextOptions options = new() { RefreshTimeoutSeconds = 1 };
    private readonly AegisDbContext db = new(new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private PlaceService Service => new(db, clock, Options.Create(options), signals);
    private static double Longitude(double distanceMeters) => distanceMeters / (6371000 * Math.PI / 180);
    private static PlaceCircle Circle(double meters, double radius = 500) => new(Guid.NewGuid(), 0, Longitude(meters), radius);
    private static PlaceView Candidate(string name, double meters, double radius = 500) => new(Guid.NewGuid(), name, "property", null, 1, [Circle(meters, radius)], ["casa"]);
    private LocationObservationView Observation(double ageSeconds = 1800, double meters = 0, double accuracy = 10) =>
        new(Guid.NewGuid(), 0, Longitude(meters), accuracy, clock.Now.AddSeconds(-ageSeconds), clock.Now.AddSeconds(-ageSeconds), "fused", null, ageSeconds);
    private PlaceContextResolution Resolve(LocationObservationView? observation, params PlaceView[] candidates) => PlaceGeometry.EvaluateContext(candidates, observation, options, clock.Now);
    private async Task<(Guid Phone, PlaceView Home, PlaceView Away)> Setup(double separation = 750000)
    {
        var home = await Service.SaveAsync(null, new("Casa A sintética", "property", null, [Circle(0)], ["casa"]));
        var away = await Service.SaveAsync(null, new("Casa B sintética", "property", null, [Circle(separation)], ["casa"]));
        var node = new AegisNode(Guid.NewGuid(), "Celular sintético", NodePlatform.Android, "0.7.2", 1, clock.Now);
        db.Nodes.Add(node); await db.SaveChangesAsync();
        await Service.ConfigureAsync(new(node.Id, node.Id, "dynamic"));
        return (node.Id, home, away);
    }

    [Fact] public void ThirtyMinuteFixDiscriminatesHomes750KmApartWithoutClaimingPresence()
    {
        var a = Candidate("Casa A", 0); var b = Candidate("Casa B", 750000);
        var result = Resolve(Observation(), a, b);
        Assert.Equal(a.Id, result.PlaceId); Assert.Equal("historical_temporal_plausibility", result.Reason);
        Assert.Equal("outside", PlaceGeometry.Classify(a.Circles, 0, Longitude(20000), 10));
        Assert.Equal(a.Id, Resolve(Observation(meters: 20000), a, b).PlaceId); // regional interpretation, not presence
    }
    [Theory]
    [InlineData(70000, 1800)] // nearby cities such as Sao Paulo/Mongagua
    [InlineData(750000, 2400)] // exceptional travel becomes plausible
    [InlineData(750000, 3600)]
    public void ReachableAlternativeRequiresClarification(double separation, double age)
    {
        var result = Resolve(Observation(age), Candidate("Casa A", 0), Candidate("Casa B", separation));
        Assert.Null(result.PlaceId); Assert.Equal("multiple_plausible_candidates", result.Reason);
    }
    [Fact] public void RecentFixCanDiscriminateNearbyCitiesButNotOverlappingHomes()
    {
        var a = Candidate("Casa A", 0);
        Assert.Equal(a.Id, Resolve(Observation(10), a, Candidate("Casa B", 70000)).PlaceId);
        var overlapping = Resolve(Observation(0, 200), a, Candidate("Casa B", 400));
        Assert.Null(overlapping.PlaceId); Assert.Equal("multiple_plausible_candidates", overlapping.Reason);
    }
    [Fact] public void CandidateAreaRadiusAndReportedAccuracyExpandPossibility()
    {
        var a = Candidate("Casa A", 0);
        Assert.Null(Resolve(Observation(), a, Candidate("Casa extensa", 750000, 100000)).PlaceId);
        var boundary = Candidate("Casa limite", 656000);
        Assert.Equal(a.Id, Resolve(Observation(accuracy: 10), a, boundary).PlaceId);
        Assert.Null(Resolve(Observation(accuracy: 1500), a, boundary).PlaceId);
    }
    [Fact] public void NoNearestHomeIsChosenForAnUnrelatedRegion()
    {
        var result = Resolve(Observation(meters: 250000), Candidate("Casa A", 0), Candidate("Casa B", 750000));
        Assert.Null(result.PlaceId); Assert.Equal("outside_candidate_regions", result.Reason);
    }
    [Fact] public void UnboundedHistoryAndImpreciseFixesAreRejected()
    {
        var a = Candidate("Casa A", 0); var b = Candidate("Casa remota", 10000000);
        Assert.Equal("observation_too_old", Resolve(Observation(21601), a, b).Reason);
        Assert.Equal("location_too_imprecise", Resolve(Observation(accuracy: 3000), a, b).Reason);
        Assert.Equal("location_missing", Resolve(null, a, b).Reason);
        Assert.Equal("candidate_geometry_unavailable", Resolve(Observation(), a, b with { Circles = [] }).Reason);
    }
    [Theory]
    [InlineData("future")]
    [InlineData("future_received")]
    [InlineData("before_capture")]
    [InlineData("wrong_age")]
    [InlineData("nan_age")]
    [InlineData("invalid_coordinate")]
    [InlineData("invalid_accuracy")]
    [InlineData("invalid_source")]
    [InlineData("default_timestamp")]
    public void InconsistentObservationsDoNotInferContext(string scenario)
    {
        var observation = Observation();
        observation = scenario switch {
            "future" => observation with { CapturedAt = clock.Now.AddMinutes(1), AgeSeconds = -60 },
            "future_received" => observation with { ReceivedAt = clock.Now.AddSeconds(1) },
            "before_capture" => observation with { ReceivedAt = observation.CapturedAt.AddSeconds(-1) },
            "wrong_age" => observation with { AgeSeconds = 100 },
            "nan_age" => observation with { AgeSeconds = double.NaN },
            "invalid_coordinate" => observation with { Latitude = double.NaN },
            "invalid_accuracy" => observation with { AccuracyMeters = double.NaN },
            "invalid_source" => observation with { Source = "unknown" },
            _ => observation with { CapturedAt = default }
        };
        var result = Resolve(observation, Candidate("Casa A", 0), Candidate("Casa B", 750000));
        Assert.Null(result.PlaceId); Assert.Equal("observation_invalid", result.Reason);
    }
    [Fact] public async Task HistoricalResolutionAvoidsRefreshAndExplicitReferenceWins()
    {
        var (phone, home, away) = await Setup();
        await Service.ObserveAsync(phone, new(0, 0, 10, clock.Now.AddMinutes(-30), "fused"));
        var historical = await Service.ResolveAsync("casa", false);
        Assert.Equal(home.Id, historical.PlaceId); Assert.Equal("historical_temporal_plausibility", historical.Reason);
        Assert.Null(historical.RefreshStatus); Assert.Equal(0, signals.Refreshes);
        var explicitReference = await Service.ResolveAsync(away.Name, false);
        Assert.Equal(away.Id, explicitReference.PlaceId); Assert.Equal("explicit_name", explicitReference.Reason);
        Assert.Equal(0, signals.Refreshes);
        var tool = await new PlaceResolveTool(Service).ExecuteAsync(JsonSerializer.SerializeToElement(new { reference = "casa" }), new(Guid.NewGuid(), Guid.NewGuid(), "pedido sintético"));
        Assert.False(JsonSerializer.Deserialize<JsonElement>(tool.Content).GetProperty("presenceConfirmed").GetBoolean());
        Assert.DoesNotContain("latitude", tool.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("longitude", tool.Content, StringComparison.OrdinalIgnoreCase);
    }
    [Fact] public async Task MissingGpsAndUnavailableRefreshRemainClarificationWithSeparateDiagnostics()
    {
        await Setup();
        var result = await Service.ResolveAsync("casa", false);
        Assert.Equal("clarification_required", result.Status); Assert.Null(result.PlaceId);
        Assert.Equal("location_missing", result.ObservationReason); Assert.Equal("refresh_unavailable", result.RefreshStatus);
        Assert.Equal(1, signals.Refreshes);
    }
    [Fact] public async Task RefreshWithoutResponseIsBoundedAndDoesNotReuseAmbiguousHistory()
    {
        var (phone, _, _) = await Setup(70000);
        await Service.ObserveAsync(phone, new(0, 0, 10, clock.Now.AddMinutes(-30), "gps"));
        signals.Refresh = (_, _) => Task.FromResult("accepted");
        var result = await Service.ResolveAsync("casa", false);
        Assert.Equal("clarification_required", result.Status); Assert.Null(result.PlaceId);
        Assert.Equal("multiple_plausible_candidates", result.ObservationReason); Assert.Equal("refresh_timed_out", result.RefreshStatus);
        Assert.Equal(1, signals.Refreshes);
    }
    [Fact] public async Task ActualRefreshResolvesOnlyAfterNewObservationArrives()
    {
        var (phone, _, away) = await Setup(70000);
        await Service.ObserveAsync(phone, new(0, 0, 10, clock.Now.AddMinutes(-30), "gps"));
        signals.Refresh = async (id, input) => {
            var request = JsonSerializer.SerializeToElement(input);
            await Service.ObserveAsync(id, new(0, Longitude(70000), 10, clock.Now, "gps", request.GetProperty("requestId").GetGuid()));
            return "accepted";
        };
        var result = await Service.ResolveAsync("casa", false);
        Assert.Equal(away.Id, result.PlaceId); Assert.Equal("updated", result.RefreshStatus); Assert.Equal("recent_personal_phone_context", result.Reason);
    }
    [Fact] public async Task FutureDeviceTimestampIsNeverClampedIntoFreshContext()
    {
        var (phone, _, _) = await Setup();
        await Service.ObserveAsync(phone, new(0, 0, 10, clock.Now.AddMinutes(1), "gps"));
        var result = await Service.ResolveAsync("casa", false);
        Assert.Null(result.PlaceId); Assert.Equal("observation_invalid", result.ObservationReason);
        Assert.Equal(-60, result.LocationAgeSeconds);
    }
    [Theory]
    [InlineData("accuracy")]
    [InlineData("source")]
    public async Task IncompletePersistedObservationIsInvalidWithoutThrowing(string missing)
    {
        var (phone, _, _) = await Setup();
        await Service.ObserveAsync(phone, new(0, 0, 10, clock.Now, "gps"));
        var profile = await db.Set<NodeLocationProfile>().SingleAsync();
        if (missing == "accuracy") db.Entry(profile).Property(p => p.AccuracyMeters).CurrentValue = null;
        else db.Entry(profile).Property(p => p.Source).CurrentValue = null;
        await db.SaveChangesAsync();
        var result = await Service.ResolveAsync("casa", false);
        Assert.Null(result.PlaceId); Assert.Equal("observation_invalid", result.ObservationReason);
    }
    [Fact] public void HistoricalContextCannotChangePresenceOrTriggerGeofences()
    {
        var a = Candidate("Casa A", 0); var oldFix = Observation();
        Assert.Equal(a.Id, Resolve(oldFix, a, Candidate("Casa B", 750000)).PlaceId);
        var initial = new GeofencePresenceState("outside");
        var presence = GeofencePresenceMachine.Observe(initial, a.Circles, oldFix, clock.Now);
        Assert.Equal(initial, presence.State); Assert.Null(presence.Transition); Assert.False(presence.NeedsConfirmation);
    }
    [Fact] public void OptionsRequireFiniteConservativeBounds()
    {
        Assert.True(options.IsValid());
        options.ConservativeTravelSpeedKmh = 120; Assert.False(options.IsValid());
        options.ConservativeTravelSpeedKmh = 1200; options.MaximumHistoricalAgeSeconds = 90001; Assert.False(options.IsValid());
        options.MaximumHistoricalAgeSeconds = 21600; options.TravelUncertaintyMeters = double.NaN; Assert.False(options.IsValid());
    }
}
