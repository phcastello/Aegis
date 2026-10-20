using Aegis.Application.Places;
using Aegis.Domain.Entities;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class CircleUnionCoverageTests
{
    private static double Degrees(double meters) => meters / 6371000 * 180 / Math.PI;
    private static PlaceCircle Circle(double east, double north, double radius) => new(Guid.NewGuid(), Degrees(north), Degrees(east), radius);

    [Theory]
    [InlineData(50, "inside")]
    [InlineData(52, "uncertain")]
    public void OverlappingCirclesJointlyCoverConfidenceDisk(double accuracy, string expected) =>
        Assert.Equal(expected, PlaceGeometry.Classify([Circle(-40, 0, 65), Circle(40, 0, 65)], 0, 0, accuracy));

    [Fact]
    public void CoveredCenterAndPerimeterDoNotHideAnInternalHole()
    {
        var ring = Enumerable.Range(0, 12).Select(i => Circle(150 * Math.Cos(i * 2 * Math.PI / 12), 150 * Math.Sin(i * 2 * Math.PI / 12), 100))
            .Append(Circle(-60, 0, 70)).ToArray();
        Assert.Equal("inside", PlaceGeometry.Classify(ring, 0, 0, 3));
        Assert.Equal("uncertain", PlaceGeometry.Classify(ring, 0, 0, 200));
        Assert.Equal("outside", PlaceGeometry.Classify(ring, 0, Degrees(40), 3));
    }

    [Fact]
    public void TangenciesGapsAndExactConfidenceBoundaryRemainUncertain()
    {
        Assert.Equal("uncertain", PlaceGeometry.Classify([Circle(-65, 0, 65), Circle(65, 0, 65)], 0, 0, 13));
        Assert.Equal("uncertain", PlaceGeometry.Classify([Circle(-66, 0, 65), Circle(66, 0, 65)], 0, 0, 13));
        Assert.Equal("uncertain", PlaceGeometry.Classify([Circle(0, 0, 65)], 0, Degrees(52), 13));
    }

    [Fact]
    public void UnionDoesNotDependOnLongitudeWrappingOrPolarProjection()
    {
        Assert.Equal("inside", PlaceGeometry.Classify([
            new(Guid.NewGuid(), 0, 179.99954, 65), new(Guid.NewGuid(), 0, -179.99974, 65)], 0, 179.9999, 50));
        Assert.Equal("inside", PlaceGeometry.Classify([
            new(Guid.NewGuid(), 89.99864, 179.9999, 65), new(Guid.NewGuid(), 89.99936, 179.9999, 65)], 89.999, 179.9999, 50));
    }
}
