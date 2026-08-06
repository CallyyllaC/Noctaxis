using Avalonia;
using Noctaxis.Core.Calculations;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;

namespace Noctaxis.Desktop.Tests;

public sealed class FramingWedgeLayoutCalculatorTests
{
    private static readonly GeoCoordinate Observer = new(0, 0);
    private readonly FramingWedgeLayoutCalculator _calculator = new();
    private readonly CameraFramingGuideCalculator _guideCalculator = new();

    [Fact]
    public void ViewportResize_ClipsRaysToEachCurrentViewport()
    {
        var guide = Guide(40, 0);
        var small = _calculator.Calculate(Observer, new Rect(0, 0, 400, 300), guide,
            ProjectFrom(new Point(200, 150), 10_000));
        var large = _calculator.Calculate(Observer, new Rect(0, 0, 800, 600), guide,
            ProjectFrom(new Point(400, 300), 10_000));

        Assert.Equal(0, small.CentreBearing!.ClippedEnd.Y, 6);
        Assert.Equal(0, large.CentreBearing!.ClippedEnd.Y, 6);
        AssertOnBoundary(small.LeftBoundary!.ClippedEnd, new Rect(0, 0, 400, 300));
        AssertOnBoundary(large.LeftBoundary!.ClippedEnd, new Rect(0, 0, 800, 600));
        Assert.True(Distance(large.Apex, large.CentreBearing.ClippedEnd) > Distance(small.Apex, small.CentreBearing.ClippedEnd));
    }

    [Fact]
    public void MapZoomChange_DoesNotIntroduceAnArbitraryRayLength()
    {
        var viewport = new Rect(0, 0, 800, 600);
        var apex = new Point(400, 300);
        var guide = Guide(55, 35);
        var zoomedOut = _calculator.Calculate(Observer, viewport, guide, ProjectFrom(apex, 2_000));
        var zoomedIn = _calculator.Calculate(Observer, viewport, guide, ProjectFrom(apex, 40_000));

        AssertPointEqual(zoomedOut.LeftBoundary!.ClippedEnd, zoomedIn.LeftBoundary!.ClippedEnd, 0.01);
        AssertPointEqual(zoomedOut.RightBoundary!.ClippedEnd, zoomedIn.RightBoundary!.ClippedEnd, 0.01);
        AssertPointEqual(zoomedOut.CentreBearing!.ClippedEnd, zoomedIn.CentreBearing!.ClippedEnd, 0.01);
    }

    [Fact]
    public void NarrowTelephotoFov_RemainsNarrowWithoutMinimumMouthExpansion()
    {
        var viewport = new Rect(0, 0, 800, 600);
        var layout = _calculator.Calculate(Observer, viewport, Guide(1, 0),
            ProjectFrom(new Point(400, 300), 10_000));

        Assert.Equal(0, layout.LeftBoundary!.ClippedEnd.Y, 6);
        Assert.Equal(0, layout.RightBoundary!.ClippedEnd.Y, 6);
        Assert.InRange(Distance(layout.LeftBoundary.ClippedEnd, layout.RightBoundary.ClippedEnd), 0, 6);
        Assert.Equal(0, layout.CentreBearing!.ClippedEnd.Y, 6);
        AssertShadeRegions(layout, FramingRaySegmentState.Visible);
    }

    [Fact]
    public void WideAngleFov_PreservesItsTrueOpeningAngle()
    {
        var viewport = new Rect(0, 0, 800, 600);
        var layout = _calculator.Calculate(Observer, viewport, Guide(100, 0),
            ProjectFrom(new Point(400, 300), 10_000));

        Assert.Equal(100, AngularSeparation(
            layout.LeftBoundary!.BearingDegrees,
            layout.RightBoundary!.BearingDegrees));
        Assert.True(Distance(layout.LeftBoundary.ClippedEnd, layout.RightBoundary.ClippedEnd) > 600);
        AssertOnBoundary(layout.LeftBoundary.ClippedEnd, viewport);
        AssertOnBoundary(layout.RightBoundary.ClippedEnd, viewport);
        AssertShadeRegions(layout, FramingRaySegmentState.Visible);
    }

    [Theory]
    [InlineData(100, 2, 0)]
    [InlineData(198, 100, 90)]
    [InlineData(100, 198, 180)]
    [InlineData(2, 100, 270)]
    public void PinNearViewportEdge_CentreRayClipsToTheNearestEdge(double x, double y, double bearing)
    {
        var viewport = new Rect(0, 0, 200, 200);
        var layout = _calculator.Calculate(Observer, viewport, Guide(30, bearing),
            ProjectFrom(new Point(x, y), 10_000));

        AssertPointEqual(new Point(x, y), layout.CentreBearing!.ClippedStart, 0.001);
        AssertOnBoundary(layout.CentreBearing.ClippedEnd, viewport);
        AssertInside(layout.LeftBoundary!.ClippedStart, viewport);
        AssertInside(layout.RightBoundary!.ClippedStart, viewport);
    }

