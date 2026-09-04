using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;
using Avalonia.Media;
using Noctaxis.Desktop.Services;

namespace Noctaxis.Desktop.Tests;

public sealed class GeographicOverlayGeometryTests
{
    private static readonly GeoCoordinate Observer = new(53.61, -0.43);
    private readonly GeographicOverlayGeometryBuilder _builder = new();

    [Fact]
    public void WeatherLimitedConeColourIsTrueGrayscaleAndPreservesRequestedAlpha()
    {
        var grayscale = CameraOverlayColourPolicy.Grayscale(Color.FromArgb(220, 40, 170, 230), 91);

        Assert.Equal(91, grayscale.A);
        Assert.Equal(grayscale.R, grayscale.G);
        Assert.Equal(grayscale.G, grayscale.B);
        Assert.NotEqual(40, grayscale.R);
    }

    [Fact]
    public void MaximumRange_IsSingleNamed500KilometreValue()
    {
        Assert.Equal(500, MapOverlayGeometry.CameraOverlayMaxRangeKm);
        Assert.Equal(500_000, MapOverlayGeometry.MaximumRangeMetres);
    }

    [Fact]
    public void FiveHundredKilometreRay_KeepsEndpointWhileProjectedLengthChangesWithZoom()
    {
        var ray = _builder.BuildRay(new GeoRay(
            Observer, 72, MapOverlayGeometry.MaximumRangeMetres));
        var endpoint = ray.Coordinates[^1];
        Assert.InRange(Angles.GreatCircleDistanceMetres(Observer, endpoint), 499_999, 500_001);

        var regional = WebMercatorViewport.Create(Observer, 5, 256, 1200, 800);
        var local = WebMercatorViewport.Create(Observer, 8, 256, 1200, 800);
        var regionalLength = ProjectedLength(regional, Observer, endpoint);
        var localLength = ProjectedLength(local, Observer, endpoint);

        Assert.True(localLength > regionalLength * 7.9);
        AssertCoordinate(endpoint, ray.Coordinates[^1]);
    }

    [Fact]
    public void ChangingObserverMovesCameraAndCelestialGeographicOrigins()
    {
        var moved = new GeoCoordinate(-33.8688, 151.2093);
        var celestial = _builder.BuildRay(new GeoRay(moved, 125, MapOverlayGeometry.MaximumRangeMetres));
        var camera = _builder.BuildCameraOverlay(new GeoSector(
            moved, 125, 60, MapOverlayGeometry.MaximumRangeMetres));

        AssertCoordinate(moved, celestial.Coordinates[0]);
        AssertCoordinate(moved, camera.LeftBoundary[0].Coordinates[0]);
        AssertCoordinate(moved, camera.CentreBearing[0].Coordinates[0]);
        AssertCoordinate(moved, camera.RightBoundary[0].Coordinates[0]);
        Assert.NotEqual(Observer, celestial.Coordinates[0]);
    }

    [Fact]
    public void CelestialRayMatchesSuppliedCurrentAzimuth()
    {
        const double azimuth = 237.4;
        var ray = _builder.BuildRay(new GeoRay(Observer, azimuth, MapOverlayGeometry.MaximumRangeMetres));

        Assert.Equal(azimuth, ray.Ray.BearingDegrees, 10);
        Assert.Equal(azimuth, Angles.InitialBearing(ray.Coordinates[0], ray.Coordinates[1]), 8);
        Assert.True(ray.Coordinates.Count > 2, "Long celestial rays must be sampled rather than drawn to one endpoint.");
    }

    [Fact]
    public void CameraBoundariesUseCentrePlusAndMinusHalfHorizontalFov()
    {
        var overlay = _builder.BuildCameraOverlay(new GeoSector(
            Observer, 350, 40, MapOverlayGeometry.MaximumRangeMetres));

        Assert.Equal(330, overlay.Sector.LeftBearingDegrees, 10);
        Assert.Equal(10, overlay.Sector.RightBearingDegrees, 10);
        Assert.Equal(350, overlay.Sector.CentreBearingDegrees, 10);
    }

