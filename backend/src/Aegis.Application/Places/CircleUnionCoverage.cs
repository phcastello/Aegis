using Aegis.Domain.Entities;

namespace Aegis.Application.Places;

/// <summary>
/// Bounded spherical circle arrangement shared conceptually with NodeGeoEngine.
/// Every arc is partitioned at all intersections, so membership is constant on
/// each open arc. Both the target perimeter and exposed union boundaries inside
/// it are checked: perimeter coverage alone would overlook internal holes.
/// </summary>
internal static class CircleUnionCoverage
{
    internal const double NumericMarginMeters = .05;
    internal const int MaximumCircles = 100;
    private const double EarthMeters = 6371000;
    private readonly record struct Vector(double X, double Y, double Z)
    {
        public static Vector operator +(Vector a, Vector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vector operator *(Vector a, double scale) => new(a.X * scale, a.Y * scale, a.Z * scale);
        public double Dot(Vector b) => X * b.X + Y * b.Y + Z * b.Z;
        public Vector Cross(Vector b) => new(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
        public double Length => Math.Sqrt(Dot(this));
        public Vector Unit => this * (1 / Length);
        public double Separation(Vector b) => Math.Atan2(Cross(b).Length, Dot(b));
    }
    private static Vector Position(double latitude, double longitude)
    {
        var lat = latitude * Math.PI / 180; var lon = longitude * Math.PI / 180;
        return new(Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat));
    }
    private sealed class Cap
    {
        public Vector Center { get; }
        private double Radius { get; }
        private Vector U { get; }
        private Vector V { get; }
        public Cap(Vector center, double radius)
        {
            Center = center; Radius = radius;
            var reference = Math.Abs(center.Z) < .9 ? new Vector(0, 0, 1) : new Vector(1, 0, 0);
            U = reference.Cross(center).Unit; V = center.Cross(U);
        }
        public bool Contains(Vector point) => Center.Separation(point) < Radius - 1e-12;
        private Vector Point(double angle) => Center * Math.Cos(Radius) + (U * Math.Cos(angle) + V * Math.Sin(angle)) * Math.Sin(Radius);
        private IEnumerable<double> Cuts(Cap other)
        {
            var separation = Center.Separation(other.Center);
            if (separation < 1e-12) yield break;
            // Stable at residential distances, where naive cos(a)-cos(b) loses precision.
            var numerator = -2 * Math.Sin((other.Radius + Radius) / 2) * Math.Sin((other.Radius - Radius) / 2)
                + Math.Cos(Radius) * 2 * Math.Pow(Math.Sin(separation / 2), 2);
            var ratio = numerator / (Math.Sin(Radius) * Math.Sin(separation));
            if (!double.IsFinite(ratio) || ratio < -1 - 1e-10 || ratio > 1 + 1e-10) yield break;
            var middle = Math.Atan2(other.Center.Dot(V), other.Center.Dot(U));
            var delta = Math.Acos(Math.Clamp(ratio, -1, 1));
            yield return Normalize(middle - delta); yield return Normalize(middle + delta);
        }
        public IEnumerable<Vector> Witnesses(IReadOnlyList<Cap> others)
        {
            var angles = others.SelectMany(Cuts).Append(0).Order().ToArray();
            for (var i = 0; i < angles.Length; i++)
            {
                var start = angles[i]; var end = i + 1 < angles.Length ? angles[i + 1] : angles[0] + 2 * Math.PI;
                yield return Point(start); yield return Point((start + end) / 2);
            }
        }
    }
    private static double Normalize(double angle) => (angle % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI);
    public static bool Covers(IReadOnlyList<PlaceCircle> circles, double latitude, double longitude, double confidence)
    {
        var target = new Cap(Position(latitude, longitude), confidence / EarthMeters);
        var caps = circles.Select(c => new Cap(Position(c.Latitude, c.Longitude), Math.Max(0, c.RadiusMeters - NumericMarginMeters) / EarthMeters)).ToArray();
        if (!caps.Any(c => c.Contains(target.Center))) return false;
        if (target.Witnesses(caps).Any(p => !caps.Any(c => c.Contains(p)))) return false;
        for (var i = 0; i < caps.Length; i++)
        {
            var others = caps.Where((_, j) => j != i).ToArray();
            if (caps[i].Witnesses([.. others, target]).Any(p => target.Contains(p) && !others.Any(c => c.Contains(p)))) return false;
        }
        return true;
    }
}
