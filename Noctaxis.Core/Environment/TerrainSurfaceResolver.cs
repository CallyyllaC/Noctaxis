using Microsoft.Extensions.Logging;
using Noctaxis.Core.Domain;
using NodaTime;

namespace Noctaxis.Core.Environment;

public enum TerrainWaterBodyKind
{
    NotWater,
    PermanentWaterUnspecified,
    Ocean,
    InlandWater
}

public enum TerrainSurfaceResolutionReason
{
    RawTerrainLand,
    RawTerrainClassificationUnavailable,
    WaterElevationPreserved,
    OceanBathymetryAdjustedToMeanSeaLevel,
    PermanentWaterBathymetryAdjustedToMeanSeaLevel,
    TerrainUnavailable
}

public readonly record struct TerrainSurfaceResolution(
    double? RawTerrainElevationMetres,
    double? SurfaceElevationMetres,
    LandCoverClass? Classification,
    TerrainWaterBodyKind WaterBodyKind,
    TerrainSurfaceResolutionReason Reason,
    bool WasAdjusted)
{
    public bool IsWater => WaterBodyKind is not TerrainWaterBodyKind.NotWater;
}

public sealed record TerrainSurfaceSampleResult(
    EnvironmentalValue<double> RawTerrain,
    EnvironmentalValue<double> SurfaceElevation,
    EnvironmentalValue<LandCoverClass> Classification,
    TerrainSurfaceResolution Resolution,
    ElevationSampleDiagnostics RawTerrainDiagnostics);

public sealed record TerrainSurfaceBatchResult(
    EnvironmentalDataState State,
    IReadOnlyList<double?> RawTerrainElevationsMetres,
    IReadOnlyList<double?> SurfaceElevationsMetres,
    IReadOnlyList<LandCoverClass?> Classifications,
    IReadOnlyList<TerrainSurfaceResolutionReason> ResolutionReasons,
    IReadOnlyList<bool> AdjustedSamples,
    IReadOnlyList<TerrainSampleStatus> SampleStatuses,
    string Message)
{
    public TerrainSampleStatus StatusAt(int index) => SampleStatuses[index];
}

public sealed record TerrainSurfaceClassificationBatch(
    EnvironmentalDataState State,
    IReadOnlyList<LandCoverClass?> Classifications,
    IReadOnlyList<TerrainWaterBodyKind> WaterBodyKinds,
    string Message);

public interface ITerrainSurfaceResolver
{
    Task<TerrainSurfaceSampleResult> GetSurfaceSampleAsync(GeoCoordinate coordinate,
        CancellationToken cancellationToken);
    Task<TerrainSurfaceBatchResult> GetSurfaceElevationsAsync(IReadOnlyList<GeoCoordinate> coordinates,
        CancellationToken cancellationToken);
    Task<TerrainSurfaceClassificationBatch> GetClassificationsAsync(
        IReadOnlyList<GeoCoordinate> coordinates, CancellationToken cancellationToken);
    Task<TerrainSurfaceBatchResult> GetSurfaceElevationsAsync(IReadOnlyList<GeoCoordinate> coordinates,
        TerrainSurfaceClassificationBatch classifications, CancellationToken cancellationToken);
    Task PreloadAsync(IReadOnlyList<GeoCoordinate> coordinates, CancellationToken cancellationToken);
}

