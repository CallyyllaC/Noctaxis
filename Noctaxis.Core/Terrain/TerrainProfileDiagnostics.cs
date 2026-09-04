using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Noctaxis.Core.Domain;

namespace Noctaxis.Core.Terrain;

/// <summary>Explicit developer tooling for inspecting a calculated profile; never invoked per frame.</summary>
public static class TerrainProfileDiagnostics
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string ExportHorizonCsv(TerrainHorizonProfile profile)
    {
        var text = new StringBuilder();
        text.AppendLine("BearingDegrees,TerrainHorizonDegrees,HorizonFeatureMeters,TerrainObstructionMeters,TerrainState");
        foreach (var sample in profile.Samples)
        {
            var obstruction = profile.TerrainObstructionAt(sample.BearingDegrees);
            Append(text, sample.BearingDegrees);
            Append(text, sample.GroundHorizonElevationDegrees);
            Append(text, sample.GroundHorizonFeatureDistanceMetres);
            Append(text, obstruction.GroundFirstObstructionDistanceMetres);
            text.Append(profile.GroundHorizonState).AppendLine();
        }
        return text.ToString();
    }

    public static string ExportRadialCsv(TerrainHorizonProfile profile, double bearingDegrees)
    {
        var text = new StringBuilder();
        text.AppendLine("DistanceMeters,TerrainElevationMeters,CurvatureDropMeters,TerrainApparentAngleDegrees,TerrainSampleStatus");
        foreach (var point in profile.SightlineAt(bearingDegrees))
        {
            Append(text, point.DistanceMetres);
            Append(text, point.GroundElevationMetres);
            Append(text, point.CurvatureDropMetres);
            Append(text, point.GroundElevationAngleDegrees);
            text.Append(point.GroundStatus).AppendLine();
        }
        return text.ToString();
    }

    public static string CreateObserverSummary(TerrainHorizonProfile profile)
    {
        var ground = profile.GroundElevationAtObserver;
        var radial = profile.Samples.FirstOrDefault().Sightline;
        return string.Create(Invariant,
            $"Observer={profile.Observer.Latitude:F5},{profile.Observer.Longitude:F5}; " +
            $"TerrainObserver={Value(ground?.HasValue == true ? ground.Value : null)}m; " +
            $"ChosenGround={Value(profile.ChosenObserverGroundElevationMetres)}m; " +
            $"CameraHeight={profile.ObserverHeightAboveGroundMetres:F1}m; " +
            $"ObserverAbsolute={Value(profile.ObserverAbsoluteElevationMetres)}m; " +
            $"Confidence={profile.ObserverDatumConfidence}; " +
            $"RadialSamples={radial?.Count ?? 0}; " +
            $"Nearest={Value(radial is { Count: > 0 } ? radial[0].DistanceMetres : null)}m; " +
            $"TerrainSource={ground?.SourceId ?? "unavailable"}[{profile.GroundHorizonState}]");
    }

    public static string CreateBearingSummary(TerrainHorizonProfile profile, double bearingDegrees)
    {
        var line = profile.SightlineAt(bearingDegrees);
        var obstruction = profile.TerrainObstructionAt(bearingDegrees);
        var maximumGround = line.Where(point => point.GroundElevationMetres.HasValue)
            .Select(point => point.GroundElevationMetres!.Value).DefaultIfEmpty(double.NaN).Max();
        var groundAngle = profile.GroundAltitudeAt(bearingDegrees);
        var groundFeatureDistance = InterpolatedFeatureDistance(profile, bearingDegrees,
            sample => sample.GroundHorizonFeatureDistanceMetres);
        return string.Create(Invariant,
            $"Bearing={Angles.NormaliseDegrees(bearingDegrees):F1}; TerrainHorizon={Value(groundAngle)}deg; " +
            $"TerrainObstruction={Value(obstruction.GroundFirstObstructionDistanceMetres)}m; " +
            $"MaximumTerrainElevation={Value(double.IsNaN(maximumGround) ? null : maximumGround)}m; " +
            $"TerrainHorizonFeatureDistance={Value(groundFeatureDistance)}m; Samples={line.Count}; " +
            $"Nearest={Value(line.Count > 0 ? line[0].DistanceMetres : null)}m");
    }

    public static string CreateCompactRadialTable(TerrainHorizonProfile profile, double bearingDegrees,
        int maximumRows = 24)
    {
        var line = profile.SightlineAt(bearingDegrees);
        if (line.Count == 0) return "Distance | Terrain | Curvature | Terrain angle\n—";
        maximumRows = Math.Clamp(maximumRows, 2, line.Count);
        var indexes = Enumerable.Range(0, maximumRows)
            .Select(index => (int)Math.Round(index * (line.Count - 1d) / (maximumRows - 1)))
            .Distinct().ToArray();
        var text = new StringBuilder("Distance | Terrain | Curvature | Terrain angle\n");
        foreach (var index in indexes)
        {
            var point = line[index];
            text.AppendFormat(Invariant, "{0,8:F0} | {1,7} | {2,9:F2} | {3,13}\n",
                point.DistanceMetres, Value(point.GroundElevationMetres),
                point.CurvatureDropMetres, Value(point.GroundElevationAngleDegrees));
        }
        return text.ToString();
    }

    public static string CreateDebugSnapshot(TerrainHorizonProfile profile, double bearingDegrees)
    {
        var bearing = NearestBearingSample(profile, bearingDegrees);
        var line = bearing?.Sightline ?? [];
        var winningDistance = bearing?.EffectiveHorizonFeatureDistanceMetres;
        var winning = winningDistance is double distance
            ? line.OrderBy(point => Math.Abs(point.DistanceMetres - distance)).FirstOrDefault()
            : default;
        var winningCoordinate = winningDistance is double featureDistance && bearing is not null
            ? Angles.Destination(profile.Observer, bearing.Value.BearingDegrees, featureDistance)
            : (GeoCoordinate?)null;
        var diagnostics = profile.ObserverDiagnostics;
        var ground = diagnostics?.TerrainSample;
        var minimum = line.Count > 0 ? line[0].DistanceMetres : (double?)null;
        var maximum = line.Count > 0 ? line[^1].DistanceMetres : (double?)null;
        var curvature = winningDistance.HasValue ? HorizonService.CurvatureDrop(winningDistance.Value) : (double?)null;
        var geometricCurvature = winningDistance.HasValue
            ? winningDistance.Value * winningDistance.Value / (2 * HorizonService.MeanEarthRadiusMetres)
            : (double?)null;
        var refraction = geometricCurvature.HasValue && curvature.HasValue
            ? geometricCurvature.Value - curvature.Value : (double?)null;
        var resolvedStatus = diagnostics?.ResolvedStatus ?? TerrainSampleStatus.Unavailable;
        var text = new StringBuilder();
        text.AppendLine("TERRAIN DEBUG");
        text.AppendLine("Observer");
        text.AppendFormat(Invariant, "Latitude: {0:F6}\nLongitude: {1:F6}\n\n",
            profile.Observer.Latitude, profile.Observer.Longitude);
        text.AppendLine($"DEM provider: {ground?.Provider ?? profile.GroundElevationAtObserver?.SourceId ?? "Unavailable"}");
        text.AppendLine($"DEM tile: {ground?.Tile ?? "Unavailable"}");
        text.AppendLine($"DEM cell: {ground?.Cell ?? "Unavailable"}");
        text.AppendLine($"DEM resolution: {ground?.Resolution ?? "Unavailable"}");
        text.AppendLine($"Vertical datum: {ground?.VerticalDatum ?? "Unavailable"}");
        text.AppendLine("Raw sample(s):");
        if (ground?.RawSamples is { Count: > 0 } raw)
        {
            foreach (var sample in raw)
                text.AppendFormat(Invariant,
                    "  r{0} c{1} {2:F6},{3:F6}: {4} m [{5}], weight {6:F3}\n",
                    sample.Row, sample.Column, sample.Coordinate.Latitude, sample.Coordinate.Longitude,
                    Value(sample.RawElevationMetres), sample.Status, sample.Weight);
        }
        else text.AppendLine("  Unavailable");
        text.AppendLine($"Interpolated terrain ASL: {Value(ground?.InterpolatedElevationMetres)} m");
        text.AppendLine($"Sample status: {ground?.Status.ToString() ?? "Unavailable"}");
        text.AppendLine($"Resolved status: {resolvedStatus}");
        text.AppendLine($"Resolution policy: {diagnostics?.ResolutionPolicy ?? profile.ObserverDatumMessage ?? "Unavailable"}");
        text.AppendFormat(Invariant, "Camera height AGL: {0:F1} m\nFinal observer ASL: {1} m\n\n",
            profile.ObserverHeightAboveGroundMetres, Value(profile.ObserverAbsoluteElevationMetres));
        text.AppendLine("Horizon calculation");
        text.AppendFormat(Invariant, "Azimuth: {0:F1} degrees (profile bearing {1})\n",
            Angles.NormaliseDegrees(bearingDegrees), bearing is null ? "Unavailable" : $"{bearing.Value.BearingDegrees:F1} degrees");
        text.AppendLine($"Minimum sample distance: {Value(minimum)} m");
        text.AppendLine($"Maximum sample distance: {Value(maximum)} m");
        text.AppendLine("Sampling strategy: adaptive radial steps (15/40/100/250/500/1000/2000 m by range)");
        text.AppendLine($"Samples tested: {line.Count}");
        text.AppendLine("Winning obstruction sample:");
        text.AppendLine($"Latitude: {(winningCoordinate.HasValue ? winningCoordinate.Value.Latitude.ToString("F6", Invariant) : "Unavailable")}");
        text.AppendLine($"Longitude: {(winningCoordinate.HasValue ? winningCoordinate.Value.Longitude.ToString("F6", Invariant) : "Unavailable")}");
        text.AppendLine($"Distance: {Value(winningDistance)} m");
        text.AppendLine($"Terrain ASL: {Value(winning.GroundElevationMetres)} m [{winning.GroundStatus}]");
        text.AppendLine($"Height relative to observer: {Value(winning.GroundElevationMetres - profile.ObserverAbsoluteElevationMetres)} m");
        text.AppendLine($"Elevation angle: {Value(bearing?.EffectiveHorizonElevationDegrees)} degrees");
        text.AppendLine($"Curvature correction: -{Value(curvature)} m");
        text.AppendLine($"Refraction correction: +{Value(refraction)} m (7/6 effective Earth radius)");
        text.AppendLine($"Final horizon angle: {Value(bearing?.EffectiveHorizonElevationDegrees)} degrees");
        return text.ToString().TrimEnd();
    }

    [Conditional("DEBUG")]
    public static void LogProfile(ILogger logger, TerrainHorizonProfile profile, double bearingDegrees)
    {
        logger.LogDebug("{TerrainObserverDiagnostics}", CreateObserverSummary(profile));
        logger.LogDebug("{TerrainBearingDiagnostics}", CreateBearingSummary(profile, bearingDegrees));
        logger.LogDebug("Terrain radial profile:\n{TerrainRadialTable}",
            CreateCompactRadialTable(profile, bearingDegrees));
    }

    private static double? InterpolatedFeatureDistance(TerrainHorizonProfile profile, double bearingDegrees,
        Func<TerrainHorizonSample, double?> selector)
    {
        if (profile.Samples.Count == 0) return null;
        var position = Angles.NormaliseDegrees(bearingDegrees) / (360d / profile.Samples.Count);
        var lower = (int)Math.Floor(position) % profile.Samples.Count;
        var upper = (lower + 1) % profile.Samples.Count;
        var fraction = position - Math.Floor(position);
        var left = selector(profile.Samples[lower]);
        var right = selector(profile.Samples[upper]);
        return left.HasValue && right.HasValue ? left + (right - left) * fraction : left ?? right;
    }

    public static TerrainHorizonSample? NearestBearingSample(TerrainHorizonProfile profile,
        double bearingDegrees)
    {
        if (profile.Samples.Count == 0) return null;
        var spacing = 360d / profile.Samples.Count;
        var index = (int)Math.Round(Angles.NormaliseDegrees(bearingDegrees) / spacing) % profile.Samples.Count;
        return profile.Samples[index];
    }

    private static string Value(double? value) => value.HasValue && double.IsFinite(value.Value)
        ? value.Value.ToString("0.###", Invariant)
        : "—";

    private static void Append(StringBuilder text, double? value, bool comma = true)
    {
        if (value.HasValue && double.IsFinite(value.Value))
            text.Append(value.Value.ToString("0.########", Invariant));
        if (comma) text.Append(',');
    }
}
