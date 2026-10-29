using Aegis.Domain.Entities;

namespace Aegis.Application.Places;

public static class PlaceGeometry
{
    public static double DistanceMeters(double latitude, double longitude, double otherLatitude, double otherLongitude)
    {
        const double radians = Math.PI / 180;
        var dLat = (otherLatitude - latitude) * radians; var dLon = (otherLongitude - longitude) * radians;
        var a = Math.Pow(Math.Sin(dLat / 2), 2) + Math.Cos(latitude * radians) * Math.Cos(otherLatitude * radians) * Math.Pow(Math.Sin(dLon / 2), 2);
        return 6371000 * 2 * Math.Atan2(Math.Sqrt(Math.Clamp(a, 0, 1)), Math.Sqrt(Math.Clamp(1 - a, 0, 1)));
    }
    // Accuracy is a heuristic uncertainty indicator, never a physical bound/proof.
    public static string Classify(IReadOnlyList<PlaceCircle> circles, double latitude, double longitude, double accuracyMeters)
    {
        if (circles.Count == 0 || circles.Count > CircleUnionCoverage.MaximumCircles ||
            circles.Any(c => !double.IsFinite(c.Latitude) || c.Latitude is < -90 or > 90 || !double.IsFinite(c.Longitude) || c.Longitude is < -180 or > 180 || !double.IsFinite(c.RadiusMeters) || c.RadiusMeters is <= 0 or > 100000) || !double.IsFinite(latitude) || latitude is < -90 or > 90 ||
            !double.IsFinite(longitude) || longitude is < -180 or > 180 ||
            !double.IsFinite(accuracyMeters) || accuracyMeters is <= 0 or > 200) return "uncertain";
        // Match the native confidence policy. Do not erode a small home by a second
        // fixed 30 m margin or silently expand its Android registration geometry.
        var confidenceMargin = Math.Max(3, accuracyMeters);
        var signedDistances = circles.Select(c => DistanceMeters(latitude, longitude, c.Latitude, c.Longitude) - c.RadiusMeters).ToArray();
        if (signedDistances.Any(d => d < -confidenceMargin - CircleUnionCoverage.NumericMarginMeters) ||
            CircleUnionCoverage.Covers(circles, latitude, longitude, confidenceMargin)) return "inside";
        if (signedDistances.All(d => d > confidenceMargin + CircleUnionCoverage.NumericMarginMeters)) return "outside";
        return "uncertain";
    }
    public static IReadOnlyList<PlaceCircle> EffectiveCircles(Place place, IReadOnlyDictionary<Guid, Place> places)
    {
        var seen = new HashSet<Guid>(); var current = place;
        while (seen.Add(current.Id))
        {
            if (current.Circles.Count != 0) return current.Circles;
            if (current.ParentId is not { } parent || !places.TryGetValue(parent, out current!)) return [];
        }
        throw new ArgumentException("A hierarquia de lugares contém um ciclo.");
    }
    public static bool WouldCycle(Guid id, Guid? parent, IReadOnlyDictionary<Guid, Guid?> parents)
    {
        var seen = new HashSet<Guid> { id };
        while (parent is { } next) { if (!seen.Add(next)) return true; parent = parents.GetValueOrDefault(next); }
        return false;
    }
    public static Guid? ResolveContext(IReadOnlyList<PlaceView> candidates, LocationObservationView? observation, LocationContextOptions options, DateTimeOffset? now = null)
        => EvaluateContext(candidates, observation, options, now).PlaceId;

    public static string? ObservationIssue(LocationObservationView? observation, LocationContextOptions options, DateTimeOffset? now = null)
    {
        if (observation is null) return "location_missing";
        if (!double.IsFinite(observation.Latitude) || observation.Latitude is < -90 or > 90 ||
            !double.IsFinite(observation.Longitude) || observation.Longitude is < -180 or > 180 ||
            !double.IsFinite(observation.AccuracyMeters) || observation.AccuracyMeters <= 0 ||
            !double.IsFinite(observation.AgeSeconds) || observation.AgeSeconds < 0 ||
            observation.CapturedAt == default || observation.ReceivedAt == default || observation.CapturedAt > observation.ReceivedAt ||
            (observation.ReceivedAt - observation.CapturedAt).TotalSeconds > observation.AgeSeconds + 1 ||
            observation.Source is not ("fused" or "gps" or "network" or "passive")) return "observation_invalid";
        if (now is { } current && (observation.CapturedAt > current || observation.ReceivedAt > current ||
            Math.Abs((current - observation.CapturedAt).TotalSeconds - observation.AgeSeconds) > 1)) return "observation_invalid";
        if (observation.AccuracyMeters > options.MaximumCityAccuracyMeters) return "location_too_imprecise";
        if (observation.AgeSeconds > options.MaximumHistoricalAgeSeconds) return "observation_too_old";
        return null;
    }