    [Fact]
    public void WeatherVisibilityChangesStyleWithoutTruncatingGeometry()
    {
        var overlay = _builder.BuildCameraOverlay(Sector(), WeatherLimit(25_000));

        Assert.Equal(2, overlay.FillRegions.Count);
        Assert.Equal(CameraOverlayEffect.None, overlay.FillRegions[0].Effect);
        Assert.Equal(CameraOverlayEffect.WeatherDesaturated, overlay.FillRegions[1].Effect);
        Assert.Equal(25_000, overlay.FillRegions[1].StartDistanceMetres);
        Assert.Equal(MapOverlayGeometry.MaximumRangeMetres, overlay.FillRegions[1].EndDistanceMetres);
        AssertBoundaryEndsAtMaximumRange(overlay.LeftBoundary, overlay.Sector.Origin);
        AssertBoundaryEndsAtMaximumRange(overlay.CentreBearing, overlay.Sector.Origin);
        AssertBoundaryEndsAtMaximumRange(overlay.RightBoundary, overlay.Sector.Origin);
    }

    [Fact]
    public void TerrainObstructionAddsVariableHatchWithoutTruncatingGeometry()
    {
        var sector = Sector();
        var visibility = VisibilityWithTerrain(
            new(sector.LeftBearingDegrees, true, 60_000),
            new(sector.CentreBearingDegrees, true, 95_000),
            new(sector.RightBearingDegrees, true, 130_000));

        var overlay = _builder.BuildCameraOverlay(sector, visibility);

        Assert.NotEmpty(overlay.TerrainHatchRegions);
        Assert.Equal(60_000, overlay.TerrainHatchRegions.Min(region => region.StartDistanceMetres));
        Assert.All(overlay.TerrainHatchRegions,
            region => Assert.Equal(MapOverlayGeometry.MaximumRangeMetres, region.EndDistanceMetres));
        AssertBoundaryEndsAtMaximumRange(overlay.CentreBearing, sector.Origin);
    }

    [Fact]
    public void AsymmetricTerrainTapersHatchToAClearSideWithoutTruncatingCone()
    {
        var sector = Sector();
        var visibility = VisibilityWithTerrain(
            new(sector.LeftBearingDegrees, true, 2_000),
            new(sector.CentreBearingDegrees, true, 8_000),
            new(sector.RightBearingDegrees, false));

        var overlay = _builder.BuildCameraOverlay(sector, visibility);

        Assert.NotEmpty(overlay.TerrainHatchRegions);
        Assert.Equal(2_000, overlay.TerrainHatchRegions.Min(region => region.StartDistanceMetres));
        Assert.Contains(overlay.TerrainSamples, sample => !sample.IsObstructed);
        Assert.All(overlay.TerrainHatchRegions,
            region => Assert.Equal(MapOverlayGeometry.MaximumRangeMetres, region.EndDistanceMetres));
        AssertBoundaryEndsAtMaximumRange(overlay.RightBoundary, sector.Origin);
    }

    [Fact]
    public void TerrainBeforeWeatherProducesColourThenHatchThenGrayscale()
    {
        var sector = Sector();
        var visibility = VisibilityWithTerrain(
            new(sector.LeftBearingDegrees, true, 20_000),
            new(sector.CentreBearingDegrees, true, 25_000),
            new(sector.RightBearingDegrees, true, 30_000)) with
        {
            WeatherVisibilityDistanceMetres = 40_000
        };

        var overlay = _builder.BuildCameraOverlay(sector, visibility);

        Assert.NotEmpty(overlay.TerrainHatchRegions);
        Assert.All(overlay.TerrainHatchRegions, hatch => Assert.Equal(40_000, hatch.EndDistanceMetres));
        Assert.Contains(overlay.FillRegions, region =>
            region.Effect == CameraOverlayEffect.WeatherDesaturated && region.StartDistanceMetres == 40_000);
    }

    [Fact]
    public void WeatherBeforeTerrainProducesColourThenGrayscaleThenHatch()
    {
        var sector = Sector();
        var visibility = VisibilityWithTerrain(
            new(sector.LeftBearingDegrees, true, 80_000),
            new(sector.CentreBearingDegrees, true, 100_000),
            new(sector.RightBearingDegrees, true, 120_000)) with
        {
            WeatherVisibilityDistanceMetres = 40_000
        };

        var overlay = _builder.BuildCameraOverlay(sector, visibility);

        Assert.NotEmpty(overlay.TerrainHatchRegions);
        Assert.All(overlay.TerrainHatchRegions, hatch =>
        {
            Assert.True(hatch.StartDistanceMetres > 40_000);
            Assert.Equal(MapOverlayGeometry.MaximumRangeMetres, hatch.EndDistanceMetres);
        });
    }

