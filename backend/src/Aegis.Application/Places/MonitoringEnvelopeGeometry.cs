using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Aegis.Domain.Entities;

namespace Aegis.Application.Places;

/// <summary>Operational proximity regions are derived from the entire real union.
/// A uniform approved padding expands each circle (the union's Minkowski buffer),
/// without merging real gaps or changing real circle identities. Registration counts
/// both sets and shares physical regions across rules before enforcing Android's cap.</summary>
public static class MonitoringEnvelopeGeometry
{
    public static double SuggestedPadding(IReadOnlyList<PlaceCircle> circles) => circles.Count == 0 ? 150 :
        Math.Clamp(200 - circles.Min(c => c.RadiusMeters), 75, 150);

    public static string Fingerprint(IReadOnlyList<PlaceCircle> circles) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join(";", circles.OrderBy(c => c.Id).Select(c => string.Create(CultureInfo.InvariantCulture,
            $"{c.Id}:{c.Latitude:R}:{c.Longitude:R}:{c.RadiusMeters:R}")))))).ToLowerInvariant();

    public static MonitoringEnvelopeView View(Place place, IReadOnlyList<PlaceCircle> realCircles)
    {
        var config = place.MonitoringEnvelope;
        var approvalRequired = config is null || config.GeometryFingerprint != Fingerprint(realCircles);
        var enabled = config?.Enabled ?? false;
        var circles = enabled && !approvalRequired && place.Type != "room"
            ? realCircles.Select(c => c with { Id = RegionId(place.Id, c.Id), RadiusMeters = c.RadiusMeters + config!.PaddingMeters }).ToArray()
            : Array.Empty<PlaceCircle>();
        return new(enabled, config?.Revision ?? 0, config?.PaddingMeters ?? SuggestedPadding(realCircles), config?.ApprovedAt, approvalRequired, circles);
    }

    private static Guid RegionId(Guid place, Guid circle) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"aegis:monitoring-envelope:v1:{place}:{circle}"))[..16]);

    public static int RegionCount(IEnumerable<Guid> placeIds, IReadOnlyDictionary<Guid, Place> places)
    {
        var ids = placeIds.Distinct().ToArray();
        var real = ids.SelectMany(id => PlaceGeometry.EffectiveCircles(places[id], places)).DistinctBy(c => c.Id).Count();
        var operational = ids.SelectMany(id => View(places[id], PlaceGeometry.EffectiveCircles(places[id], places)).Circles).DistinctBy(c => c.Id).Count();
        return real + operational;
    }
}