    [Fact]
    public void WedgeFromOffscreenPin_ClipsRaysAndShadeToVisibleViewport()
    {
        var viewport = new Rect(0, 0, 800, 600);
        var layout = _calculator.Calculate(Observer, viewport, Guide(20, 90),
            ProjectFrom(new Point(-50, 300), 10_000));

        AssertPointEqual(new Point(0, 300), layout.CentreBearing!.ClippedStart, 0.01);
        AssertPointEqual(new Point(800, 300), layout.CentreBearing.ClippedEnd, 0.01);
        var shade = Assert.Single(layout.ShadeRegions);
        Assert.Equal(FramingRaySegmentState.Visible, shade.State);
        Assert.True(shade.Points.Count >= 3);
        foreach (var point in shade.Points) AssertInside(point, viewport);
    }

    [Fact]
    public void WedgeFromDistantOffscreenPin_ExtendsShadeThroughViewport()
    {
        var viewport = new Rect(0, 0, 800, 600);
        var layout = _calculator.Calculate(Observer, viewport, Guide(2, 90),
            ProjectFrom(new Point(-5_000, 300), 10_000));

        var shade = Assert.Single(layout.ShadeRegions);
        Assert.True(shade.Points.Count >= 3);
        foreach (var point in shade.Points) AssertInside(point, viewport);
    }

    [Fact]
    public void WeatherLimitInsideViewport_SplitsEveryRayWithoutChangingItsBearing()
    {
        var viewport = new Rect(0, 0, 800, 600);
        var guide = Guide(50, 0);
        var layout = _calculator.Calculate(Observer, viewport, guide,
            ProjectFrom(new Point(400, 300), 1_000), WeatherLimit(8_000));

        Assert.Equal(2, layout.LeftBoundary!.Segments.Count);
        Assert.Equal(2, layout.CentreBearing!.Segments.Count);
        Assert.Equal(2, layout.RightBoundary!.Segments.Count);
        Assert.Equal(FramingRaySegmentState.Visible, layout.CentreBearing.Segments[0].State);
        Assert.Equal(FramingRaySegmentState.Limited, layout.CentreBearing.Segments[1].State);
        Assert.Equal(FramingLimitReason.WeatherVisibility, layout.CentreBearing.Segments[1].LimitingReason);
        Assert.Equal(guide.CentreBearingDegrees, layout.CentreBearing.BearingDegrees);
        Assert.NotNull(layout.Transition?.Centre);
        Assert.Equal(2, layout.ShadeRegions.Count);
        Assert.Equal(FramingRaySegmentState.Visible, layout.ShadeRegions[0].State);
        Assert.Equal(FramingRaySegmentState.Limited, layout.ShadeRegions[1].State);
    }

    [Fact]
    public void WeatherLimitBeyondViewport_LeavesVisibleGeometryUnsplit()
    {
        var viewport = new Rect(0, 0, 800, 600);
        var layout = _calculator.Calculate(Observer, viewport, Guide(50, 0),
            ProjectFrom(new Point(400, 300), 1_000), WeatherLimit(100_000));

        Assert.Single(layout.LeftBoundary!.Segments);
        Assert.Single(layout.CentreBearing!.Segments);
        Assert.Single(layout.RightBoundary!.Segments);
        Assert.Equal(FramingRaySegmentState.Visible, layout.CentreBearing.Segments[0].State);
        Assert.Null(layout.Transition?.Centre);
    }

    [Fact]
    public void WeatherTransitionUsesMapScaleWhenZoomChanges()
    {
        var viewport = new Rect(0, 0, 800, 600);
        var apex = new Point(400, 300);
        var guide = Guide(40, 0);
        var zoomedOut = _calculator.Calculate(Observer, viewport, guide,
            ProjectFrom(apex, 500), WeatherLimit(8_000));
        var zoomedIn = _calculator.Calculate(Observer, viewport, guide,
            ProjectFrom(apex, 2_000), WeatherLimit(8_000));

        var outDistance = Distance(apex, zoomedOut.Transition!.Centre!.Value);
        var inDistance = Distance(apex, zoomedIn.Transition!.Centre!.Value);
        Assert.InRange(inDistance / outDistance, 3.99, 4.01);
        AssertPointEqual(zoomedOut.CentreBearing!.ClippedEnd, zoomedIn.CentreBearing!.ClippedEnd, 0.01);
    }