    [Fact]
    public void TerrainCrossingWeatherBoundarySplitsAtTheTransitionOrderingChange()
    {
        var sector = Sector();
        var visibility = VisibilityWithTerrain(
            new(sector.LeftBearingDegrees, true, 20_000),
            new(sector.CentreBearingDegrees, true, 60_000),
            new(sector.RightBearingDegrees, true, 80_000)) with
        {
            WeatherVisibilityDistanceMetres = 40_000
        };

        var overlay = _builder.BuildCameraOverlay(sector, visibility);

        Assert.Contains(overlay.TerrainHatchRegions, hatch => hatch.EndDistanceMetres == 40_000);
        Assert.Contains(overlay.TerrainHatchRegions,
            hatch => hatch.EndDistanceMetres == MapOverlayGeometry.MaximumRangeMetres);
    }

    [Fact]
    public void LegacyMultiIntervalSegmentsCannotPunchClearHolesAfterFirstObstruction()
    {
        var sector = Sector();
        HorizonVisibilitySegment[] intervals =
        [
            new(0, 10_000, HorizonVisibilityState.Visible),
            new(10_000, 30_000, HorizonVisibilityState.TerrainOccluded),
            new(30_000, 45_000, HorizonVisibilityState.Visible),
            new(45_000, MapOverlayGeometry.MaximumRangeMetres, HorizonVisibilityState.TerrainOccluded)
        ];
        var visibility = VisibilityWithTerrain(
            new(sector.LeftBearingDegrees, true, 10_000, VisibilitySegments: intervals),
            new(sector.CentreBearingDegrees, true, 10_000, VisibilitySegments: intervals),
            new(sector.RightBearingDegrees, true, 10_000, VisibilitySegments: intervals));

        var overlay = _builder.BuildCameraOverlay(sector, visibility);

        Assert.NotEmpty(overlay.TerrainHatchRegions);
        Assert.All(overlay.TerrainHatchRegions, region =>
        {
            Assert.Equal(10_000, region.StartDistanceMetres);
            Assert.Equal(MapOverlayGeometry.MaximumRangeMetres, region.EndDistanceMetres);
        });
        AssertBoundaryEndsAtMaximumRange(overlay.CentreBearing, sector.Origin);
    }

    [Fact]
    public void UniformGradualSharpAndVeryCloseBoundariesRemainRadiallyMonotonic()
    {
        double[][] cases =
        [
            [1_000, 1_000, 1_000],
            [500, 5_000, 25_000],
            [100, 80_000, 120],
            [15, 500, 100]
        ];
        foreach (var distances in cases)
        {
            var sector = Sector();
            var overlay = _builder.BuildCameraOverlay(sector, VisibilityWithTerrain(
                TerrainSample(sector, 0, distances[0]),
                TerrainSample(sector, 30, distances[1]),
                TerrainSample(sector, 60, distances[2])));

            AssertValidTerrainTopology(overlay);
        }
    }

    [Fact]
    public void ClearStateIsNotInterpolatedAsMaximumRangeObstruction()
    {
        var sector = Sector();
        var overlay = _builder.BuildCameraOverlay(sector, VisibilityWithTerrain(
            TerrainSample(sector, 0, 80),
            TerrainSample(sector, 30, null),
            TerrainSample(sector, 60, 120)));

        var clear = Assert.Single(overlay.TerrainSamples, sample => !sample.IsObstructed);
        Assert.Null(clear.ObstructionDistanceMetres);
        Assert.DoesNotContain(overlay.TerrainHatchRegions,
            region => region.StartDistanceMetres == MapOverlayGeometry.MaximumRangeMetres);
        AssertValidTerrainTopology(overlay);
    }

    [Fact]
    public void ObstructedClearObstructedCreatesOpenObserverConnectedCorridor()
    {
        var sector = Sector();
        var overlay = _builder.BuildCameraOverlay(sector, VisibilityWithTerrain(
            TerrainSample(sector, 0, 80),
            TerrainSample(sector, 20, 95),
            TerrainSample(sector, 30, null),
            TerrainSample(sector, 40, 110),
            TerrainSample(sector, 60, 120)));

        for (var distance = 100d; distance < sector.DistanceMetres; distance += 5_000)
            Assert.False(IsHatched(overlay, 30, distance));
        AssertValidTerrainTopology(overlay);
    }

