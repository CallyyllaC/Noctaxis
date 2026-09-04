using Noctaxis.Core.Domain;
using Noctaxis.Core.Calculations;

namespace Noctaxis.Desktop.Controls;

public static class MapOverlayGeometry
{
    public const double CameraOverlayMaxRangeKm = LocalHorizonCalculator.MaximumTerrainCastDistanceKilometres;
    public const double MaximumRangeMetres = LocalHorizonCalculator.MaximumTerrainCastDistanceMetres;
    public const double GeodesicSampleSpacingMetres = 10_000;
    public const double SectorBearingSampleStepDegrees = 2;
}

public readonly record struct GeoRay(
    GeoCoordinate Origin,
    double BearingDegrees,
    double DistanceMetres);

public readonly record struct GeoSector(
    GeoCoordinate Origin,
    double CentreBearingDegrees,
    double HorizontalFovDegrees,
    double DistanceMetres)
{
    public double LeftBearingDegrees => Angles.NormaliseDegrees(CentreBearingDegrees - HorizontalFovDegrees / 2);
    public double RightBearingDegrees => Angles.NormaliseDegrees(CentreBearingDegrees + HorizontalFovDegrees / 2);
}

[Flags]
public enum CameraOverlayEffect
{
    None = 0,
    WeatherDesaturated = 1,
    TerrainHatch = 2
}

public sealed record GeographicRayGeometry(
    GeoRay Ray,
    IReadOnlyList<GeoCoordinate> Coordinates);

public sealed record GeographicPathSegment(
    double StartDistanceMetres,
    double EndDistanceMetres,
    CameraOverlayEffect Effect,
    IReadOnlyList<GeoCoordinate> Coordinates);

public sealed record GeographicOverlayRegion(
    CameraOverlayEffect Effect,
    IReadOnlyList<GeoCoordinate> Coordinates,
    double StartDistanceMetres,
    double EndDistanceMetres);

public sealed record TerrainConeRenderSample(
    double BearingDegrees,
    double UnwrappedBearingDegrees,
    double OffsetDegrees,
    bool IsObstructed,
    double? ObstructionDistanceMetres);

public sealed record GeographicCameraOverlay(
    GeoSector Sector,
    IReadOnlyList<GeographicPathSegment> LeftBoundary,
    IReadOnlyList<GeographicPathSegment> CentreBearing,
    IReadOnlyList<GeographicPathSegment> RightBoundary,
    IReadOnlyList<GeographicOverlayRegion> FillRegions,
    IReadOnlyList<GeographicOverlayRegion> TerrainHatchRegions,
    IReadOnlyList<TerrainConeRenderSample> TerrainSamples);

public sealed record GeographicCameraOutline(
    GeoSector Sector,
    IReadOnlyList<GeographicPathSegment> LeftBoundary,
    IReadOnlyList<GeographicPathSegment> CentreBearing,
    IReadOnlyList<GeographicPathSegment> RightBoundary);

/// <summary>
/// Generates reusable great-circle geometry. The result contains only geographic coordinates and
/// environmental styling metadata; map pan and zoom therefore require projection, not rebuilding.
/// </summary>
public sealed class GeographicOverlayGeometryBuilder
{
    public GeographicRayGeometry BuildRay(GeoRay ray)
    {
        ValidateDistance(ray.DistanceMetres);
        return new GeographicRayGeometry(
            ray with { BearingDegrees = Angles.NormaliseDegrees(ray.BearingDegrees) },
            SampleRayRange(ray.Origin, ray.BearingDegrees, 0, ray.DistanceMetres));
    }

