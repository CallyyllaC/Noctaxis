using System.Text.Json;
using Microsoft.Extensions.Logging;
using Noctaxis.Core.Domain;
using NodaTime;

namespace Noctaxis.Core.Environment;

/// <summary>Honest VIIRS boundary until a radiance composite has been configured/acquired.</summary>
public sealed class UnavailableViirsLightPollutionProvider : ILightPollutionProvider
{
    public Task<EnvironmentalValue<LightPollutionSample>> GetLightPollutionAsync(GeoCoordinate coordinate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(EnvironmentalValue<LightPollutionSample>.Unavailable(
            "viirs-night-lights", "foundation-1",
            "VIIRS radiance data has not been acquired for this installation."));
    }
}

/// <summary>Global NOAA SWPC OVATION intensity plus separately-labelled planetary Kp.</summary>
public sealed class NoaaSwpcAuroraProvider(HttpClient http, ILogger<NoaaSwpcAuroraProvider> logger) : IAuroraProvider
{
    public const string SourceId = "noaa-swpc";
    public const string SourceVersion = "live-json-v1";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AuroraSnapshot? _snapshot;
    private static readonly Duration Freshness = Duration.FromMinutes(10);

    public async Task<EnvironmentalValue<AuroraEnvironment>> GetAuroraAsync(GeoCoordinate coordinate,
        CancellationToken cancellationToken)
    {
        var snapshot = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
            return EnvironmentalValue<AuroraEnvironment>.Unavailable(SourceId, SourceVersion,
                "NOAA SWPC aurora and geomagnetic data are unavailable.");
        var intensity = NearestIntensity(snapshot.Ovation, coordinate);
        var value = new AuroraEnvironment(intensity, snapshot.Kp, snapshot.ForecastTimestamp,
            snapshot.DataTimestamp);
        var state = intensity.HasValue || snapshot.Kp.HasValue
            ? intensity.HasValue && snapshot.Kp.HasValue ? EnvironmentalDataState.Available : EnvironmentalDataState.Partial
            : EnvironmentalDataState.Unavailable;
        return new EnvironmentalValue<AuroraEnvironment>(state, value, SourceId, SourceVersion,
            intensity.HasValue
                ? "NOAA OVATION local intensity and planetary geomagnetic activity."
                : "NOAA planetary geomagnetic activity; local OVATION intensity unavailable.",
            snapshot.DataTimestamp, snapshot.RetrievedAt);
    }