/// <summary>
/// Resolves the physical line-of-sight surface from the canonical Terrarium elevation and
/// WorldCover classification. WorldCover supplies classification only; it never supplies height.
/// </summary>
public sealed class TerrainSurfaceResolver(
    ITerrainElevationProvider terrain,
    ILandCoverProvider landCover,
    ILogger<TerrainSurfaceResolver> logger) : ITerrainSurfaceResolver
{
    public const string SourceId = "terrarium-worldcover-surface";
    public const string SourceVersion = "elevation-tiles-prod+worldcover-2021-v200";

    public Task PreloadAsync(IReadOnlyList<GeoCoordinate> coordinates,
        CancellationToken cancellationToken) => terrain.PreloadAsync(coordinates, cancellationToken);

    public async Task<TerrainSurfaceSampleResult> GetSurfaceSampleAsync(GeoCoordinate coordinate,
        CancellationToken cancellationToken)
    {
        var rawTask = terrain.GetElevationSampleAsync(coordinate, cancellationToken);
        var coverTask = SafeLandCoverAsync(coordinate, cancellationToken);
        await Task.WhenAll(rawTask, coverTask).ConfigureAwait(false);
        var raw = await rawTask.ConfigureAwait(false);
        var cover = await coverTask.ConfigureAwait(false);
        var resolution = Resolve(raw.Value.HasValue ? raw.Value.Value : null,
            cover.HasValue ? cover.Value : null,
            cover.State == EnvironmentalDataState.Water ? TerrainWaterBodyKind.Ocean : null);
        var surface = CreateSurfaceValue(raw.Value, resolution);
        LogAdjustment(coordinate, resolution);
        return new TerrainSurfaceSampleResult(raw.Value, surface, cover, resolution, raw.Diagnostics);
    }

    public async Task<TerrainSurfaceBatchResult> GetSurfaceElevationsAsync(
        IReadOnlyList<GeoCoordinate> coordinates, CancellationToken cancellationToken)
    {
        var classifications = await GetClassificationsAsync(coordinates, cancellationToken).ConfigureAwait(false);
        return await GetSurfaceElevationsAsync(coordinates, classifications, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<TerrainSurfaceClassificationBatch> GetClassificationsAsync(
        IReadOnlyList<GeoCoordinate> coordinates, CancellationToken cancellationToken)
    {
        var cover = await SafeLandCoversAsync(coordinates, cancellationToken).ConfigureAwait(false);
        var kinds = Enumerable.Range(0, coordinates.Count).Select(cover.WaterBodyKindAt).ToArray();
        return new TerrainSurfaceClassificationBatch(cover.State, cover.Classifications, kinds, cover.Message);
    }

    public async Task<TerrainSurfaceBatchResult> GetSurfaceElevationsAsync(
        IReadOnlyList<GeoCoordinate> coordinates, TerrainSurfaceClassificationBatch classifications,
        CancellationToken cancellationToken)
    {
        var raw = await terrain.GetElevationsAsync(coordinates, cancellationToken).ConfigureAwait(false);
        if (raw.ElevationsMetres.Count != coordinates.Count ||
            classifications.Classifications.Count != coordinates.Count ||
            classifications.WaterBodyKinds.Count != coordinates.Count)
            throw new InvalidDataException("Terrain surface input batch length did not match its request.");

        var surfaces = new double?[coordinates.Count];
        var reasons = new TerrainSurfaceResolutionReason[coordinates.Count];
        var adjusted = new bool[coordinates.Count];
        var statuses = new TerrainSampleStatus[coordinates.Count];
        var adjustedCount = 0;
        for (var index = 0; index < coordinates.Count; index++)
        {
            var resolution = Resolve(raw.ElevationsMetres[index], classifications.Classifications[index],
                classifications.WaterBodyKinds[index]);
            surfaces[index] = resolution.SurfaceElevationMetres;
            reasons[index] = resolution.Reason;
            adjusted[index] = resolution.WasAdjusted;
            if (resolution.WasAdjusted) adjustedCount++;
            statuses[index] = !resolution.SurfaceElevationMetres.HasValue
                ? raw.StatusAt(index)
                : resolution.IsWater ? TerrainSampleStatus.Water : TerrainSampleStatus.Valid;
        }

        var available = surfaces.Count(value => value.HasValue);
        var state = available == 0 ? EnvironmentalDataState.Unavailable :
            available == surfaces.Length ? EnvironmentalDataState.Available : EnvironmentalDataState.Partial;
        return new TerrainSurfaceBatchResult(state, raw.ElevationsMetres, surfaces,
            classifications.Classifications, reasons, adjusted, statuses,
            adjustedCount == 0 ? "Terrarium physical-surface batch." :
            $"Terrarium physical-surface batch corrected {adjustedCount} bathymetric water sample(s).");
    }

    public static TerrainSurfaceResolution Resolve(double? rawTerrainElevationMetres,
        LandCoverClass? classification, TerrainWaterBodyKind? classifiedWaterBody = null)
    {
        var waterKind = classifiedWaterBody ?? classification switch
        {
            LandCoverClass.PermanentWater => TerrainWaterBodyKind.PermanentWaterUnspecified,
            null => TerrainWaterBodyKind.NotWater,
            _ => TerrainWaterBodyKind.NotWater
        };
        if (!rawTerrainElevationMetres.HasValue)
            return new TerrainSurfaceResolution(null, null, classification, waterKind,
                TerrainSurfaceResolutionReason.TerrainUnavailable, false);

        var raw = rawTerrainElevationMetres.Value;
        if (waterKind == TerrainWaterBodyKind.InlandWater || waterKind is
            TerrainWaterBodyKind.PermanentWaterUnspecified && raw >= 0)
            return new TerrainSurfaceResolution(raw, raw, classification, waterKind,
                TerrainSurfaceResolutionReason.WaterElevationPreserved, false);
        if (waterKind == TerrainWaterBodyKind.Ocean && raw < 0)
            return new TerrainSurfaceResolution(raw, 0, classification, waterKind,
                TerrainSurfaceResolutionReason.OceanBathymetryAdjustedToMeanSeaLevel, true);
        if (waterKind == TerrainWaterBodyKind.PermanentWaterUnspecified && raw < 0)
            return new TerrainSurfaceResolution(raw, 0, classification, waterKind,
                TerrainSurfaceResolutionReason.PermanentWaterBathymetryAdjustedToMeanSeaLevel, true);
        return new TerrainSurfaceResolution(raw, raw, classification, waterKind,
            classification.HasValue ? TerrainSurfaceResolutionReason.RawTerrainLand :
                TerrainSurfaceResolutionReason.RawTerrainClassificationUnavailable, false);
    }

    private static EnvironmentalValue<double> CreateSurfaceValue(EnvironmentalValue<double> raw,
        TerrainSurfaceResolution resolution)
    {
        if (!resolution.SurfaceElevationMetres.HasValue)
            return new EnvironmentalValue<double>(raw.State, default, SourceId, SourceVersion,
                "Physical terrain surface is unavailable because Terrarium elevation is unavailable.");
        var state = resolution.IsWater ? EnvironmentalDataState.Water : EnvironmentalDataState.Available;
        return new EnvironmentalValue<double>(state, resolution.SurfaceElevationMetres.Value,
            SourceId, SourceVersion, ResolutionMessage(resolution),
            RetrievedAt: SystemClock.Instance.GetCurrentInstant());
    }

    public static string ResolutionMessage(TerrainSurfaceResolution resolution) => resolution.Reason switch
    {
        TerrainSurfaceResolutionReason.OceanBathymetryAdjustedToMeanSeaLevel =>
            "Ocean bathymetry resolved to approximate mean sea level.",
        TerrainSurfaceResolutionReason.PermanentWaterBathymetryAdjustedToMeanSeaLevel =>
            "Negative Terrarium permanent-water sample resolved to approximate mean sea level; ocean connectivity is not independently known.",
        TerrainSurfaceResolutionReason.WaterElevationPreserved =>
            "Permanent-water Terrarium elevation retained to preserve inland/elevated water.",
        TerrainSurfaceResolutionReason.RawTerrainClassificationUnavailable =>
            "WorldCover unavailable; raw Terrarium elevation retained.",
        TerrainSurfaceResolutionReason.RawTerrainLand => "Non-water Terrarium elevation retained.",
        _ => "Terrarium elevation unavailable."
    };

    private async Task<EnvironmentalValue<LandCoverClass>> SafeLandCoverAsync(GeoCoordinate coordinate,
        CancellationToken cancellationToken)
    {
        try { return await landCover.GetLandCoverAsync(coordinate, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "WorldCover classification failed during terrain surface resolution");
            return EnvironmentalValue<LandCoverClass>.Unavailable(WorldCoverLandCoverProvider.SourceId,
                WorldCoverLandCoverProvider.SourceVersion, "WorldCover classification failed.");
        }
    }

    private async Task<LandCoverBatchResult> SafeLandCoversAsync(IReadOnlyList<GeoCoordinate> coordinates,
        CancellationToken cancellationToken)
    {
        try { return await landCover.GetLandCoversAsync(coordinates, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "WorldCover classification batch failed during terrain surface resolution");
            return new LandCoverBatchResult(EnvironmentalDataState.Error,
                new LandCoverClass?[coordinates.Count], WorldCoverLandCoverProvider.SourceId,
                WorldCoverLandCoverProvider.SourceVersion, "WorldCover classification failed.");
        }
    }

    private void LogAdjustment(GeoCoordinate coordinate, TerrainSurfaceResolution resolution)
    {
        if (!resolution.WasAdjusted) return;
        logger.LogInformation(
            "Terrain surface adjusted at {Latitude:F6},{Longitude:F6}: raw={Raw:F3}m, resolved={Resolved:F3}m, classification={Classification}, reason={Reason}",
            coordinate.Latitude, coordinate.Longitude, resolution.RawTerrainElevationMetres,
            resolution.SurfaceElevationMetres, resolution.Classification, resolution.Reason);
    }
}

/// <summary>Test/compatibility adapter for terrain-only callers; production DI uses TerrainSurfaceResolver.</summary>
internal sealed class RawTerrainSurfaceResolver(ITerrainElevationProvider terrain) : ITerrainSurfaceResolver
{
    public Task PreloadAsync(IReadOnlyList<GeoCoordinate> coordinates, CancellationToken cancellationToken) =>
        terrain.PreloadAsync(coordinates, cancellationToken);

    public async Task<TerrainSurfaceSampleResult> GetSurfaceSampleAsync(GeoCoordinate coordinate,
        CancellationToken cancellationToken)
    {
        var raw = await terrain.GetElevationSampleAsync(coordinate, cancellationToken).ConfigureAwait(false);
        var unavailable = EnvironmentalValue<LandCoverClass>.Unavailable("classification", "none",
            "Classification not supplied by this terrain-only test adapter.");
        var resolution = TerrainSurfaceResolver.Resolve(raw.Value.HasValue ? raw.Value.Value : null, null);
        return new TerrainSurfaceSampleResult(raw.Value, raw.Value, unavailable, resolution, raw.Diagnostics);
    }

    public async Task<TerrainSurfaceBatchResult> GetSurfaceElevationsAsync(
        IReadOnlyList<GeoCoordinate> coordinates, CancellationToken cancellationToken)
    {
        var raw = await terrain.GetElevationsAsync(coordinates, cancellationToken).ConfigureAwait(false);
        return new TerrainSurfaceBatchResult(raw.State, raw.ElevationsMetres, raw.ElevationsMetres,
            new LandCoverClass?[coordinates.Count],
            Enumerable.Repeat(TerrainSurfaceResolutionReason.RawTerrainClassificationUnavailable,
                coordinates.Count).ToArray(), new bool[coordinates.Count],
            Enumerable.Range(0, coordinates.Count).Select(raw.StatusAt).ToArray(), raw.Message);
    }

    public Task<TerrainSurfaceClassificationBatch> GetClassificationsAsync(
        IReadOnlyList<GeoCoordinate> coordinates, CancellationToken cancellationToken) => Task.FromResult(
        new TerrainSurfaceClassificationBatch(EnvironmentalDataState.Unavailable,
            new LandCoverClass?[coordinates.Count], new TerrainWaterBodyKind[coordinates.Count],
            "Classification not supplied by this terrain-only test adapter."));

    public Task<TerrainSurfaceBatchResult> GetSurfaceElevationsAsync(
        IReadOnlyList<GeoCoordinate> coordinates, TerrainSurfaceClassificationBatch classifications,
        CancellationToken cancellationToken) => GetSurfaceElevationsAsync(coordinates, cancellationToken);
}
