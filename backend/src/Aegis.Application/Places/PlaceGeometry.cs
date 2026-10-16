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
        if (circles.Count == 0 || !double.IsFinite(latitude) || latitude is < -90 or > 90 ||
            !double.IsFinite(longitude) || longitude is < -180 or > 180 ||
            !double.IsFinite(accuracyMeters) || accuracyMeters is <= 0 or > 200) return "uncertain";
        // Match the native confidence policy. Do not erode a small home by a second
        // fixed 30 m margin or silently expand its Android registration geometry.
        var confidenceMargin = Math.Max(3, accuracyMeters);
        var signedDistances = circles.Select(c => DistanceMeters(latitude, longitude, c.Latitude, c.Longitude) - c.RadiusMeters).ToArray();
        if (signedDistances.Any(d => d < -confidenceMargin)) return "inside";
        if (signedDistances.All(d => d > confidenceMargin)) return "outside";
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
    public static Guid? ResolveContext(IReadOnlyList<PlaceView> candidates, LocationObservationView? observation, LocationContextOptions options)
    {
        if (observation is null || observation.AgeSeconds < 0 || observation.AgeSeconds > options.MaximumAgeSeconds || observation.AccuracyMeters > options.MaximumCityAccuracyMeters) return null;
        var inside = candidates.Where(p => Classify(p.Circles, observation.Latitude, observation.Longitude, observation.AccuracyMeters) == "inside").ToArray();
        if (inside.Length == 1) return inside[0].Id;
        if (inside.Length > 1) return null;
        if (candidates.Any(p => p.Circles.Count == 0)) return null;
        var nearest = candidates.Where(p => p.Circles.Count > 0).Select(p => new {
            p.Id, Distance = p.Circles.Min(c => Math.Max(0, DistanceMeters(observation.Latitude, observation.Longitude, c.Latitude, c.Longitude) - c.RadiusMeters))
        }).OrderBy(p => p.Distance).ToArray();
        if (nearest.Length == 0 || nearest[0].Distance > options.LocalResidenceMeters) return null;
        if (nearest.Length == 1 || nearest[1].Distance - nearest[0].Distance >= options.CandidateMarginMeters && nearest[1].Distance >= Math.Max(nearest[0].Distance, 1) * options.CandidateDistanceRatio) return nearest[0].Id;
        return null;
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