    private async Task<AuroraSnapshot?> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        if (_snapshot is not null && now - _snapshot.RetrievedAt < Freshness) return _snapshot;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = SystemClock.Instance.GetCurrentInstant();
            if (_snapshot is not null && now - _snapshot.RetrievedAt < Freshness) return _snapshot;
            IReadOnlyList<OvationCell> cells = [];
            Instant? observation = null;
            Instant? forecast = null;
            double? kp = null;
            try
            {
                using var ovation = await FetchJsonAsync("https://services.swpc.noaa.gov/json/ovation_aurora_latest.json",
                    cancellationToken).ConfigureAwait(false);
                cells = ParseOvation(ovation.RootElement, out observation, out forecast);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException)
            {
                logger.LogWarning(ex, "NOAA SWPC OVATION acquisition failed");
            }
            try
            {
                using var kpJson = await FetchJsonAsync("https://services.swpc.noaa.gov/products/noaa-planetary-k-index.json",
                    cancellationToken).ConfigureAwait(false);
                kp = ParseLatestKp(kpJson.RootElement);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException)
            {
                logger.LogWarning(ex, "NOAA SWPC planetary Kp acquisition failed");
            }
            if (cells.Count == 0 && !kp.HasValue) return _snapshot;
            _snapshot = new AuroraSnapshot(cells, kp, observation, forecast, now);
            return _snapshot;
        }
        finally { _gate.Release(); }
    }

    private async Task<JsonDocument> FetchJsonAsync(string url, CancellationToken cancellationToken)
    {
        var bytes = await EnvironmentalHttpDownloader.DownloadAsync(http, new Uri(url), 12 * 1024 * 1024,
            cancellationToken, maximumAttempts: 2).ConfigureAwait(false);
        if (bytes is null) throw new InvalidDataException("NOAA SWPC response was empty.");
        return JsonDocument.Parse(bytes);
    }

    private static IReadOnlyList<OvationCell> ParseOvation(JsonElement root, out Instant? observation,
        out Instant? forecast)
    {
        observation = ParseInstant(Property(root, "Observation Time"));
        forecast = ParseInstant(Property(root, "Forecast Time"));
        var cells = new List<OvationCell>();
        if (!root.TryGetProperty("coordinates", out var coordinates) || coordinates.ValueKind != JsonValueKind.Array)
            return cells;
        foreach (var cell in coordinates.EnumerateArray())
        {
            if (cell.ValueKind != JsonValueKind.Array || cell.GetArrayLength() < 3) continue;
            var longitude = cell[0].GetDouble();
            var latitude = cell[1].GetDouble();
            var intensity = cell[2].GetDouble();
            if (longitude > 180) longitude -= 360;
            if (double.IsFinite(latitude) && double.IsFinite(longitude) && double.IsFinite(intensity))
                cells.Add(new OvationCell(latitude, longitude, intensity));
        }
        return cells;
    }

    private static double? ParseLatestKp(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array) return null;
        double? latest = null;
        foreach (var row in root.EnumerateArray().Skip(1))
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 2) continue;
            if (double.TryParse(row[1].GetString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var value)) latest = value;
        }
        return latest;
    }

    private static double? NearestIntensity(IReadOnlyList<OvationCell> cells, GeoCoordinate coordinate)
    {
        if (cells.Count == 0) return null;
        var nearest = cells.MinBy(cell =>
        {
            var longitude = Math.Abs(cell.Longitude - coordinate.Longitude);
            longitude = Math.Min(longitude, 360 - longitude);
            return Math.Pow(cell.Latitude - coordinate.Latitude, 2) + Math.Pow(longitude, 2);
        });
        return nearest is null ? null : nearest.Intensity;
    }

    private static string? Property(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) ? property.GetString() : null;

    private static Instant? ParseInstant(string? value) => DateTimeOffset.TryParse(value,
        System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
        ? Instant.FromDateTimeOffset(parsed)
        : null;

    private sealed record OvationCell(double Latitude, double Longitude, double Intensity);
    private sealed record AuroraSnapshot(IReadOnlyList<OvationCell> Ovation, double? Kp,
        Instant? DataTimestamp, Instant? ForecastTimestamp, Instant RetrievedAt);
}

public sealed class LocationEnvironmentService(
    ITerrainElevationProvider terrain,
    ILandCoverProvider landCover,
    ISettlementDataProvider settlement,
    ILightPollutionProvider lightPollution,
    IAuroraProvider aurora) : ILocationEnvironmentService
{
    public async Task<LocationEnvironment> GetAsync(LocationEnvironmentRequest request,
        CancellationToken cancellationToken)
    {
        EnvironmentalValue<double>? terrainValue = null;
        EnvironmentalValue<LandCoverClass>? coverValue = null;
        EnvironmentalValue<SettlementRaster>? settlementValue = null;
        EnvironmentalValue<LightPollutionSample>? lightValue = null;
        EnvironmentalValue<AuroraEnvironment>? auroraValue = null;
        if (request.Layers.HasFlag(EnvironmentLayer.TerrainElevation))
            terrainValue = await terrain.GetElevationAsync(request.Coordinate, cancellationToken).ConfigureAwait(false);
        if (request.Layers.HasFlag(EnvironmentLayer.LandCover))
            coverValue = await landCover.GetLandCoverAsync(request.Coordinate, cancellationToken).ConfigureAwait(false);
        if (request.Layers.HasFlag(EnvironmentLayer.Settlement))
            settlementValue = request.SettlementArea is null
                ? EnvironmentalValue<SettlementRaster>.Unavailable("wsf-3d", "request",
                    "A settlement area/grid must be supplied.")
                : await settlement.GetSettlementAsync(request.SettlementArea, cancellationToken).ConfigureAwait(false);
        if (request.Layers.HasFlag(EnvironmentLayer.LightPollution))
            lightValue = await lightPollution.GetLightPollutionAsync(request.Coordinate, cancellationToken).ConfigureAwait(false);
        if (request.Layers.HasFlag(EnvironmentLayer.Aurora))
            auroraValue = await aurora.GetAuroraAsync(request.Coordinate, cancellationToken).ConfigureAwait(false);
        return new LocationEnvironment(request.Coordinate, terrainValue, coverValue,
            settlementValue, lightValue, auroraValue);
    }
}