    [Fact]
    public void AlternatingPathologicalStatesStillProduceAdditiveNonIntersectingCells()
    {
        var sector = Sector();
        var overlay = _builder.BuildCameraOverlay(sector, VisibilityWithTerrain(
            TerrainSample(sector, 0, 80),
            TerrainSample(sector, 10, null),
            TerrainSample(sector, 20, 100),
            TerrainSample(sector, 30, null),
            TerrainSample(sector, 40, 90),
            TerrainSample(sector, 50, null),
            TerrainSample(sector, 60, 120)));

        AssertValidTerrainTopology(overlay);
    }

    [Fact]
    public void NorthCrossingFovKeepsStrictlyIncreasingUnwrappedBearingOrder()
    {
        var sector = Sector(); // 350 degrees through north to 50 degrees.
        var overlay = _builder.BuildCameraOverlay(sector, VisibilityWithTerrain(
            TerrainSample(sector, 0, 100),
            TerrainSample(sector, 15, 200),
            TerrainSample(sector, 30, null),
            TerrainSample(sector, 45, 250),
            TerrainSample(sector, 60, 150)));

        Assert.Equal([350, 365, 380, 395, 410],
            overlay.TerrainSamples.Select(sample => sample.UnwrappedBearingDegrees).ToArray());
        AssertValidTerrainTopology(overlay);
    }

    [Fact]
    public void AllClearProducesNoHatchCells()
    {
        var sector = Sector();
        var overlay = _builder.BuildCameraOverlay(sector, VisibilityWithTerrain(
            TerrainSample(sector, 0, null),
            TerrainSample(sector, 30, null),
            TerrainSample(sector, 60, null)));

        Assert.Empty(overlay.TerrainHatchRegions);
        Assert.All(overlay.TerrainSamples, sample => Assert.False(sample.IsObstructed));
    }

    [Fact]
    public void AllBlockedNearObserverLeavesOnlyObserverConnectedClearRegion()
    {
        var sector = Sector();
        var overlay = _builder.BuildCameraOverlay(sector, VisibilityWithTerrain(
            TerrainSample(sector, 0, 15),
            TerrainSample(sector, 30, 20),
            TerrainSample(sector, 60, 18)));

        AssertValidTerrainTopology(overlay);
        Assert.All(overlay.TerrainSamples, sample => Assert.InRange(
            sample.ObstructionDistanceMetres!.Value, 0, sector.DistanceMetres));
    }

    [Fact]
    public void MissingEnvironmentalDataLeavesNormalFullLengthOverlay()
    {
        var overlay = _builder.BuildCameraOverlay(Sector(), new FramingVisibilityAssessment(
            false, null, null, null, "Visibility data unavailable"));

        var fill = Assert.Single(overlay.FillRegions);
        Assert.Equal(CameraOverlayEffect.None, fill.Effect);
        Assert.Equal(MapOverlayGeometry.MaximumRangeMetres, fill.EndDistanceMetres);
        Assert.Empty(overlay.TerrainHatchRegions);
        Assert.Single(overlay.CentreBearing);
    }

    [Fact]
    public void AngularTerrainObstructionWithoutFirstIntersectionUsesDocumentedNoOpHatchFallback()
    {
        var sector = Sector();
        var visibility = VisibilityWithTerrain(
            new(sector.LeftBearingDegrees, true),
            new(sector.CentreBearingDegrees, true),
            new(sector.RightBearingDegrees, true));

        var overlay = _builder.BuildCameraOverlay(sector, visibility);

        Assert.Empty(overlay.TerrainHatchRegions);
        AssertBoundaryEndsAtMaximumRange(overlay.CentreBearing, sector.Origin);
    }

