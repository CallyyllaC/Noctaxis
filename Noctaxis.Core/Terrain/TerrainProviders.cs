using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Environment;
using NodaTime;

namespace Noctaxis.Core.Terrain;

public sealed record TerrainRadialSamplingPolicy(
    double MinimumDistanceMetres = 15,
    double NearFieldEndMetres = 1_000,
    double NearFieldStepMetres = 15,
    double LocalEndMetres = 5_000,
    double LocalStepMetres = 40,
    double RegionalEndMetres = 20_000,
    double RegionalStepMetres = 100,
    double FarFieldEndMetres = 50_000,
    double FarFieldStepMetres = 250,
    double DistantEndMetres = 100_000,
    double DistantStepMetres = 500,
    double LongRangeEndMetres = 250_000,
    double LongRangeStepMetres = 1_000,
    double ExtremeRangeStepMetres = 2_000)
{
    public static TerrainRadialSamplingPolicy Default { get; } = new();
}

public sealed record TerrainProfileRequest(
    int AzimuthSampleCount = 360,
    double MaximumDistanceMetres = 50_000,
    double? DistanceStepMetres = null,
    bool AccountForEarthCurvature = true,
    double ObserverHeightAboveGroundMetres = 1.7,
    TerrainRadialSamplingPolicy? AdaptiveSampling = null,
    double? ManualGroundElevationOverrideMetres = null)
{
    public TerrainRadialSamplingPolicy EffectiveAdaptiveSampling =>
        AdaptiveSampling ?? TerrainRadialSamplingPolicy.Default;
}

/// <summary>Development timing data. Values are wall-clock stage times, not invented percentages.</summary>
public sealed record TerrainPipelineTimings(
    double GeographicCoordinateGenerationMilliseconds,
    double RequiredTileDiscoveryMilliseconds,
    double CacheLookupMilliseconds,
    double DiskReadAndDecodeMilliseconds,
    double NetworkAcquisitionMilliseconds,
    double TilePreparationMilliseconds,
    double SurfaceClassificationMilliseconds,
    double TerrainSamplingMilliseconds,
    double HorizonMathematicsMilliseconds,
    double ModelConstructionMilliseconds,
    double TotalMilliseconds,
    int DegreeOfParallelism,
    int RadialSampleCount,
    int CompletedBearingCount,
    bool IsPartial);

/// <summary>
/// A progressive profile build. Priority requests fill the fixed bearing slots used by the camera
/// before the complete 360-degree result, without creating a second terrain algorithm.
/// </summary>
public sealed class TerrainHorizonWork
{
    private readonly Func<IReadOnlyList<double>, CancellationToken, Task<TerrainHorizonProfile>> _prioritise;

    internal TerrainHorizonWork(Task<TerrainHorizonProfile> completeProfile,
        Func<IReadOnlyList<double>, CancellationToken, Task<TerrainHorizonProfile>> prioritise,
        int degreeOfParallelism)
    {
        CompleteProfile = completeProfile;
        _prioritise = prioritise;
        DegreeOfParallelism = degreeOfParallelism;
    }

    public Task<TerrainHorizonProfile> CompleteProfile { get; }
    public int DegreeOfParallelism { get; }

    public Task<TerrainHorizonProfile> PrioritiseBearingsAsync(IReadOnlyList<double> bearings,
        CancellationToken cancellationToken) => _prioritise(bearings, cancellationToken);
}

/// <summary>Calculates the horizon from the application's one canonical terrain source.</summary>
public interface IHorizonService
{
    TerrainHorizonWork StartProfile(GeoCoordinate observer, TerrainProfileRequest request,
        CancellationToken cancellationToken) => new(GetProfileAsync(observer, request, cancellationToken),
        (_, token) => GetProfileAsync(observer, request, token), 1);
    Task<TerrainHorizonProfile> GetProfileAsync(GeoCoordinate observer, TerrainProfileRequest request,
        CancellationToken cancellationToken);
    Task<TerrainHorizonProfile> GetPriorityProfileAsync(GeoCoordinate observer, TerrainProfileRequest request,
        IReadOnlyList<double> bearings, CancellationToken cancellationToken) =>
        GetProfileAsync(observer, request, cancellationToken);
}

public sealed class HorizonService : IHorizonService
{
    public const double MeanEarthRadiusMetres = 6_371_008.8;
    public const double StandardRefractionEffectiveRadiusMultiplier = 7d / 6d;
    public const double EffectiveEarthRadiusMetres = MeanEarthRadiusMetres * StandardRefractionEffectiveRadiusMultiplier;
    public const int MaximumTerrainWorkers = 6;
    public static int DefaultDegreeOfParallelism => Math.Max(1,
        Math.Min(MaximumTerrainWorkers, System.Environment.ProcessorCount - 1));

