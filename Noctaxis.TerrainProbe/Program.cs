using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Environment;
using Noctaxis.Core.Persistence;
using Noctaxis.Core.Terrain;

if (args.Length is < 2 or > 4 ||
    !double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude) ||
    !double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude))
{
    Console.Error.WriteLine("Usage: Noctaxis.TerrainProbe <latitude> <longitude> [camera-height-metres] [horizon-distance-metres]");
    return 2;
}

var cameraHeight = args.Length >= 3
    ? double.Parse(args[2], CultureInfo.InvariantCulture)
    : 1.7;
var horizonDistance = args.Length >= 4
    ? double.Parse(args[3], CultureInfo.InvariantCulture)
    : 50_000;
var coordinate = new GeoCoordinate(latitude, longitude).Normalised();

using var loggerFactory = LoggerFactory.Create(builder =>
    builder.AddSimpleConsole(options => options.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
var paths = new ProbePaths();
var cache = new EnvironmentalTileCache(paths, loggerFactory.CreateLogger<EnvironmentalTileCache>());
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("Noctaxis-TerrainProbe/1.0");
var provider = new TerrariumTerrainProvider(http, cache,
    loggerFactory.CreateLogger<TerrariumTerrainProvider>(), new TerrariumTerrainOptions());
var worldCover = new WorldCoverLandCoverProvider(http, cache,
    loggerFactory.CreateLogger<WorldCoverLandCoverProvider>());
var surfaceResolver = new TerrainSurfaceResolver(provider, worldCover,
    loggerFactory.CreateLogger<TerrainSurfaceResolver>());
var observer = await surfaceResolver.GetSurfaceSampleAsync(coordinate, CancellationToken.None);
var horizonService = new HorizonService(surfaceResolver, loggerFactory.CreateLogger<HorizonService>());
var request = new TerrainProfileRequest(MaximumDistanceMetres: horizonDistance,
    ObserverHeightAboveGroundMetres: cameraHeight);
var profile = await horizonService.GetProfileAsync(coordinate, request, CancellationToken.None);

var rows = profile.Samples.Select(sample =>
{
    var line = sample.Sightline ?? [];
    var winning = sample.EffectiveHorizonFeatureDistanceMetres is double distance
        ? line.OrderBy(point => Math.Abs(point.DistanceMetres - distance)).FirstOrDefault()
        : default;
    return new
    {
        bearingDegrees = sample.BearingDegrees,
        horizonAltitudeDegrees = sample.EffectiveHorizonElevationDegrees,
        winningDistanceMetres = sample.EffectiveHorizonFeatureDistanceMetres,
        winningRawTerrariumElevationMetres = winning.RawTerrainElevationMetres,
        winningResolvedSurfaceElevationMetres = winning.GroundElevationMetres,
        winningClassification = winning.Classification?.ToString() ?? "Unavailable",
        winningSurfaceAdjusted = winning.SurfaceWasAdjusted,
        winningResolution = winning.SurfaceResolutionReason?.ToString() ?? "Unavailable",
        winningStatus = winning.GroundStatus.ToString(),
        samples = line.Count,
        noDataSamples = line.Count(point => !point.GroundElevationMetres.HasValue)
    };
}).ToArray();

var report = new
{
    requestedCoordinate = coordinate,
    observer = new
    {
        classification = observer.Resolution.Classification?.ToString() ?? "Unavailable",
        waterBodyKind = observer.Resolution.WaterBodyKind.ToString(),
        rawTerrariumElevationMetres = observer.Resolution.RawTerrainElevationMetres,
        resolvedSurfaceElevationMetres = observer.Resolution.SurfaceElevationMetres,
        surfaceWasAdjusted = observer.Resolution.WasAdjusted,
        surfaceResolution = observer.Resolution.Reason.ToString(),
        resolvedGroundElevationMetres = profile.ChosenObserverGroundElevationMetres,
        cameraHeightMetres = cameraHeight,
        effectiveCameraElevationMetres = profile.ObserverAbsoluteElevationMetres,
        provider = observer.RawTerrainDiagnostics.Provider,
        version = observer.RawTerrainDiagnostics.Version,
        tile = observer.RawTerrainDiagnostics.Tile,
        cell = observer.RawTerrainDiagnostics.Cell,
        resolution = observer.RawTerrainDiagnostics.Resolution,
        rawSamples = observer.RawTerrainDiagnostics.RawSamples,
        status = observer.SurfaceElevation.State.ToString(),
        message = TerrainSurfaceResolver.ResolutionMessage(observer.Resolution)
    },
    horizon = new
    {
        horizonDistanceMetres = horizonDistance,
        bearings = rows,
        summary = new
        {
            minimumAngleDegrees = rows.Min(row => row.horizonAltitudeDegrees),
            maximumAngleDegrees = rows.Max(row => row.horizonAltitudeDegrees),
            unavailableBearings = rows.Count(row => !row.horizonAltitudeDegrees.HasValue),
            profile.PipelineTimings
        }
    },
    providerMetrics = provider.Metrics
};

Console.WriteLine(TerrainProfileDiagnostics.CreateObserverSummary(profile));
Console.WriteLine();
Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
return 0;

file sealed class ProbePaths : IUserDataPathProvider
{
    public string GetApplicationDataDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("NOCTAXIS_TERRAIN_CACHE");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Noctaxis")
            : configured;
    }
}
