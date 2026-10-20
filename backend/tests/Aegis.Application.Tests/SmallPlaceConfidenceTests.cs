using Aegis.Application.Places;
using Aegis.Domain.Entities;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class SmallPlaceConfidenceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-16T12:00:00Z");
    private static double Longitude(double meters) => meters / 6371000 * 180 / Math.PI;
    private static PlaceCircle Circle(double center = 0, double radius = 65) => new(Guid.NewGuid(), 0, Longitude(center), radius);
    private static LocationObservationView Observation(double meters, double accuracy, int seconds = 0) =>
        new(Guid.NewGuid(), 0, Longitude(meters), accuracy, Now.AddSeconds(seconds), Now.AddSeconds(seconds), "fused", null, 0);

    [Theory]
    [InlineData(13, "inside")]
    [InlineData(30, "inside")]
    [InlineData(65, "uncertain")]
    [InlineData(100, "uncertain")]
    [InlineData(200, "uncertain")]
    public void SmallHomeDoesNotInventConfidence(double accuracy, string expected)
    {
        foreach (var circles in new[] { new[] { Circle() }, new[] { Circle(), Circle(50) } })
            Assert.Equal(expected, PlaceGeometry.Classify(circles, 0, 0, accuracy));
    }

    [Theory]
    [InlineData(0, 13, "inside")]
    [InlineData(40, 13, "inside")]
    [InlineData(40, 30, "uncertain")]
    [InlineData(65, 13, "uncertain")]
    [InlineData(90, 13, "outside")]
    [InlineData(90, 30, "uncertain")]
    [InlineData(300, 200, "outside")]
    public void GeometryAndUncertaintyRemainSeparate(double meters, double accuracy, string expected) =>
        Assert.Equal(expected, PlaceGeometry.Classify([Circle()], 0, Longitude(meters), accuracy));

    [Theory]
    [InlineData(150, 100, "inside")]
    [InlineData(150, 200, "uncertain")]
    [InlineData(500, 200, "inside")]
    public void LargerAreaIsStillSubjectToObservedAccuracy(double radius, double accuracy, string expected) =>
        Assert.Equal(expected, PlaceGeometry.Classify([Circle(radius: radius)], 0, 0, accuracy));

    [Fact]
    public void PoorBatterySaverReadingCannotEraseInsideOrTriggerAnExit()
    {
        var circles = new[] { Circle(), Circle(50) };
        var baseline = GeofencePresenceMachine.Observe(new(), circles, Observation(0, 13), Now);
        Assert.Equal("inside", baseline.State.Confirmed); Assert.Null(baseline.Transition);
        var poor = GeofencePresenceMachine.Observe(baseline.State, circles, Observation(130, 100, 1), Now.AddSeconds(1));
        Assert.Equal("inside", poor.State.Confirmed); Assert.True(poor.NeedsConfirmation); Assert.Null(poor.Transition);
        var moved = GeofencePresenceMachine.Observe(poor.State, circles, Observation(50, 13, 2), Now.AddSeconds(2));
        Assert.Equal("inside", moved.State.Confirmed); Assert.Null(moved.Transition);
        var outside = GeofencePresenceMachine.Observe(moved.State, circles, Observation(300, 30, 3), Now.AddSeconds(3));
        Assert.Null(outside.Transition);
        var confirmed = GeofencePresenceMachine.Observe(outside.State, circles, Observation(310, 30, 33), Now.AddSeconds(33));
        Assert.Equal("exit", confirmed.Transition);
    }

    [Fact]
    public void InitialOutsideAndStaleCandidatesAreNotTransitions()
    {
        var circles = new[] { Circle() };
        var initial = GeofencePresenceMachine.Observe(new(), circles, Observation(300, 13), Now);
        Assert.Equal("outside", initial.State.Confirmed); Assert.Null(initial.Transition);
        var entering = GeofencePresenceMachine.Observe(initial.State, circles, Observation(0, 13, 1), Now.AddSeconds(1));
        var tooLate = GeofencePresenceMachine.Observe(entering.State, circles, Observation(0, 13, 122), Now.AddSeconds(122));
        Assert.Null(tooLate.Transition);
        var entered = GeofencePresenceMachine.Observe(tooLate.State, circles, Observation(0, 13, 152), Now.AddSeconds(152));
        Assert.Equal("enter", entered.Transition);
        foreach (var (captured, evaluated) in new[] { (151, 153), (153, 400), (402, 401) })
            Assert.Equal(entered.State, GeofencePresenceMachine.Observe(entered.State, circles, Observation(300, 13, captured), Now.AddSeconds(evaluated)).State);
    }
}