    public GeographicCameraOverlay BuildCameraOverlay(
        GeoSector sector,
        FramingVisibilityAssessment? visibility = null)
    {
        ValidateDistance(sector.DistanceMetres);
        if (!double.IsFinite(sector.HorizontalFovDegrees) || sector.HorizontalFovDegrees is <= 0 or >= 180)
            throw new ArgumentOutOfRangeException(nameof(sector), "Horizontal field of view must be between 0 and 180 degrees.");

        var normalised = sector with
        {
            CentreBearingDegrees = Angles.NormaliseDegrees(sector.CentreBearingDegrees),
            HorizontalFovDegrees = Math.Abs(sector.HorizontalFovDegrees)
        };
        var weatherDistance = ValidWeatherDistance(visibility, normalised.DistanceMetres);
        var fills = BuildFillRegions(normalised, weatherDistance);
        var terrain = BuildTerrainHatchGeometry(normalised, visibility, weatherDistance);

        return new GeographicCameraOverlay(
            normalised,
            BuildBoundary(normalised.Origin, normalised.LeftBearingDegrees, normalised.DistanceMetres, weatherDistance),
            BuildBoundary(normalised.Origin, normalised.CentreBearingDegrees, normalised.DistanceMetres, weatherDistance),
            BuildBoundary(normalised.Origin, normalised.RightBearingDegrees, normalised.DistanceMetres, weatherDistance),
            fills,
            terrain.Regions,
            terrain.Samples);
    }

    /// <summary>
    /// Builds only the three low-cardinality geographic cone paths. Continuous fill, weather and
    /// terrain effects are handled by <see cref="EnvironmentalOverlayRenderer"/>.
    /// </summary>
    public GeographicCameraOutline BuildCameraOutline(
        GeoSector sector,
        FramingVisibilityAssessment? visibility = null)
    {
        ValidateDistance(sector.DistanceMetres);
        if (!double.IsFinite(sector.HorizontalFovDegrees) || sector.HorizontalFovDegrees is <= 0 or >= 180)
            throw new ArgumentOutOfRangeException(nameof(sector), "Horizontal field of view must be between 0 and 180 degrees.");
        var normalised = sector with
        {
            CentreBearingDegrees = Angles.NormaliseDegrees(sector.CentreBearingDegrees),
            HorizontalFovDegrees = Math.Abs(sector.HorizontalFovDegrees)
        };
        var weatherDistance = ValidWeatherDistance(visibility, normalised.DistanceMetres);
        return new GeographicCameraOutline(
            normalised,
            BuildBoundary(normalised.Origin, normalised.LeftBearingDegrees, normalised.DistanceMetres, weatherDistance),
            BuildBoundary(normalised.Origin, normalised.CentreBearingDegrees, normalised.DistanceMetres, weatherDistance),
            BuildBoundary(normalised.Origin, normalised.RightBearingDegrees, normalised.DistanceMetres, weatherDistance));
    }

    /// <summary>
    /// Builds the continuous geographic camera sector independently of environmental obstruction
    /// effects, so its apex always remains the observer coordinate.
    /// </summary>
    public IReadOnlyList<GeoCoordinate> BuildCameraBaseFill(GeoSector sector)
    {
        ValidateDistance(sector.DistanceMetres);
        if (!double.IsFinite(sector.HorizontalFovDegrees) || sector.HorizontalFovDegrees is <= 0 or >= 180)
            throw new ArgumentOutOfRangeException(nameof(sector), "Horizontal field of view must be between 0 and 180 degrees.");
        var normalised = sector with
        {
            CentreBearingDegrees = Angles.NormaliseDegrees(sector.CentreBearingDegrees),
            HorizontalFovDegrees = Math.Abs(sector.HorizontalFovDegrees)
        };
        return SampleSectorBand(normalised, 0, normalised.DistanceMetres);
    }

    private static IReadOnlyList<GeographicPathSegment> BuildBoundary(
        GeoCoordinate origin,
        double bearingDegrees,
        double maximumDistanceMetres,
        double? weatherDistanceMetres)
    {
        if (weatherDistanceMetres is not double weather)
        {
            return
            [
                new GeographicPathSegment(0, maximumDistanceMetres, CameraOverlayEffect.None,
                    SampleRayRange(origin, bearingDegrees, 0, maximumDistanceMetres))
            ];
        }

        return
        [
            new GeographicPathSegment(0, weather, CameraOverlayEffect.None,
                SampleRayRange(origin, bearingDegrees, 0, weather)),
            new GeographicPathSegment(weather, maximumDistanceMetres, CameraOverlayEffect.WeatherDesaturated,
                SampleRayRange(origin, bearingDegrees, weather, maximumDistanceMetres))
        ];
    }