    [Fact]
    public void VisibilityStylingDoesNotChangeClippedFovGeometry()
    {
        var viewport = new Rect(0, 0, 800, 600);
        var guide = Guide(70, 25);
        var project = ProjectFrom(new Point(400, 300), 1_000);
        var normal = _calculator.Calculate(Observer, viewport, guide, project);
        var limited = _calculator.Calculate(Observer, viewport, guide, project, WeatherLimit(6_000));

        Assert.Equal(normal.LeftBoundary!.BearingDegrees, limited.LeftBoundary!.BearingDegrees);
        Assert.Equal(normal.RightBoundary!.BearingDegrees, limited.RightBoundary!.BearingDegrees);
        Assert.Equal(normal.CentreBearing!.BearingDegrees, limited.CentreBearing!.BearingDegrees);
        AssertPointEqual(normal.LeftBoundary.ClippedStart, limited.LeftBoundary.ClippedStart, 0.001);
        AssertPointEqual(normal.LeftBoundary.ClippedEnd, limited.LeftBoundary.ClippedEnd, 0.001);
        AssertPointEqual(normal.RightBoundary.ClippedEnd, limited.RightBoundary.ClippedEnd, 0.001);
        AssertPointEqual(normal.CentreBearing.ClippedEnd, limited.CentreBearing.ClippedEnd, 0.001);
    }

    [Fact]
    public void TerrainObstructionMutesWholeGeometryWhileRetainingWeatherMetadata()
    {
        var visibility = WeatherLimit(8_000) with
        {
            IsTargetTerrainObstructed = true,
            TerrainClearanceDegrees = -2.4,
            TerrainHorizonDegrees = 12,
            Status = "Below terrain horizon by 2.4°"
        };
        var layout = _calculator.Calculate(Observer, new Rect(0, 0, 800, 600), Guide(50, 0),
            ProjectFrom(new Point(400, 300), 1_000), visibility);

        var centre = Assert.Single(layout.CentreBearing!.Segments);
        Assert.Equal(FramingRaySegmentState.Limited, centre.State);
        Assert.Equal(FramingLimitReason.TerrainHorizon, centre.LimitingReason);
        AssertShadeRegions(layout, FramingRaySegmentState.Limited);
        Assert.Equal(8_000, visibility.NearestRadialLimit!.DistanceMetres);
        Assert.Null(layout.Transition?.Centre);
    }

    private CameraFramingGuide Guide(double horizontalFov, double bearing) =>
        _guideCalculator.Calculate(new FieldOfView(horizontalFov, 40, 70), bearing, new CameraFramingSettings());

    private static FramingVisibilityAssessment WeatherLimit(double distanceMetres) => new(
        false,
        null,
        null,
        [new FramingRadialLimit(FramingLimitReason.WeatherVisibility, distanceMetres, $"Visibility {distanceMetres / 1_000:F1} km")],
        $"Weather visibility: {distanceMetres / 1_000:F1} km");

    private static Func<GeoCoordinate, Point?> ProjectFrom(Point apex, double pixelsPerDegree) => coordinate =>
        new Point(apex.X + coordinate.Longitude * pixelsPerDegree,
            apex.Y - coordinate.Latitude * pixelsPerDegree);

    private static void AssertShadeRegions(FramingWedgeLayout layout, FramingRaySegmentState expectedState)
    {
        var region = Assert.Single(layout.ShadeRegions);
        Assert.Equal(expectedState, region.State);
        Assert.True(region.Points.Count >= 3);
        foreach (var point in region.Points) AssertInside(point, new Rect(0, 0, 800, 600));
    }

    private static void AssertOnBoundary(Point point, Rect viewport)
    {
        AssertInside(point, viewport);
        var onBoundary = Near(point.X, viewport.Left) || Near(point.X, viewport.Right) ||
                         Near(point.Y, viewport.Top) || Near(point.Y, viewport.Bottom);
        Assert.True(onBoundary, $"Point {point} was not on viewport boundary {viewport}.");
    }

    private static void AssertInside(Point point, Rect viewport)
    {
        Assert.InRange(point.X, viewport.Left - 0.001, viewport.Right + 0.001);
        Assert.InRange(point.Y, viewport.Top - 0.001, viewport.Bottom + 0.001);
    }

    private static void AssertPointEqual(Point expected, Point actual, double tolerance)
    {
        Assert.InRange(actual.X, expected.X - tolerance, expected.X + tolerance);
        Assert.InRange(actual.Y, expected.Y - tolerance, expected.Y + tolerance);
    }

    private static bool Near(double first, double second) => Math.Abs(first - second) < 0.001;
    private static double Distance(Point first, Point second) =>
        Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Y - second.Y, 2));

    private static double AngularSeparation(double first, double second) =>
        Math.Min(Math.Abs(first - second), 360 - Math.Abs(first - second));
}