    private readonly ITerrainSurfaceResolver _surface;
    private readonly ILogger<HorizonService> _logger;
    private readonly ConcurrentDictionary<string, ProgressiveSession> _active = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TerrainHorizonProfile> _completed = new(StringComparer.Ordinal);
    private readonly int _degreeOfParallelism;

    [ActivatorUtilitiesConstructor]
    public HorizonService(ITerrainSurfaceResolver surface, ILogger<HorizonService> logger,
        int? degreeOfParallelism = null)
    {
        _surface = surface;
        _logger = logger;
        _degreeOfParallelism = Math.Clamp(degreeOfParallelism ?? DefaultDegreeOfParallelism,
            1, MaximumTerrainWorkers);
    }

    public HorizonService(ITerrainElevationProvider terrain, ILogger<HorizonService> logger,
        int? degreeOfParallelism = null)
        : this(new RawTerrainSurfaceResolver(terrain), logger, degreeOfParallelism) { }

    public TerrainHorizonWork StartProfile(GeoCoordinate observer, TerrainProfileRequest request,
        CancellationToken cancellationToken)
    {
        Validate(request);
        var normalised = observer.Normalised();
        var key = CacheKey(normalised, request);
        if (_completed.TryGetValue(key, out var complete))
            return new TerrainHorizonWork(Task.FromResult(complete), (_, token) =>
                Task.FromResult(complete), _degreeOfParallelism);

        var session = _active.GetOrAdd(key, _ => new ProgressiveSession(normalised, request, _surface,
            _logger, cancellationToken, _degreeOfParallelism));
        _ = CompleteAndCacheAsync(key, session);
        return session.Work;
    }

    public Task<TerrainHorizonProfile> GetProfileAsync(GeoCoordinate observer, TerrainProfileRequest request,
        CancellationToken cancellationToken) => StartProfile(observer, request, cancellationToken)
        .CompleteProfile.WaitAsync(cancellationToken);

    public Task<TerrainHorizonProfile> GetPriorityProfileAsync(GeoCoordinate observer,
        TerrainProfileRequest request, IReadOnlyList<double> bearings, CancellationToken cancellationToken) =>
        StartProfile(observer, request, cancellationToken).PrioritiseBearingsAsync(bearings, cancellationToken);

    private async Task CompleteAndCacheAsync(string key, ProgressiveSession session)
    {
        try
        {
            var result = await session.Work.CompleteProfile.ConfigureAwait(false);
            _completed.TryAdd(key, result);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogDebug(ex, "Progressive terrain profile failed"); }
        finally { _active.TryRemove(new KeyValuePair<string, ProgressiveSession>(key, session)); }
    }

    private static string CacheKey(GeoCoordinate observer, TerrainProfileRequest request) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{observer.Latitude:R}:{observer.Longitude:R}:{observer.ElevationMetres:R}:{request}");

    public static double CurvatureDrop(double distanceMetres, bool accountForEarthCurvature = true) =>
        accountForEarthCurvature ? distanceMetres * distanceMetres / (2 * EffectiveEarthRadiusMetres) : 0;

    public static double ElevationSlope(double targetElevationMetres, double observerElevationMetres,
        double curvatureDropMetres, double inverseDistanceMetres) =>
        (targetElevationMetres - observerElevationMetres - curvatureDropMetres) * inverseDistanceMetres;

    public static double SlopeToElevationDegrees(double slope) =>
        Math.Atan(slope) * Angles.RadiansToDegrees;

    private sealed class ProgressiveSession
    {
        private const int BearingChunkSize = 24;
        private readonly GeoCoordinate _observer;
        private readonly TerrainProfileRequest _request;
        private readonly ITerrainSurfaceResolver _surface;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _cancellation;
        private readonly int _degreeOfParallelism;
        private readonly int[] _states;
        private readonly TaskCompletionSource<bool>[] _bearingReady;
        private readonly TerrainHorizonSample[] _samples;
        private readonly SemaphoreSlim _computeSlots;
        private readonly TaskCompletionSource<bool> _prepared = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TimingAccumulator _timings = new();
        private GeoCoordinate[] _coordinates = [];
        private TerrainSurfaceClassificationBatch _classifications = new(
            EnvironmentalDataState.Unavailable, [], [], "Not prepared.");
        private RadialPlan _radial = RadialPlan.Empty;
        private EnvironmentalValue<double> _observerTerrain = EnvironmentalValue<double>.Unavailable(
            TerrainSurfaceResolver.SourceId, TerrainSurfaceResolver.SourceVersion, "Not prepared.");
        private TerrainSurfaceResolution _observerSurfaceResolution;
        private ElevationSampleDiagnostics _observerTerrainDiagnostics = ElevationDiagnostics.FromValue(
            EnvironmentalValue<double>.Unavailable(TerrariumTerrainProvider.SourceId,
                TerrariumTerrainProvider.SourceVersion, "Not prepared.")).Diagnostics;
        private double _observerGroundMetres;
        private double _observerCameraMetres;
        private TerrainSampleStatus _observerResolvedStatus = TerrainSampleStatus.Fallback;
        private string _observerResolutionPolicy = string.Empty;
        private ObserverDatumConfidence _datumConfidence;
        private string _datumMessage = string.Empty;
        private int _terrainCoverage;
        private int _completedBearings;
        private int _groundState;