    private static IReadOnlyList<GeographicOverlayRegion> BuildFillRegions(
        GeoSector sector,
        double? weatherDistanceMetres)
    {
        if (weatherDistanceMetres is not double weather)
        {
            return
            [
                new GeographicOverlayRegion(CameraOverlayEffect.None,
                    SampleSectorBand(sector, 0, sector.DistanceMetres), 0, sector.DistanceMetres)
            ];
        }

        return
        [
            new GeographicOverlayRegion(CameraOverlayEffect.None,
                SampleSectorBand(sector, 0, weather), 0, weather),
            new GeographicOverlayRegion(CameraOverlayEffect.WeatherDesaturated,
                SampleSectorBand(sector, weather, sector.DistanceMetres), weather, sector.DistanceMetres)
        ];
    }

    private static TerrainHatchGeometry BuildTerrainHatchGeometry(
        GeoSector sector,
        FramingVisibilityAssessment? visibility,
        double? weatherDistanceMetres)
    {
        if (visibility is null || visibility.EffectiveTerrainObstructions.Count < 2)
            return new TerrainHatchGeometry([], []);

        var samples = new List<TerrainConeRenderSample>();
        foreach (var sample in visibility.EffectiveTerrainObstructions)
        {
            var offset = Angles.NormaliseDegrees(sample.BearingDegrees - sector.LeftBearingDegrees);
            if (offset > sector.HorizontalFovDegrees + 1e-7) continue;
            var obstructed = TryObstructionDistance(sample, sector.DistanceMetres, out var distance);
            samples.Add(new TerrainConeRenderSample(
                Angles.NormaliseDegrees(sample.BearingDegrees),
                sector.LeftBearingDegrees + offset,
                offset,
                obstructed,
                obstructed ? distance : null));
        }
        samples.Sort(static (left, right) => left.OffsetDegrees.CompareTo(right.OffsetDegrees));
        RemoveDuplicateBearings(samples);

        var regions = new List<GeographicOverlayRegion>(Math.Max(0, samples.Count - 1));
        for (var index = 0; index < samples.Count - 1; index++)
        {
            var left = samples[index];
            var right = samples[index + 1];
            if (!left.IsObstructed && !right.IsObstructed) continue;

            if (left.IsObstructed && right.IsObstructed)
            {
                AddOrderedTerrainCells(sector, left.UnwrappedBearingDegrees, right.UnwrappedBearingDegrees,
                    left.ObstructionDistanceMetres!.Value, right.ObstructionDistanceMetres!.Value,
                    weatherDistanceMetres, regions);
                continue;
            }

            // Clear is a state, never a synthetic obstruction at the cone's maximum range. The
            // upstream sampler refines state changes; the midpoint of the narrow bracket is the
            // angular edge of the blocked region.
            var transitionBearing = (left.UnwrappedBearingDegrees + right.UnwrappedBearingDegrees) / 2;
            if (left.IsObstructed)
            {
                AddOrderedTerrainCells(sector, left.UnwrappedBearingDegrees, transitionBearing,
                    left.ObstructionDistanceMetres!.Value, left.ObstructionDistanceMetres.Value,
                    weatherDistanceMetres, regions);
            }
            else
            {
                AddOrderedTerrainCells(sector, transitionBearing, right.UnwrappedBearingDegrees,
                    right.ObstructionDistanceMetres!.Value, right.ObstructionDistanceMetres.Value,
                    weatherDistanceMetres, regions);
            }
        }
        return new TerrainHatchGeometry(regions, samples);
    }

