using Noctaxis.Core.Domain;

namespace Noctaxis.Core.Environment;

public sealed record ElevationBatchResult(
    EnvironmentalDataState State,
    IReadOnlyList<double?> ElevationsMetres,
    string SourceId,
    string SourceVersion,
    string Message,
    IReadOnlyList<TerrainSampleStatus>? SampleStatuses = null)
{
    public TerrainSampleStatus StatusAt(int index) => SampleStatuses is { } statuses && index < statuses.Count
        ? statuses[index]
        : ElevationsMetres[index].HasValue ? TerrainSampleStatus.Valid : TerrainSampleStatus.Unavailable;
}

public sealed record TerrainGridSample(
    GeoCoordinate Coordinate,
    int Row,
    int Column,
    double? RawElevationMetres,
    double Weight,
    TerrainSampleStatus Status);

public sealed record ElevationSampleDiagnostics(
    string Provider,
    string Version,
    string Tile,
    string Cell,
    string Resolution,
    string VerticalDatum,
    IReadOnlyList<TerrainGridSample> RawSamples,
    double? InterpolatedElevationMetres,
    TerrainSampleStatus Status,
    string Message,
    bool IsInterpolated = false,
    double? NativeResolutionMetres = null,
    string? Quality = null);

public sealed record ElevationSampleResult(
    EnvironmentalValue<double> Value,
    ElevationSampleDiagnostics Diagnostics);

public sealed record LandCoverBatchResult(
    EnvironmentalDataState State,
    IReadOnlyList<LandCoverClass?> Classifications,
    string SourceId,
    string SourceVersion,
    string Message,
    IReadOnlyList<TerrainWaterBodyKind>? WaterBodyKinds = null)
{
    public TerrainWaterBodyKind WaterBodyKindAt(int index) =>
        WaterBodyKinds is { } kinds && index < kinds.Count
            ? kinds[index]
            : Classifications[index] == LandCoverClass.PermanentWater
                ? TerrainWaterBodyKind.PermanentWaterUnspecified
                : TerrainWaterBodyKind.NotWater;
}

public interface ITerrainElevationProvider
{
    Task<EnvironmentalValue<double>> GetElevationAsync(GeoCoordinate coordinate,
        CancellationToken cancellationToken);
    Task<ElevationBatchResult> GetElevationsAsync(IReadOnlyList<GeoCoordinate> coordinates,
        CancellationToken cancellationToken);
    async Task<ElevationSampleResult> GetElevationSampleAsync(GeoCoordinate coordinate,
        CancellationToken cancellationToken)
    {
        var value = await GetElevationAsync(coordinate, cancellationToken).ConfigureAwait(false);
        return ElevationDiagnostics.FromValue(value);
    }
    Task PreloadAsync(IReadOnlyList<GeoCoordinate> coordinates, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

internal static class ElevationDiagnostics
{
    public static ElevationSampleResult FromValue(EnvironmentalValue<double> value)
    {
        var status = value.State switch
        {
            EnvironmentalDataState.Water => TerrainSampleStatus.Water,
            EnvironmentalDataState.Error or EnvironmentalDataState.InvalidData or EnvironmentalDataState.InvalidRaster
                => TerrainSampleStatus.Error,
            _ when value.HasValue => TerrainSampleStatus.Valid,
            _ => TerrainSampleStatus.Unavailable
        };
        return new ElevationSampleResult(value, new ElevationSampleDiagnostics(
            value.SourceId, value.SourceVersion, "Provider-specific", "Provider-specific",
            "Provider-specific", "Provider-specific", [], value.HasValue ? value.Value : null,
            status, value.Message));
    }
}

public interface ILandCoverProvider
{
    Task<EnvironmentalValue<LandCoverClass>> GetLandCoverAsync(GeoCoordinate coordinate,
        CancellationToken cancellationToken);

    async Task<LandCoverBatchResult> GetLandCoversAsync(IReadOnlyList<GeoCoordinate> coordinates,
        CancellationToken cancellationToken)
    {
        var values = new LandCoverClass?[coordinates.Count];
        for (var index = 0; index < coordinates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = await GetLandCoverAsync(coordinates[index], cancellationToken).ConfigureAwait(false);
            if (value.HasValue) values[index] = value.Value;
        }
        var available = values.Count(value => value.HasValue);
        return new LandCoverBatchResult(
            available == 0 ? EnvironmentalDataState.Unavailable :
            available == values.Length ? EnvironmentalDataState.Available : EnvironmentalDataState.Partial,
            values,
            "land-cover",
            "provider",
            available == 0 ? "Land-cover classification is unavailable." : "Land-cover classification batch.");
    }
}

public interface ISettlementDataProvider
{
    Task<EnvironmentalValue<SettlementRaster>> GetSettlementAsync(GeoRasterRequest request,
        CancellationToken cancellationToken);
}

public sealed class UnavailableSettlementDataProvider : ISettlementDataProvider
{
    public Task<EnvironmentalValue<SettlementRaster>> GetSettlementAsync(GeoRasterRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(EnvironmentalValue<SettlementRaster>.Unavailable(
            "wsf-3d", "unconfigured", "WSF settlement data is not configured."));
    }
}

public interface ILightPollutionProvider
{
    Task<EnvironmentalValue<LightPollutionSample>> GetLightPollutionAsync(GeoCoordinate coordinate,
        CancellationToken cancellationToken);
}

public interface IAuroraProvider
{
    Task<EnvironmentalValue<AuroraEnvironment>> GetAuroraAsync(GeoCoordinate coordinate,
        CancellationToken cancellationToken);
}