        public ProgressiveSession(GeoCoordinate observer, TerrainProfileRequest request,
            ITerrainSurfaceResolver surface, ILogger logger,
            CancellationToken cancellationToken, int degreeOfParallelism)
        {
            _observer = observer;
            _request = request;
            _surface = surface;
            _logger = logger;
            _degreeOfParallelism = degreeOfParallelism;
            _states = new int[request.AzimuthSampleCount];
            _bearingReady = new TaskCompletionSource<bool>[request.AzimuthSampleCount];
            _samples = new TerrainHorizonSample[request.AzimuthSampleCount];
            for (var index = 0; index < request.AzimuthSampleCount; index++)
            {
                _bearingReady[index] = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _samples[index] = new TerrainHorizonSample(index * 360d / request.AzimuthSampleCount,
                    null, null);
            }
            _computeSlots = new SemaphoreSlim(degreeOfParallelism, degreeOfParallelism);
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var completion = Task.Run(BuildCompleteAsync, CancellationToken.None);
            Work = new TerrainHorizonWork(completion, PrioritiseAsync, degreeOfParallelism);
        }

        public TerrainHorizonWork Work { get; }

        private async Task<TerrainHorizonProfile> BuildCompleteAsync()
        {
            var total = Stopwatch.StartNew();
            using var diagnostics = EnvironmentalPerformanceDiagnostics.Use(_timings.AddEnvironmentalStage);
            try
            {
                await PrepareAsync(_cancellation.Token).ConfigureAwait(false);
                _prepared.TrySetResult(true);
                var nextChunk = -BearingChunkSize;
                var backgroundWorkers = Math.Max(1, _degreeOfParallelism - 1);
                var workers = new Task[backgroundWorkers];
                for (var worker = 0; worker < workers.Length; worker++)
                    workers[worker] = RunBackgroundWorkerAsync(() =>
                        Interlocked.Add(ref nextChunk, BearingChunkSize), _cancellation.Token);
                await Task.WhenAll(workers).ConfigureAwait(false);
                total.Stop();
                _timings.TotalMilliseconds = total.Elapsed.TotalMilliseconds;
                var profile = CreateProfile(isComplete: true);
                _logger.LogInformation(
                    "Horizon generated at {Latitude:F5},{Longitude:F5}: azimuths={Azimuths}, radialSamples={RadialSamples}, workers={Workers}, total={Total:F1}ms, coordinates={Coordinates:F1}ms, tilePreparation={Tiles:F1}ms, terrainSampling={Terrain:F1}ms, horizonMath={Math:F1}ms",
                    _observer.Latitude, _observer.Longitude, _samples.Length, _radial.Distances.Length,
                    _degreeOfParallelism, total.Elapsed.TotalMilliseconds,
                    _timings.CoordinateGenerationMilliseconds, _timings.TilePreparationMilliseconds,
                    _timings.TerrainSamplingMilliseconds,
                    _timings.HorizonMathematicsMilliseconds);
                return profile;
            }
            catch (Exception ex)
            {
                _prepared.TrySetException(ex);
                for (var index = 0; index < _bearingReady.Length; index++)
                    _bearingReady[index].TrySetException(ex);
                throw;
            }
            finally { _cancellation.Dispose(); }
        }

        private async Task PrepareAsync(CancellationToken cancellationToken)
        {
            _radial = CreateRadialPlan(_request);
            var coordinateTimer = Stopwatch.StartNew();
            _coordinates = new GeoCoordinate[_request.AzimuthSampleCount * _radial.Distances.Length];
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = _degreeOfParallelism,
                CancellationToken = cancellationToken
            };
            var originLatitude = _observer.Latitude * Angles.DegreesToRadians;
            var originLongitude = _observer.Longitude * Angles.DegreesToRadians;
            var (sinOriginLatitude, cosOriginLatitude) = Math.SinCos(originLatitude);
            Parallel.For(0, _request.AzimuthSampleCount, options, bearingIndex =>
            {
                var offset = bearingIndex * _radial.Distances.Length;
                for (var radialIndex = 0; radialIndex < _radial.Distances.Length; radialIndex++)
                    _coordinates[offset + radialIndex] = Destination(_observer.ElevationMetres,
                        originLongitude, sinOriginLatitude, cosOriginLatitude, bearingIndex, radialIndex, _radial);
            });
            coordinateTimer.Stop();
            _timings.CoordinateGenerationMilliseconds = coordinateTimer.Elapsed.TotalMilliseconds;

