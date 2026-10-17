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
public sealed class PlannerEnvironmentService : IPlannerEnvironmentService
{
    private readonly IHorizonService horizons;
    private readonly ISettlementDataProvider settlement;
    private readonly ILogger<PlannerEnvironmentService> logger;
    public PlannerEnvironmentService(IHorizonService horizons, ISettlementDataProvider settlement,
        ILogger<PlannerEnvironmentService> logger, IEnvironmentalTileCache? terrainCache = null,
        TimeProvider? time = null)
    {
        this.horizons = horizons;
        this.settlement = settlement;
        this.logger = logger;
        _completed = new(CompletedSnapshotCapacity, snapshot => HorizonService.EstimateProfileBytes(snapshot.HorizonProfile) +
            (snapshot.Settlement.Value is { } raster ?
                ((long)raster.BuildingFraction.Length + raster.BuildingHeightMetres.Length) * sizeof(float) : 0), time);
        terrainCache?.RegisterTerrainInvalidation(InvalidateCache);
    }

    public static readonly TerrainProfileRequest DefaultHorizonRequest = new();
    private const double SettlementSampleHalfSizeDegrees = 0.001;
    public const int CompletedSnapshotCapacity = 4;
    private readonly object _gate = new();
    private long _generation;
    private readonly Dictionary<EnvironmentCacheKey, BuildEntry> _snapshots = new();
    private readonly CompletedResultCache<EnvironmentCacheKey, PlannerEnvironmentSnapshot> _completed;
    public CompletedResultCacheDiagnostics CompletedCacheDiagnostics => _completed.Diagnostics;

    /// <summary>
    /// A snapshot built while a source was transiently missing is reused only as long as a degraded
    /// horizon is, so it is rebuilt once the source is back. Settlement that the source reports as
    /// absent for the area (<see cref="EnvironmentalDataState.TileAbsent"/>) is permanent, not degraded.
    /// </summary>
    private static TimeSpan? CompletedLifetime(PlannerEnvironmentSnapshot snapshot) =>
        HorizonService.IsDegraded(snapshot.HorizonProfile) ||
        snapshot.Settlement.State is EnvironmentalDataState.SourceUnavailable or EnvironmentalDataState.Error
            ? HorizonService.DegradedProfileLifetime
            : null;
    public int ActiveBuildCount { get { lock (_gate) return _snapshots.Count; } }

    private sealed class BuildEntry
    {
        public BuildEntry(Func<BuildEntry, Task<PlannerEnvironmentSnapshot>> build) =>
            Task = new(() => build(this), LazyThreadSafetyMode.ExecutionAndPublication);
        public Lazy<Task<PlannerEnvironmentSnapshot>> Task { get; }

        /// <summary>The build's own lifetime, never a caller's token (see <see cref="RemoveAbandonedBuilds"/>). It has
        /// no timer or wait handle, so it is not disposed: a superseded build may still be cancelling.</summary>
        public CancellationTokenSource Cancellation { get; } = new();

        /// <summary>Callers waiting for this build right now. Guarded by the service gate.</summary>
        public int Consumers;
    }

    public void InvalidateCache()
    {
        lock (_gate) { _generation++; _completed.Clear(); _snapshots.Clear(); }
    }

    /// <summary>
    /// Called under the gate when a build for a new observer or request starts. Builds nobody is waiting for
    /// any more (their callers moved on) are forgotten, so nobody can join them, and returned for the caller to
    /// cancel once it has left the gate: rapid observer moves leave one build running rather than one per move
    /// (PF-01, NX-20). A build is never cancelled while a caller waits for it, and an abandoned build stays
    /// joinable until something else supersedes it: an astronomy refresh that carries the observer's work on
    /// rejoins it for the same observer without a new build (NX-09).
    /// </summary>
    private List<BuildEntry>? RemoveAbandonedBuilds(EnvironmentCacheKey current)
    {
        List<KeyValuePair<EnvironmentCacheKey, BuildEntry>>? abandoned = null;
        foreach (var pair in _snapshots)
            if (pair.Value.Consumers == 0 && !pair.Key.Equals(current))
                (abandoned ??= []).Add(pair);
        if (abandoned is null) return null;
        foreach (var (key, _) in abandoned) _snapshots.Remove(key);
        return abandoned.ConvertAll(pair => pair.Value);
    }

