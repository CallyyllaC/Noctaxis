using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Terrain;
using NodaTime;

namespace Noctaxis.Core.Environment;

/// <summary>
/// Builds the observer-scoped static environmental snapshot used by Planner. The snapshot is
/// independent of date, weather, map viewport and camera bearing, and is therefore safe to reuse.
/// </summary>
public sealed class PlannerEnvironmentService(
    IHorizonService horizons,
    ISettlementDataProvider settlement,
    ILogger<PlannerEnvironmentService> logger) : IPlannerEnvironmentService
{
    public static readonly TerrainProfileRequest DefaultHorizonRequest = new();
    private const double SettlementSampleHalfSizeDegrees = 0.001;
    private readonly ConcurrentDictionary<EnvironmentCacheKey, Lazy<Task<PlannerEnvironmentSnapshot>>> _snapshots =
        new();

    public async Task<PlannerEnvironmentSnapshot> GetSnapshotAsync(GeoCoordinate observer,
        CancellationToken cancellationToken) =>
        await GetSnapshotAsync(observer, DefaultHorizonRequest, cancellationToken).ConfigureAwait(false);

    public async Task<PlannerEnvironmentSnapshot> GetSnapshotAsync(GeoCoordinate observer,
        TerrainProfileRequest terrainRequest, CancellationToken cancellationToken)
    {
        var normalised = observer.Normalised();
        var key = new EnvironmentCacheKey(normalised, terrainRequest);
        var lazy = _snapshots.GetOrAdd(key, _ => new Lazy<Task<PlannerEnvironmentSnapshot>>(
            () => BuildAsync(normalised, terrainRequest, CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (lazy.IsValueCreated && lazy.Value.IsCompleted && !lazy.Value.IsCompletedSuccessfully)
                _snapshots.TryRemove(new KeyValuePair<EnvironmentCacheKey,
                    Lazy<Task<PlannerEnvironmentSnapshot>>>(key, lazy));
            throw;
        }
    }

    public Task<TerrainHorizonProfile> GetPriorityHorizonAsync(GeoCoordinate observer,
        IReadOnlyList<double> bearings, CancellationToken cancellationToken) =>
        GetPriorityHorizonAsync(observer, DefaultHorizonRequest, bearings, cancellationToken);

    public Task<TerrainHorizonProfile> GetPriorityHorizonAsync(GeoCoordinate observer,
        TerrainProfileRequest terrainRequest, IReadOnlyList<double> bearings,
        CancellationToken cancellationToken) =>
        horizons.GetPriorityProfileAsync(observer.Normalised(), terrainRequest, bearings,
            cancellationToken);

    private async Task<PlannerEnvironmentSnapshot> BuildAsync(GeoCoordinate observer,
        TerrainProfileRequest terrainRequest, CancellationToken cancellationToken)
    {
        var horizonTask = horizons.GetProfileAsync(observer, terrainRequest, cancellationToken);
        var settlementTask = TimedSettlementAsync(observer, cancellationToken);
        var horizon = await horizonTask.ConfigureAwait(false);

        var classification = horizon.ObserverDiagnostics?.Classification;
        var currentCover = classification.HasValue
            ? new EnvironmentalValue<LandCoverClass>(EnvironmentalDataState.Available, classification.Value,
                WorldCoverLandCoverProvider.SourceId, WorldCoverLandCoverProvider.SourceVersion,
                "WorldCover classification used by terrain surface resolution.",
                RetrievedAt: SystemClock.Instance.GetCurrentInstant())
            : EnvironmentalValue<LandCoverClass>.Unavailable(WorldCoverLandCoverProvider.SourceId,
                WorldCoverLandCoverProvider.SourceVersion,
                "WorldCover classification is unavailable at this coordinate.");
        var ground = horizon.GroundElevationAtObserver ?? EnvironmentalValue<double>.Unavailable(
            TerrariumTerrainProvider.SourceId, TerrariumTerrainProvider.SourceVersion,
            "Terrarium terrain elevation is unavailable at this coordinate.");
        return new PlannerEnvironmentSnapshot(observer, ground, currentCover,
            await settlementTask.ConfigureAwait(false), horizon, SystemClock.Instance.GetCurrentInstant());
    }

    private async Task<EnvironmentalValue<SettlementRaster>> SafeSettlementAsync(GeoCoordinate observer,
        CancellationToken cancellationToken)
    {
        try
        {
            return await settlement.GetSettlementAsync(CreateSettlementRequest(observer), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "WSF settlement association failed for Planner environment");
            return new EnvironmentalValue<SettlementRaster>(EnvironmentalDataState.Error, default,
                WsfSettlementDataProvider.SourceId, WsfSettlementDataProvider.SourceVersion,
                "WSF settlement data failed for this coordinate.");
        }
    }

    private async Task<EnvironmentalValue<SettlementRaster>> TimedSettlementAsync(GeoCoordinate observer,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        try { return await SafeSettlementAsync(observer, cancellationToken).ConfigureAwait(false); }
        finally
        {
            timer.Stop();
            logger.LogDebug("WSF observer-context lookup completed in {Elapsed:F1}ms",
                timer.Elapsed.TotalMilliseconds);
        }
    }

    private static GeoRasterRequest CreateSettlementRequest(GeoCoordinate observer)
    {
        var south = Math.Max(-90, observer.Latitude - SettlementSampleHalfSizeDegrees);
        var north = Math.Min(90, observer.Latitude + SettlementSampleHalfSizeDegrees);
        var west = observer.Longitude - SettlementSampleHalfSizeDegrees;
        var east = observer.Longitude + SettlementSampleHalfSizeDegrees;
        if (west < -180) west += 360;
        if (east > 180) east -= 360;
        return new GeoRasterRequest(new GeoBounds(south, west, north, east), 1, 1);
    }

    private readonly record struct EnvironmentCacheKey(
        GeoCoordinate Observer,
        TerrainProfileRequest TerrainRequest);
}