            var observerTerrainTask = SafeSurfaceSampleAsync(
                () => _surface.GetSurfaceSampleAsync(_observer, cancellationToken));
            var classificationsTask = GetTimedClassificationsAsync(cancellationToken);
            var tileTimer = Stopwatch.StartNew();
            await Task.WhenAll(_surface.PreloadAsync(_coordinates, cancellationToken), observerTerrainTask,
                    classificationsTask)
                .ConfigureAwait(false);
            tileTimer.Stop();
            _timings.TilePreparationMilliseconds = tileTimer.Elapsed.TotalMilliseconds;
            _classifications = await classificationsTask.ConfigureAwait(false);
            var observerTerrain = await observerTerrainTask.ConfigureAwait(false);
            _observerTerrain = observerTerrain.SurfaceElevation;
            _observerTerrainDiagnostics = observerTerrain.RawTerrainDiagnostics;
            _observerSurfaceResolution = observerTerrain.Resolution;
            ChooseObserverDatum();
        }

        private async Task<TerrainSurfaceClassificationBatch> GetTimedClassificationsAsync(
            CancellationToken cancellationToken)
        {
            var timer = Stopwatch.StartNew();
            try
            {
                return await _surface.GetClassificationsAsync(_coordinates, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                timer.Stop();
                _timings.SurfaceClassificationMilliseconds = timer.Elapsed.TotalMilliseconds;
            }
        }

        private async Task RunBackgroundWorkerAsync(Func<int> nextChunk, CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var start = nextChunk();
                if (start >= _samples.Length) return;
                var claimed = new List<int>(BearingChunkSize);
                for (var index = start; index < Math.Min(start + BearingChunkSize, _samples.Length); index++)
                    if (Interlocked.CompareExchange(ref _states[index], 1, 0) == 0) claimed.Add(index);
                if (claimed.Count > 0) await ProcessClaimedAsync(claimed, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<TerrainHorizonProfile> PrioritiseAsync(IReadOnlyList<double> bearings,
            CancellationToken cancellationToken)
        {
            if (bearings.Count == 0) throw new ArgumentException("At least one priority bearing is required.", nameof(bearings));
            var required = RequiredBearingIndices(bearings);
            var claimed = new List<int>(required.Length);
            foreach (var index in required)
                if (Interlocked.CompareExchange(ref _states[index], 1, 0) == 0) claimed.Add(index);
            await _prepared.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (claimed.Count > 0) await ProcessClaimedAsync(claimed, cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(required.Select(index => _bearingReady[index].Task)).WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return CreateProfile(isComplete: Volatile.Read(ref _completedBearings) == _samples.Length);
        }

        private int[] RequiredBearingIndices(IReadOnlyList<double> bearings)
        {
            var required = new HashSet<int>();
            var spacing = 360d / _samples.Length;
            foreach (var bearing in bearings)
            {
                var position = Angles.NormaliseDegrees(bearing) / spacing;
                var lower = (int)Math.Floor(position) % _samples.Length;
                required.Add(lower);
                required.Add((lower + 1) % _samples.Length);
            }
            return required.Order().ToArray();
        }

        private async Task ProcessClaimedAsync(IReadOnlyList<int> bearingIndices,
            CancellationToken cancellationToken)
        {
            using var diagnostics = EnvironmentalPerformanceDiagnostics.Use(_timings.AddEnvironmentalStage);
            await _computeSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var radialCount = _radial.Distances.Length;
                var positions = new GeoCoordinate[bearingIndices.Count * radialCount];
                var classifications = new LandCoverClass?[positions.Length];
                var waterKinds = new TerrainWaterBodyKind[positions.Length];
                for (var bearingOffset = 0; bearingOffset < bearingIndices.Count; bearingOffset++)
                {
                    Array.Copy(_coordinates, bearingIndices[bearingOffset] * radialCount,
                        positions, bearingOffset * radialCount, radialCount);
                    for (var radialIndex = 0; radialIndex < radialCount; radialIndex++)
                    {
                        var sourceIndex = bearingIndices[bearingOffset] * radialCount + radialIndex;
                        var targetIndex = bearingOffset * radialCount + radialIndex;
                        classifications[targetIndex] = _classifications.Classifications[sourceIndex];
                        waterKinds[targetIndex] = _classifications.WaterBodyKinds[sourceIndex];
                    }
                }

                var classificationSlice = new TerrainSurfaceClassificationBatch(_classifications.State,
                    classifications, waterKinds, _classifications.Message);

                var terrainBatch = await TimedBatchAsync(
                    () => _surface.GetSurfaceElevationsAsync(positions, classificationSlice,
                        cancellationToken), positions.Length,
                    "Terrain physical-surface resolution failed.").ConfigureAwait(false);
                MergeState(ref _groundState, terrainBatch.State);

                var mathTimer = Stopwatch.StartNew();
                for (var localBearing = 0; localBearing < bearingIndices.Count; localBearing++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var bearingIndex = bearingIndices[localBearing];
                    _samples[bearingIndex] = BuildBearing(bearingIndex, localBearing * radialCount, terrainBatch);
                    Volatile.Write(ref _states[bearingIndex], 2);
                    Interlocked.Increment(ref _completedBearings);
                    _bearingReady[bearingIndex].TrySetResult(true);
                }
                mathTimer.Stop();
                _timings.AddHorizonMath(mathTimer.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                foreach (var index in bearingIndices) _bearingReady[index].TrySetException(ex);
                throw;
            }
            finally { _computeSlots.Release(); }
        }

        private TerrainHorizonSample BuildBearing(int bearingIndex, int batchOffset,
            TerrainSurfaceBatchResult terrainBatch)
        {
            double? groundMaximumSlope = null;
            double? groundFeatureDistance = null;
            LandCoverClass? winningClassification = null;
            var sightline = new TerrainSightlineSample[_radial.Distances.Length];
            for (var radialIndex = 0; radialIndex < _radial.Distances.Length; radialIndex++)
            {
                var sampleIndex = batchOffset + radialIndex;
                var groundElevation = terrainBatch.SurfaceElevationsMetres[sampleIndex];
                var groundStatus = terrainBatch.StatusAt(batchOffset + radialIndex);
                double? groundSlope = null;
                if (groundElevation.HasValue)
                {
                    Interlocked.Exchange(ref _terrainCoverage, 1);
                    groundSlope = ElevationSlope(groundElevation.Value, _observerCameraMetres,
                        _radial.CurvatureDrops[radialIndex], _radial.InverseDistances[radialIndex]);
                    if (!groundMaximumSlope.HasValue || groundSlope.Value > groundMaximumSlope.Value)
                    {
                        groundMaximumSlope = groundSlope;
                        groundFeatureDistance = _radial.Distances[radialIndex];
                        winningClassification = terrainBatch.Classifications[sampleIndex];
                    }
                }
                sightline[radialIndex] = TerrainSightlineSample.FromSlope(_radial.Distances[radialIndex],
                    groundElevation, _radial.CurvatureDrops[radialIndex], groundSlope, groundStatus,
                    terrainBatch.RawTerrainElevationsMetres[sampleIndex],
                    terrainBatch.Classifications[sampleIndex], terrainBatch.AdjustedSamples[sampleIndex],
                    terrainBatch.ResolutionReasons[sampleIndex]);
            }
            double? groundAngle = groundMaximumSlope is double groundSlopeValue
                ? SlopeToElevationDegrees(groundSlopeValue) : null;
            return new TerrainHorizonSample(bearingIndex * 360d / _samples.Length,
                groundAngle, groundFeatureDistance,
                LandCover: winningClassification,
                Sightline: sightline);
        }

        private TerrainHorizonProfile CreateProfile(bool isComplete)
        {
            var modelTimer = Stopwatch.StartNew();
            var groundCoverage = Volatile.Read(ref _terrainCoverage) != 0;
            var groundSource = _observerTerrain.SourceId;
            var status = isComplete
                ? groundCoverage ? $"{groundSource} terrain horizon" : "Terrain elevation unavailable"
                : "Current camera terrain ready; full horizon refining";
            var snapshot = _samples.ToArray();
            modelTimer.Stop();
            _timings.AddModelConstruction(modelTimer.Elapsed.TotalMilliseconds);
            var completed = Volatile.Read(ref _completedBearings);
            var hasResolvedObserver = _request.ManualGroundElevationOverrideMetres.HasValue ||
                                      _observerTerrain.HasValue;
            return new TerrainHorizonProfile(_observer, snapshot, groundCoverage, status,
                SystemClock.Instance.GetCurrentInstant(), _observerTerrain,
                _request.ObserverHeightAboveGroundMetres, _request.MaximumDistanceMetres,
                ToDataState(_groundState, groundCoverage),
                hasResolvedObserver ? _observerGroundMetres : null,
                hasResolvedObserver ? _observerCameraMetres : null,
                _datumConfidence,
                _datumMessage, isComplete, completed, _timings.Snapshot(_degreeOfParallelism,
                    _radial.Distances.Length, completed, !isComplete),
                new TerrainObserverDiagnostics(_observerTerrainDiagnostics,
                    hasResolvedObserver ? _observerGroundMetres : null,
                    _observerResolvedStatus, _observerResolutionPolicy,
                    _observerSurfaceResolution.Classification,
                    _observerSurfaceResolution.SurfaceElevationMetres,
                    _observerSurfaceResolution.WasAdjusted,
                    _observerSurfaceResolution.Reason));
        }

        private void ChooseObserverDatum()
        {
            var manualOverride = _request.ManualGroundElevationOverrideMetres;
            _observerGroundMetres = manualOverride ?? (_observerTerrain.HasValue
                ? _observerTerrain.Value : _observer.ElevationMetres);
            _observerResolvedStatus = manualOverride.HasValue ? TerrainSampleStatus.Fallback :
                _observerTerrain.HasValue ? _observerTerrainDiagnostics.Status : TerrainSampleStatus.Fallback;
            _observerResolutionPolicy = manualOverride.HasValue
                ? "Manual ground override; camera height added once."
                : _observerTerrain.HasValue
                    ? $"{TerrainSurfaceResolver.ResolutionMessage(_observerSurfaceResolution)} Camera height added once."
                    : "Terrarium unavailable; supplied observer elevation used; camera height added once.";
            _datumConfidence = manualOverride.HasValue || _observerTerrain.HasValue
                ? ObserverDatumConfidence.Normal : ObserverDatumConfidence.Unavailable;
            _datumMessage = manualOverride.HasValue
                ? "Manual ground-elevation override selected; environmental ground data remains available for reset."
                : _observerTerrain.HasValue
                    ? "Resolved Terrarium/WorldCover physical observer surface selected."
                    : "Terrarium observer elevation is unavailable; the supplied observer elevation was used.";
            _observerCameraMetres = _observerGroundMetres + _request.ObserverHeightAboveGroundMetres;
        }

        private async Task<TerrainSurfaceSampleResult> SafeSurfaceSampleAsync(
            Func<Task<TerrainSurfaceSampleResult>> operation)
        {
            try { return await operation().ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                const string message = "Terrain physical-surface resolution failed.";
                _logger.LogWarning(ex, "{EnvironmentalFailure}", message);
                var raw = ElevationDiagnostics.FromValue(new EnvironmentalValue<double>(
                    EnvironmentalDataState.Error, default, TerrariumTerrainProvider.SourceId,
                    TerrariumTerrainProvider.SourceVersion, message));
                var unavailable = EnvironmentalValue<LandCoverClass>.Unavailable(
                    WorldCoverLandCoverProvider.SourceId, WorldCoverLandCoverProvider.SourceVersion, message);
                return new TerrainSurfaceSampleResult(raw.Value,
                    EnvironmentalValue<double>.Unavailable(TerrainSurfaceResolver.SourceId,
                        TerrainSurfaceResolver.SourceVersion, message), unavailable,
                    TerrainSurfaceResolver.Resolve(null, null), raw.Diagnostics);
            }
        }

        private async Task<TerrainSurfaceBatchResult> TimedBatchAsync(
            Func<Task<TerrainSurfaceBatchResult>> operation, int count, string message)
        {
            var timer = Stopwatch.StartNew();
            try
            {
                var result = await operation().ConfigureAwait(false);
                if (result.SurfaceElevationsMetres.Count != count)
                    throw new InvalidDataException("Terrain surface batch length did not match its request.");
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "{EnvironmentalFailure}", message);
                return new TerrainSurfaceBatchResult(EnvironmentalDataState.Error,
                    new double?[count], new double?[count], new LandCoverClass?[count],
                    Enumerable.Repeat(TerrainSurfaceResolutionReason.TerrainUnavailable, count).ToArray(),
                    new bool[count], Enumerable.Repeat(TerrainSampleStatus.Error, count).ToArray(), message);
            }
            finally
            {
                timer.Stop();
                _timings.AddTerrainSampling(timer.Elapsed.TotalMilliseconds);
            }
        }
    }

    private sealed class TimingAccumulator
    {
        private long _terrainTicks;
        private long _horizonTicks;
        private long _modelTicks;
        private long _tileDiscoveryTicks;
        private long _cacheLookupTicks;
        private long _diskReadDecodeTicks;
        private long _networkTicks;
        public double CoordinateGenerationMilliseconds;
        public double TilePreparationMilliseconds;
        public double SurfaceClassificationMilliseconds;
        public double TotalMilliseconds;
        public double TerrainSamplingMilliseconds => TicksToMilliseconds(Volatile.Read(ref _terrainTicks));
        public double HorizonMathematicsMilliseconds => TicksToMilliseconds(Volatile.Read(ref _horizonTicks));
        public void AddTerrainSampling(double milliseconds) => Interlocked.Add(ref _terrainTicks, MillisecondsToTicks(milliseconds));
        public void AddHorizonMath(double milliseconds) => Interlocked.Add(ref _horizonTicks, MillisecondsToTicks(milliseconds));
        public void AddModelConstruction(double milliseconds) => Interlocked.Add(ref _modelTicks, MillisecondsToTicks(milliseconds));
        public void AddEnvironmentalStage(string stage, double milliseconds)
        {
            var ticks = MillisecondsToTicks(milliseconds);
            if (stage == "tile-discovery") Interlocked.Add(ref _tileDiscoveryTicks, ticks);
            else if (stage == "cache-lookup") Interlocked.Add(ref _cacheLookupTicks, ticks);
            else if (stage is "disk-read-decode" or "dem-decode")
                Interlocked.Add(ref _diskReadDecodeTicks, ticks);
            else if (stage == "network-acquisition") Interlocked.Add(ref _networkTicks, ticks);
        }
        public TerrainPipelineTimings Snapshot(int workers, int radial, int completed, bool partial) => new(
            CoordinateGenerationMilliseconds, TicksToMilliseconds(Volatile.Read(ref _tileDiscoveryTicks)),
            TicksToMilliseconds(Volatile.Read(ref _cacheLookupTicks)),
            TicksToMilliseconds(Volatile.Read(ref _diskReadDecodeTicks)),
            TicksToMilliseconds(Volatile.Read(ref _networkTicks)),
            TilePreparationMilliseconds, SurfaceClassificationMilliseconds, TerrainSamplingMilliseconds,
            HorizonMathematicsMilliseconds,
            TicksToMilliseconds(Volatile.Read(ref _modelTicks)), TotalMilliseconds,
            workers, radial, completed, partial);
        private static long MillisecondsToTicks(double value) => (long)(value * Stopwatch.Frequency / 1_000d);
        private static double TicksToMilliseconds(long value) => value * 1_000d / Stopwatch.Frequency;
    }

    private sealed record RadialPlan(double[] Distances, double[] InverseDistances,
        double[] CurvatureDrops, double[] SinAngularDistances, double[] CosAngularDistances,
        double[] SinBearings, double[] CosBearings)
    {
        public static RadialPlan Empty { get; } = new([], [], [], [], [], [], []);
    }

    private static RadialPlan CreateRadialPlan(TerrainProfileRequest request)
    {
        var distances = CreateDistances(request).ToArray();
        var inverse = new double[distances.Length];
        var curvature = new double[distances.Length];
        var sinAngular = new double[distances.Length];
        var cosAngular = new double[distances.Length];
        for (var index = 0; index < distances.Length; index++)
        {
            inverse[index] = 1d / distances[index];
            curvature[index] = CurvatureDrop(distances[index], request.AccountForEarthCurvature);
            var angular = distances[index] / MeanEarthRadiusMetres;
            (sinAngular[index], cosAngular[index]) = Math.SinCos(angular);
        }
        var sinBearings = new double[request.AzimuthSampleCount];
        var cosBearings = new double[request.AzimuthSampleCount];
        for (var index = 0; index < request.AzimuthSampleCount; index++)
            (sinBearings[index], cosBearings[index]) = Math.SinCos(
                index * 360d / request.AzimuthSampleCount * Angles.DegreesToRadians);
        return new RadialPlan(distances, inverse, curvature, sinAngular, cosAngular,
            sinBearings, cosBearings);
    }

    private static GeoCoordinate Destination(double originElevationMetres, double originLongitudeRadians,
        double sinOriginLatitude, double cosOriginLatitude, int bearingIndex, int radialIndex, RadialPlan plan)
    {
        var sinAngular = plan.SinAngularDistances[radialIndex];
        var cosAngular = plan.CosAngularDistances[radialIndex];
        var sinLatitude2 = sinOriginLatitude * cosAngular + cosOriginLatitude * sinAngular *
                           plan.CosBearings[bearingIndex];
        var latitude2 = Math.Asin(sinLatitude2);
        var longitude2 = originLongitudeRadians + Math.Atan2(
            plan.SinBearings[bearingIndex] * sinAngular * cosOriginLatitude,
            cosAngular - sinOriginLatitude * sinLatitude2);
        return new GeoCoordinate(latitude2 * Angles.RadiansToDegrees,
            Angles.NormaliseLongitude(longitude2 * Angles.RadiansToDegrees), originElevationMetres);
    }

    private static void MergeState(ref int destination, EnvironmentalDataState state)
        => Interlocked.Or(ref destination, 1 << (int)state);

    private static EnvironmentalDataState ToDataState(int flags, bool coverage)
    {
        if (coverage)
        {
            var degraded = flags & ((1 << (int)EnvironmentalDataState.Partial) |
                                    (1 << (int)EnvironmentalDataState.Unavailable) |
                                    (1 << (int)EnvironmentalDataState.InvalidData) |
                                    (1 << (int)EnvironmentalDataState.Error));
            return degraded == 0 ? EnvironmentalDataState.Available : EnvironmentalDataState.Partial;
        }
        if ((flags & (1 << (int)EnvironmentalDataState.Error)) != 0) return EnvironmentalDataState.Error;
        if ((flags & (1 << (int)EnvironmentalDataState.InvalidData)) != 0) return EnvironmentalDataState.InvalidData;
        return EnvironmentalDataState.Unavailable;
    }

    internal static IReadOnlyList<double> CreateDistanceSequence(TerrainProfileRequest request) =>
        CreateDistances(request).ToArray();

    private static IEnumerable<double> CreateDistances(TerrainProfileRequest request)
    {
        if (request.DistanceStepMetres is double uniformStep)
        {
            for (var distance = uniformStep; distance <= request.MaximumDistanceMetres; distance += uniformStep)
                yield return distance;
            yield break;
        }
        var policy = request.EffectiveAdaptiveSampling;
        if (request.MaximumDistanceMetres <= policy.MinimumDistanceMetres)
        {
            yield return request.MaximumDistanceMetres;
            yield break;
        }
        var distances = new List<double>();
        AddSegment(policy.MinimumDistanceMetres, Math.Min(policy.NearFieldEndMetres, request.MaximumDistanceMetres), policy.NearFieldStepMetres);
        AddSegment(distances.Count == 0 ? policy.MinimumDistanceMetres : distances[^1], Math.Min(policy.LocalEndMetres, request.MaximumDistanceMetres), policy.LocalStepMetres);
        AddSegment(distances.Count == 0 ? policy.MinimumDistanceMetres : distances[^1], Math.Min(policy.RegionalEndMetres, request.MaximumDistanceMetres), policy.RegionalStepMetres);
        AddSegment(distances.Count == 0 ? policy.MinimumDistanceMetres : distances[^1], Math.Min(policy.FarFieldEndMetres, request.MaximumDistanceMetres), policy.FarFieldStepMetres);
        AddSegment(distances.Count == 0 ? policy.MinimumDistanceMetres : distances[^1], Math.Min(policy.DistantEndMetres, request.MaximumDistanceMetres), policy.DistantStepMetres);
        AddSegment(distances.Count == 0 ? policy.MinimumDistanceMetres : distances[^1], Math.Min(policy.LongRangeEndMetres, request.MaximumDistanceMetres), policy.LongRangeStepMetres);
        AddSegment(distances.Count == 0 ? policy.MinimumDistanceMetres : distances[^1], request.MaximumDistanceMetres, policy.ExtremeRangeStepMetres);
        foreach (var distance in distances) yield return distance;

        void AddSegment(double start, double end, double step)
        {
            if (end <= 0 || start > request.MaximumDistanceMetres || end < start) return;
            if (distances.Count == 0) distances.Add(Math.Min(start, end));
            var current = distances[^1];
            while (current + step < end - 1e-9) distances.Add(current += step);
            if (distances[^1] < end - 1e-9) distances.Add(end);
        }
    }

    private static void Validate(TerrainProfileRequest request)
    {
        if (request.AzimuthSampleCount is < 8 or > 1440)
            throw new ArgumentOutOfRangeException(nameof(request.AzimuthSampleCount));
        if (request.MaximumDistanceMetres <= 0 || request.DistanceStepMetres is double step &&
            (step <= 0 || step > request.MaximumDistanceMetres)) throw new ArgumentOutOfRangeException(nameof(request));
        if (!double.IsFinite(request.ObserverHeightAboveGroundMetres) ||
            request.ObserverHeightAboveGroundMetres is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(request.ObserverHeightAboveGroundMetres));
        if (request.ManualGroundElevationOverrideMetres is double manualGroundElevation &&
            (!double.IsFinite(manualGroundElevation) || manualGroundElevation is < -500 or > 9_000))
            throw new ArgumentOutOfRangeException(nameof(request.ManualGroundElevationOverrideMetres));
        var policy = request.EffectiveAdaptiveSampling;
        if (request.DistanceStepMetres is null &&
            (!double.IsFinite(policy.MinimumDistanceMetres) || policy.MinimumDistanceMetres <= 0 ||
             policy.NearFieldStepMetres <= 0 || policy.LocalStepMetres <= 0 ||
             policy.RegionalStepMetres <= 0 || policy.FarFieldStepMetres <= 0 ||
             policy.DistantStepMetres <= 0 || policy.LongRangeStepMetres <= 0 ||
             policy.ExtremeRangeStepMetres <= 0 || policy.NearFieldEndMetres < policy.MinimumDistanceMetres ||
             policy.LocalEndMetres < policy.NearFieldEndMetres || policy.RegionalEndMetres < policy.LocalEndMetres ||
             policy.FarFieldEndMetres < policy.RegionalEndMetres || policy.DistantEndMetres < policy.FarFieldEndMetres ||
             policy.LongRangeEndMetres < policy.DistantEndMetres))
            throw new ArgumentOutOfRangeException(nameof(request.AdaptiveSampling));
    }
}