    // Outside the gate: cancellation runs the builds' continuations inline.
    private static void Cancel(List<BuildEntry>? superseded)
    {
        if (superseded is null) return;
        foreach (var entry in superseded)
        {
            entry.Cancellation.Cancel();
            // Nobody awaits it; keep a failure other than the cancellation from going unobserved.
            if (entry.Task.IsValueCreated)
                _ = entry.Task.Value.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    public async Task<PlannerEnvironmentSnapshot> GetSnapshotAsync(GeoCoordinate observer,
        CancellationToken cancellationToken) =>
        await GetSnapshotAsync(observer, DefaultHorizonRequest, cancellationToken).ConfigureAwait(false);

    public async Task<PlannerEnvironmentSnapshot> GetSnapshotAsync(GeoCoordinate observer,
        TerrainProfileRequest terrainRequest, CancellationToken cancellationToken)
    {
        var normalised = observer.Normalised();
        var key = EnvironmentCacheKey.For(normalised, terrainRequest);
        BuildEntry entry;
        List<BuildEntry>? superseded = null;
        lock (_gate)
        {
            if (_completed.TryGetValue(key, out var cached)) return cached;
            if (!_snapshots.TryGetValue(key, out entry!))
            {
                superseded = RemoveAbandonedBuilds(key);
                var generation = _generation;
                entry = new BuildEntry(current => BuildAndCacheAsync(key, current, generation, normalised, terrainRequest));
                _snapshots.Add(key, entry);
            }
            entry.Consumers++;
        }
        Cancel(superseded);
        // No await between the count and the try: every counted caller reaches its finally.
        try { return await entry.Task.Value.WaitAsync(cancellationToken).ConfigureAwait(false); }
        finally { lock (_gate) entry.Consumers--; }
    }

    private async Task<PlannerEnvironmentSnapshot> BuildAndCacheAsync(EnvironmentCacheKey key,
        BuildEntry entry, long generation, GeoCoordinate observer, TerrainProfileRequest request)
    {
        try
        {
            // The build's own token: caller cancellation only detaches that caller (above).
            var result = await BuildAsync(observer, request, entry.Cancellation.Token).ConfigureAwait(false);
            lock (_gate) { if (_generation == generation) _completed.TryAdd(key, result, CompletedLifetime(result)); }
            return result;
        }
        finally
        {
            lock (_gate)
                if (_snapshots.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                    _snapshots.Remove(key);
        }
    }

    public Task<TerrainHorizonProfile> GetPriorityHorizonAsync(GeoCoordinate observer,
        IReadOnlyList<double> bearings, CancellationToken cancellationToken) =>
        GetPriorityHorizonAsync(observer, DefaultHorizonRequest, bearings, cancellationToken);

    public Task<TerrainHorizonProfile> GetPriorityHorizonAsync(GeoCoordinate observer,
        TerrainProfileRequest terrainRequest, IReadOnlyList<double> bearings,
        CancellationToken cancellationToken) =>
        !terrainRequest.EnableTerrainCalculations ? Task.FromResult(TerrainHorizonProfile.Disabled(observer,
            SystemClock.Instance.GetCurrentInstant(), terrainRequest.ObserverHeightAboveGroundMetres,
            terrainRequest.ManualGroundElevationOverrideMetres)) :
        horizons.GetPriorityProfileAsync(observer.Normalised(), terrainRequest, bearings,
            cancellationToken);

    private async Task<PlannerEnvironmentSnapshot> BuildAsync(GeoCoordinate observer,
        TerrainProfileRequest terrainRequest, CancellationToken cancellationToken)
    {
        var horizonTask = terrainRequest.EnableTerrainCalculations
            ? horizons.GetProfileAsync(observer, terrainRequest, cancellationToken)
            : Task.FromResult(TerrainHorizonProfile.Disabled(observer, SystemClock.Instance.GetCurrentInstant(),
                terrainRequest.ObserverHeightAboveGroundMetres, terrainRequest.ManualGroundElevationOverrideMetres));
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

    /// <summary>
    /// Snapshot identity. The observer's own elevation is deliberately absent: a resolved ground
    /// elevation is written back into the session observer after a build, and an otherwise identical
    /// request must still reuse this snapshot. Only the fallback datum, which is the one part of that
    /// elevation able to change a result, takes part.
    /// </summary>
    private readonly record struct EnvironmentCacheKey(
        double Latitude,
        double Longitude,
        double ObserverGroundFallbackMetres,
        TerrainProfileRequest TerrainRequest)
    {
        public static EnvironmentCacheKey For(GeoCoordinate observer, TerrainProfileRequest request) =>
            new(observer.Latitude, observer.Longitude,
                request.EffectiveObserverGroundFallbackMetres(observer.ElevationMetres), request);
    }
}