    private static void RemoveDuplicateBearings(List<TerrainConeRenderSample> samples)
    {
        for (var index = samples.Count - 1; index > 0; index--)
        {
            if (Math.Abs(samples[index].OffsetDegrees - samples[index - 1].OffsetDegrees) > 1e-7) continue;
            // Prefer a valid obstruction at a duplicate bearing, and otherwise retain one sample.
            if (!samples[index].IsObstructed || samples[index - 1].IsObstructed)
                samples.RemoveAt(index);
            else
                samples.RemoveAt(index - 1);
        }
    }

    /// <summary>
    /// Terrain and weather are sequential distance transitions, rather than permanently stacked effects.
    /// The later transition along a bearing owns the cone treatment from that point onwards.
    /// </summary>
    private static void AddOrderedTerrainCells(
        GeoSector sector,
        double leftBearingDegrees,
        double rightBearingDegrees,
        double leftStartDistanceMetres,
        double rightStartDistanceMetres,
        double? weatherDistanceMetres,
        ICollection<GeographicOverlayRegion> regions)
    {
        if (weatherDistanceMetres is not double weather)
        {
            AddCells(leftBearingDegrees, rightBearingDegrees, leftStartDistanceMetres,
                rightStartDistanceMetres, sector.DistanceMetres);
            return;
        }

        var leftBeforeWeather = leftStartDistanceMetres < weather;
        var rightBeforeWeather = rightStartDistanceMetres < weather;
        if (leftBeforeWeather == rightBeforeWeather)
        {
            AddCells(leftBearingDegrees, rightBearingDegrees, leftStartDistanceMetres,
                rightStartDistanceMetres, leftBeforeWeather ? weather : sector.DistanceMetres);
            return;
        }

        var fraction = (weather - leftStartDistanceMetres) /
                       (rightStartDistanceMetres - leftStartDistanceMetres);
        var sweep = Angles.NormaliseDegrees(rightBearingDegrees - leftBearingDegrees);
        var crossingBearing = leftBearingDegrees + sweep * fraction;

        if (leftBeforeWeather)
        {
            AddCells(leftBearingDegrees, crossingBearing, leftStartDistanceMetres, weather, weather);
            AddCells(crossingBearing, rightBearingDegrees, weather, rightStartDistanceMetres,
                sector.DistanceMetres);
        }
        else
        {
            AddCells(leftBearingDegrees, crossingBearing, leftStartDistanceMetres, weather,
                sector.DistanceMetres);
            AddCells(crossingBearing, rightBearingDegrees, weather, rightStartDistanceMetres, weather);
        }

        void AddCells(double startBearing, double endBearing, double leftStart, double rightStart, double endDistance)
        {
            var sweep = endBearing - startBearing;
            if (sweep <= 1e-7 || endDistance <= Math.Min(leftStart, rightStart)) return;
            var count = Math.Max(1, (int)Math.Ceiling(sweep /
                                                      MapOverlayGeometry.SectorBearingSampleStepDegrees));
            for (var index = 0; index < count; index++)
            {
                var firstFraction = index / (double)count;
                var secondFraction = (index + 1d) / count;
                var firstBearing = startBearing + sweep * firstFraction;
                var secondBearing = startBearing + sweep * secondFraction;
                var firstDistance = leftStart + (rightStart - leftStart) * firstFraction;
                var secondDistance = leftStart + (rightStart - leftStart) * secondFraction;
                regions.Add(new GeographicOverlayRegion(
                    CameraOverlayEffect.TerrainHatch,
                    SampleTerrainCell(sector.Origin, firstBearing, secondBearing,
                        firstDistance, secondDistance, endDistance),
                    Math.Min(firstDistance, secondDistance),
                    endDistance));
            }
        }
    }

    private sealed record TerrainHatchGeometry(
        IReadOnlyList<GeographicOverlayRegion> Regions,
        IReadOnlyList<TerrainConeRenderSample> Samples);

