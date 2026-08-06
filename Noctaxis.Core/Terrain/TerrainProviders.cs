using System.Collections.Concurrent;
using Noctaxis.Core.Domain;
using NodaTime;

namespace Noctaxis.Core.Terrain;

public sealed record TerrainProfileRequest(
    int AzimuthSampleCount = 360,
    double MaximumDistanceMetres = 50_000,
    double DistanceStepMetres = 250,
    bool AccountForEarthCurvature = true);

public interface ITerrainHorizonProvider
{
    Task<TerrainHorizonProfile> GetProfileAsync(GeoCoordinate observer, TerrainProfileRequest request, CancellationToken cancellationToken);
}

public sealed class FlatTerrainHorizonProvider : ITerrainHorizonProvider
{
    public Task<TerrainHorizonProfile> GetProfileAsync(GeoCoordinate observer, TerrainProfileRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var samples = Enumerable.Range(0, request.AzimuthSampleCount)
            .Select(i => new TerrainHorizonSample(i * 360d / request.AzimuthSampleCount, 0, null)).ToArray();
        return Task.FromResult(new TerrainHorizonProfile(observer, samples, false, "Flat astronomical horizon (no DEM configured)", SystemClock.Instance.GetCurrentInstant()));
    }
}

public interface IDemDirectoryProvider
{
    string? DirectoryPath { get; set; }
}

public sealed class DemDirectoryProvider : IDemDirectoryProvider
{
    public string? DirectoryPath { get; set; }
}

public interface IElevationSource
{
    ValueTask<double?> GetElevationMetresAsync(GeoCoordinate coordinate, CancellationToken cancellationToken);
}

public sealed class SrtmElevationSource(IDemDirectoryProvider directoryProvider) : IElevationSource
{
    private readonly ConcurrentDictionary<string, Lazy<Task<HgtTile?>>> _tiles = new(StringComparer.OrdinalIgnoreCase);

    public async ValueTask<double?> GetElevationMetresAsync(GeoCoordinate coordinate, CancellationToken cancellationToken)
    {
        var directory = directoryProvider.DirectoryPath;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return null;
        var tileName = HgtTile.GetTileName(coordinate.Latitude, coordinate.Longitude);
        var cacheKey = Path.GetFullPath(directory) + "|" + tileName;
        var lazy = _tiles.GetOrAdd(cacheKey, _ => new Lazy<Task<HgtTile?>>(() => LocateAndLoadAsync(directory, tileName)));
        var tile = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        return tile?.Interpolate(coordinate.Latitude, coordinate.Longitude);
    }

    private static Task<HgtTile?> LocateAndLoadAsync(string directory, string tileName)
    {
        var expected = Path.Combine(directory, tileName + ".hgt");
        var actual = File.Exists(expected) ? expected : Directory.EnumerateFiles(directory, tileName + ".*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => Path.GetExtension(path).Equals(".hgt", StringComparison.OrdinalIgnoreCase));
        return actual is null ? Task.FromResult<HgtTile?>(null) : LoadOrNullAsync(actual);
    }

    private static async Task<HgtTile?> LoadOrNullAsync(string path)
    {
        try { return await HgtTile.LoadAsync(path, CancellationToken.None).ConfigureAwait(false); }
        catch (IOException) { return null; }
        catch (InvalidDataException) { return null; }
    }
}

public sealed class SrtmTerrainHorizonProvider(IElevationSource elevations) : ITerrainHorizonProvider
{
    private readonly ConcurrentDictionary<string, TerrainHorizonProfile> _cache = new();

    public async Task<TerrainHorizonProfile> GetProfileAsync(GeoCoordinate observer, TerrainProfileRequest request, CancellationToken cancellationToken)
    {
        Validate(request);
        var key = $"{observer.Latitude:F4}:{observer.Longitude:F4}:{observer.ElevationMetres:F0}:{request}";
        if (_cache.TryGetValue(key, out var cached)) return cached with { Status = cached.Status + " (cached)" };

        var profile = await Task.Run(async () =>
        {
            var observerDem = await elevations.GetElevationMetresAsync(observer, cancellationToken).ConfigureAwait(false);
            var observerElevation = observer.ElevationMetres != 0 ? observer.ElevationMetres : observerDem ?? 0;
            var samples = new TerrainHorizonSample[request.AzimuthSampleCount];
            var anyCoverage = observerDem.HasValue;
            const double earthRadius = 6_371_008.8;

            for (var index = 0; index < samples.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var azimuth = index * 360d / samples.Length;
                var maximumAngle = 0d;
                double? maximumDistance = null;
                for (var distance = request.DistanceStepMetres; distance <= request.MaximumDistanceMetres; distance += request.DistanceStepMetres)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var point = Angles.Destination(observer, azimuth, distance);
                    var elevation = await elevations.GetElevationMetresAsync(point, cancellationToken).ConfigureAwait(false);
                    if (!elevation.HasValue) continue;
                    anyCoverage = true;
                    var curvatureDrop = request.AccountForEarthCurvature ? distance * distance / (2 * earthRadius) : 0;
                    var angle = Math.Atan2(elevation.Value - observerElevation - curvatureDrop, distance) * Angles.RadiansToDegrees;
                    if (angle > maximumAngle)
                    {
                        maximumAngle = angle;
                        maximumDistance = distance;
                    }
                }
                samples[index] = new TerrainHorizonSample(azimuth, maximumAngle, maximumDistance);
            }

            var status = anyCoverage ? "SRTM terrain profile" : "No SRTM coverage found; using flat horizon";
            return new TerrainHorizonProfile(observer, samples, anyCoverage, status, SystemClock.Instance.GetCurrentInstant());
        }, cancellationToken).ConfigureAwait(false);

        _cache[key] = profile;
        return profile;
    }

    private static void Validate(TerrainProfileRequest request)
    {
        if (request.AzimuthSampleCount is < 8 or > 1440) throw new ArgumentOutOfRangeException(nameof(request.AzimuthSampleCount));
        if (request.MaximumDistanceMetres <= 0 || request.DistanceStepMetres <= 0 || request.DistanceStepMetres > request.MaximumDistanceMetres)
            throw new ArgumentOutOfRangeException(nameof(request));
    }
}