    // This interprets a reference to a region. It never establishes current presence
    // and must not be used to arm or trigger a geofence.
    public static PlaceContextResolution EvaluateContext(IReadOnlyList<PlaceView> candidates, LocationObservationView? observation, LocationContextOptions options, DateTimeOffset? now = null)
    {
        if (ObservationIssue(observation, options, now) is { } issue) return new(null, issue);
        var observed = observation!;
        if (candidates.Count == 0 || candidates.Any(p => p.Circles.Count == 0 || p.Circles.Count > CircleUnionCoverage.MaximumCircles ||
            p.Circles.Any(c => !double.IsFinite(c.Latitude) || c.Latitude is < -90 or > 90 || !double.IsFinite(c.Longitude) ||
                c.Longitude is < -180 or > 180 || !double.IsFinite(c.RadiusMeters) || c.RadiusMeters is <= 0 or > 100000)))
            return new(null, "candidate_geometry_unavailable");
        var nearest = candidates.Select(p => new {
            p.Id, Distance = p.Circles.Min(c => Math.Max(0, DistanceMeters(observed.Latitude, observed.Longitude, c.Latitude, c.Longitude) - c.RadiusMeters))
        }).OrderBy(p => p.Distance).ToArray();
        // A remote observation cannot select the nearest home merely because other
        // homes are even farther away. Require evidence associating this fix to its region.
        if (nearest[0].Distance + observed.AccuracyMeters > options.LocalResidenceMeters) return new(null, "outside_candidate_regions");
        var inside = candidates.Where(p => Classify(p.Circles, observed.Latitude, observed.Longitude, observed.AccuracyMeters) == "inside").ToArray();
        var regionAnchored = inside.Length == 1 || nearest.Length == 1 ||
            nearest[1].Distance - nearest[0].Distance >= options.CandidateMarginMeters &&
            nearest[1].Distance >= Math.Max(nearest[0].Distance, 1) * options.CandidateDistanceRatio;
        if (!regionAnchored) return new(null, "multiple_plausible_candidates");
        // Expand real candidate areas by the existing regional context allowance.
        // Accuracy is an uncertainty estimate, not a guaranteed physical boundary.
        // The speed deliberately allows air travel; no urban-speed assumption or route API.
        var reach = options.ConservativeTravelSpeedKmh / 3.6 * observed.AgeSeconds + observed.AccuracyMeters + options.TravelUncertaintyMeters;
        var possible = nearest.Where(p => p.Distance <= reach + options.LocalResidenceMeters).ToArray();
        if (possible.Length != 1) return new(null, "multiple_plausible_candidates");
        return new(possible[0].Id, observed.AgeSeconds <= options.MaximumAgeSeconds ? "recent_personal_phone_context" : "historical_temporal_plausibility");
    }
}

public sealed record GeofencePresenceState(string Confirmed = "unknown", string? Candidate = null, DateTimeOffset? CandidateSince = null, DateTimeOffset? LastCapturedAt = null);
public sealed record GeofencePresenceResult(GeofencePresenceState State, string? Transition, bool NeedsConfirmation);
public static class GeofencePresenceMachine
{
    public static GeofencePresenceResult Observe(GeofencePresenceState state, IReadOnlyList<PlaceCircle> circles, LocationObservationView observation, DateTimeOffset now)
    {
        if (observation.CapturedAt > now || now - observation.CapturedAt > TimeSpan.FromMinutes(2) ||
            state.LastCapturedAt is { } last && observation.CapturedAt <= last) return new(state, null, false);
        var classified = PlaceGeometry.Classify(circles, observation.Latitude, observation.Longitude, observation.AccuracyMeters);
        if (classified == "uncertain") return new(state with { Candidate = null, CandidateSince = null, LastCapturedAt = observation.CapturedAt }, null, true);
        if (state.Confirmed == "unknown") return new(new(classified, LastCapturedAt: observation.CapturedAt), null, false);
        if (classified == state.Confirmed) return new(state with { Candidate = null, CandidateSince = null, LastCapturedAt = observation.CapturedAt }, null, false);
        if (state.Candidate != classified || state.CandidateSince is null || observation.CapturedAt - state.CandidateSince > TimeSpan.FromMinutes(2)) return new(state with { Candidate = classified, CandidateSince = observation.CapturedAt, LastCapturedAt = observation.CapturedAt }, null, true);
        if (observation.CapturedAt - state.CandidateSince < TimeSpan.FromSeconds(30)) return new(state with { LastCapturedAt = observation.CapturedAt }, null, true);
        return new(new(classified, LastCapturedAt: observation.CapturedAt), classified == "inside" ? "enter" : "exit", false);
    }
}