    private static IReadOnlyList<GeoCoordinate> SampleSectorBand(
        GeoSector sector,
        double startDistanceMetres,
        double endDistanceMetres)
    {
        var points = new List<GeoCoordinate>();
        var leftRay = SampleRayRange(sector.Origin, sector.LeftBearingDegrees, startDistanceMetres, endDistanceMetres);
        points.AddRange(leftRay);

        var outerArc = SampleArc(sector.Origin, sector.LeftBearingDegrees,
            sector.HorizontalFovDegrees, endDistanceMetres);
        for (var index = 1; index < outerArc.Count; index++) points.Add(outerArc[index]);

        var rightRay = SampleRayRange(sector.Origin, sector.RightBearingDegrees, startDistanceMetres, endDistanceMetres);
        for (var index = rightRay.Count - 2; index >= 0; index--) points.Add(rightRay[index]);

        if (startDistanceMetres > 0)
        {
            var innerArc = SampleArc(sector.Origin, sector.RightBearingDegrees,
                -sector.HorizontalFovDegrees, startDistanceMetres);
            for (var index = 1; index < innerArc.Count; index++) points.Add(innerArc[index]);
        }
        return points;
    }

    private static IReadOnlyList<GeoCoordinate> SampleTerrainCell(
        GeoCoordinate origin,
        double leftBearingDegrees,
        double rightBearingDegrees,
        double leftStartDistanceMetres,
        double rightStartDistanceMetres,
        double endDistanceMetres)
        =>
        [
            Angles.Destination(origin, leftBearingDegrees,
                Math.Clamp(leftStartDistanceMetres, 0, endDistanceMetres)),
            Angles.Destination(origin, rightBearingDegrees,
                Math.Clamp(rightStartDistanceMetres, 0, endDistanceMetres)),
            Angles.Destination(origin, rightBearingDegrees, endDistanceMetres),
            Angles.Destination(origin, leftBearingDegrees, endDistanceMetres)
        ];

    private static IReadOnlyList<GeoCoordinate> SampleRayRange(
        GeoCoordinate origin,
        double bearingDegrees,
        double startDistanceMetres,
        double endDistanceMetres)
    {
        var span = endDistanceMetres - startDistanceMetres;
        var count = Math.Max(1, (int)Math.Ceiling(span / MapOverlayGeometry.GeodesicSampleSpacingMetres));
        var points = new GeoCoordinate[count + 1];
        for (var index = 0; index <= count; index++)
        {
            var distance = startDistanceMetres + span * index / count;
            points[index] = distance == 0 ? origin : Angles.Destination(origin, bearingDegrees, distance);
        }
        return points;
    }

    private static IReadOnlyList<GeoCoordinate> SampleArc(
        GeoCoordinate origin,
        double startBearingDegrees,
        double sweepDegrees,
        double distanceMetres)
    {
        var count = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweepDegrees) /
                                                  MapOverlayGeometry.SectorBearingSampleStepDegrees));
        var points = new GeoCoordinate[count + 1];
        for (var index = 0; index <= count; index++)
            points[index] = Angles.Destination(origin, startBearingDegrees + sweepDegrees * index / count, distanceMetres);
        return points;
    }

    private static double? ValidWeatherDistance(FramingVisibilityAssessment? visibility, double maximumDistanceMetres)
    {
        var distance = visibility?.WeatherVisibilityDistanceMetres;
        return distance is double value && double.IsFinite(value) && value > 0 && value < maximumDistanceMetres
            ? value
            : null;
    }

    private static bool TryObstructionDistance(
        FramingTerrainObstructionSample sample,
        double maximumDistanceMetres,
        out double distanceMetres)
    {
        distanceMetres = sample.FirstObstructionDistanceMetres ?? 0;
        return sample.IsObstructed && double.IsFinite(distanceMetres) &&
               distanceMetres > 0 && distanceMetres < maximumDistanceMetres;
    }

    private static void ValidateDistance(double distanceMetres)
    {
        if (!double.IsFinite(distanceMetres) || distanceMetres <= 0)
            throw new ArgumentOutOfRangeException(nameof(distanceMetres));
    }
}