    [Fact]
    public void AntimeridianRayRemainsNormalisedAndProjectsContinuously()
    {
        var origin = new GeoCoordinate(10, 179);
        var ray = _builder.BuildRay(new GeoRay(origin, 90, MapOverlayGeometry.MaximumRangeMetres));
        Assert.All(ray.Coordinates, coordinate => Assert.InRange(coordinate.Longitude, -180, 180));
        Assert.Contains(ray.Coordinates, coordinate => coordinate.Longitude < 0);

        var viewport = WebMercatorViewport.Create(origin, 6, 256, 1200, 800);
        MapPixelPoint? previous = null;
        double? previousWorldX = null;
        foreach (var coordinate in ray.Coordinates)
        {
            var projected = viewport.Project(coordinate.Latitude, coordinate.Longitude);
            if (previous is { } point)
                Assert.InRange(Distance(point, projected), 0, 20);
            previous = projected;

            var world = WebMercator.FromWgs84(coordinate);
            var continuousX = WebMercator.WrapXNear(world.X, previousWorldX ?? 0);
            if (previousWorldX is double priorX)
                Assert.InRange(Math.Abs(continuousX - priorX), 0, 20_000);
            previousWorldX = continuousX;
        }
    }

    private static GeoSector Sector() => new(
        Observer, 20, 60, MapOverlayGeometry.MaximumRangeMetres);

    private static FramingVisibilityAssessment WeatherLimit(double distanceMetres) => new(
        false,
        null,
        null,
        distanceMetres,
        "Weather visibility");

    private static FramingVisibilityAssessment VisibilityWithTerrain(
        params FramingTerrainObstructionSample[] samples) => new(
        true,
        -1,
        10,
        null,
        "Terrain obstructed",
        samples);

    private static FramingTerrainObstructionSample TerrainSample(
        GeoSector sector,
        double offsetDegrees,
        double? obstructionDistanceMetres) => new(
        Angles.NormaliseDegrees(sector.LeftBearingDegrees + offsetDegrees),
        obstructionDistanceMetres.HasValue,
        obstructionDistanceMetres);

    private static void AssertValidTerrainTopology(GeographicCameraOverlay overlay)
    {
        Assert.All(overlay.TerrainSamples, sample =>
        {
            Assert.InRange(sample.OffsetDegrees, 0, overlay.Sector.HorizontalFovDegrees);
            if (sample.ObstructionDistanceMetres is double distance)
                Assert.InRange(distance, 0, overlay.Sector.DistanceMetres);
        });
        for (var index = 1; index < overlay.TerrainSamples.Count; index++)
            Assert.True(overlay.TerrainSamples[index].UnwrappedBearingDegrees >
                        overlay.TerrainSamples[index - 1].UnwrappedBearingDegrees);

        foreach (var region in overlay.TerrainHatchRegions)
        {
            Assert.Equal(4, region.Coordinates.Count);
            Assert.False(SelfIntersects(overlay.Sector.Origin, region.Coordinates));
            foreach (var coordinate in region.Coordinates)
            {
                var radius = Angles.GreatCircleDistanceMetres(overlay.Sector.Origin, coordinate);
                var offset = Angles.NormaliseDegrees(
                    Angles.InitialBearing(overlay.Sector.Origin, coordinate) - overlay.Sector.LeftBearingDegrees);
                if (offset > 360 - 1e-6) offset = 0;
                Assert.InRange(radius, 0, overlay.Sector.DistanceMetres + 1);
                Assert.InRange(offset, 0, overlay.Sector.HorizontalFovDegrees + 1e-6);
            }
        }

        for (var offset = .25; offset < overlay.Sector.HorizontalFovDegrees; offset += .5)
        {
            var hasBecomeBlocked = false;
            for (var distance = 0d; distance < overlay.Sector.DistanceMetres - 1_000; distance += 2_000)
            {
                var blocked = IsHatched(overlay, offset, distance);
                Assert.False(hasBecomeBlocked && !blocked,
                    $"Hatch returned to clear at offset {offset:F2} degrees and {distance:F0} metres.");
                hasBecomeBlocked |= blocked;
            }
        }
    }

    private static bool IsHatched(GeographicCameraOverlay overlay, double offsetDegrees, double distanceMetres)
    {
        var bearing = overlay.Sector.LeftBearingDegrees + offsetDegrees;
        var point = new LocalPoint(
            distanceMetres * Math.Sin(bearing * Angles.DegreesToRadians),
            distanceMetres * Math.Cos(bearing * Angles.DegreesToRadians));
        return overlay.TerrainHatchRegions.Any(region => PointInPolygon(point,
            region.Coordinates.Select(coordinate => ToLocal(overlay.Sector.Origin, coordinate)).ToArray()));
    }

    private static bool SelfIntersects(GeoCoordinate origin, IReadOnlyList<GeoCoordinate> coordinates)
    {
        var points = coordinates.Select(coordinate => ToLocal(origin, coordinate)).ToArray();
        return SegmentsIntersect(points[0], points[1], points[2], points[3]) ||
               SegmentsIntersect(points[1], points[2], points[3], points[0]);
    }

    private static bool PointInPolygon(LocalPoint point, IReadOnlyList<LocalPoint> polygon)
    {
        var inside = false;
        for (int current = 0, previous = polygon.Count - 1; current < polygon.Count; previous = current++)
        {
            var first = polygon[previous];
            var second = polygon[current];
            if (PointOnSegment(point, first, second)) return true;
            if ((first.Y > point.Y) == (second.Y > point.Y)) continue;
            var intersectionX = (second.X - first.X) * (point.Y - first.Y) /
                                (second.Y - first.Y) + first.X;
            if (point.X < intersectionX) inside = !inside;
        }
        return inside;
    }

    private static bool PointOnSegment(LocalPoint point, LocalPoint first, LocalPoint second)
    {
        var length = Math.Sqrt(Math.Pow(second.X - first.X, 2) + Math.Pow(second.Y - first.Y, 2));
        if (length < 1e-9) return Distance(point, first) <= 1;
        var cross = Math.Abs((point.X - first.X) * (second.Y - first.Y) -
                             (point.Y - first.Y) * (second.X - first.X)) / length;
        if (cross > 1) return false;
        var dot = (point.X - first.X) * (second.X - first.X) +
                  (point.Y - first.Y) * (second.Y - first.Y);
        return dot >= -1 && dot <= length * length + 1;
    }

    private static bool SegmentsIntersect(LocalPoint a, LocalPoint b, LocalPoint c, LocalPoint d)
    {
        static double Cross(LocalPoint first, LocalPoint second, LocalPoint third) =>
            (second.X - first.X) * (third.Y - first.Y) -
            (second.Y - first.Y) * (third.X - first.X);
        var abC = Cross(a, b, c);
        var abD = Cross(a, b, d);
        var cdA = Cross(c, d, a);
        var cdB = Cross(c, d, b);
        return abC * abD < 0 && cdA * cdB < 0;
    }

    private static LocalPoint ToLocal(GeoCoordinate origin, GeoCoordinate coordinate)
    {
        var radius = Angles.GreatCircleDistanceMetres(origin, coordinate);
        var bearing = Angles.InitialBearing(origin, coordinate) * Angles.DegreesToRadians;
        return new LocalPoint(radius * Math.Sin(bearing), radius * Math.Cos(bearing));
    }

    private static double Distance(LocalPoint first, LocalPoint second) =>
        Math.Sqrt(Math.Pow(second.X - first.X, 2) + Math.Pow(second.Y - first.Y, 2));

    private readonly record struct LocalPoint(double X, double Y);

    private static void AssertBoundaryEndsAtMaximumRange(
        IReadOnlyList<GeographicPathSegment> segments,
        GeoCoordinate origin)
    {
        var final = segments[^1];
        Assert.Equal(MapOverlayGeometry.MaximumRangeMetres, final.EndDistanceMetres);
        Assert.InRange(Angles.GreatCircleDistanceMetres(origin, final.Coordinates[^1]), 499_999, 500_001);
    }

    private static double ProjectedLength(WebMercatorViewport viewport, GeoCoordinate first, GeoCoordinate second) =>
        Distance(viewport.Project(first.Latitude, first.Longitude), viewport.Project(second.Latitude, second.Longitude));

    private static void AssertCoordinate(GeoCoordinate expected, GeoCoordinate actual)
    {
        Assert.Equal(expected.Latitude, actual.Latitude, 10);
        Assert.Equal(expected.Longitude, actual.Longitude, 10);
        Assert.Equal(expected.ElevationMetres, actual.ElevationMetres, 10);
    }

    private static double Distance(MapPixelPoint first, MapPixelPoint second) =>
        Math.Sqrt(Math.Pow(second.X - first.X, 2) + Math.Pow(second.Y - first.Y, 2));
}
