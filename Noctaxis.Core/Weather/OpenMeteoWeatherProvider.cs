using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Noctaxis.Core.Calculations;
using Noctaxis.Core.Domain;
using NodaTime;

namespace Noctaxis.Core.Weather;

public sealed record WeatherRequest(
    GeoCoordinate Location,
    Instant ApproximateTime,
    IReadOnlyList<WeatherField> EnabledFields,
    double CacheDistanceKilometres,
    bool ForceRefresh = false);

public interface IWeatherProvider
{
    Task<WeatherResult> GetWeatherAsync(WeatherRequest request, CancellationToken cancellationToken);
}

public interface IGeographicWeatherCache
{
    Duration MaximumAge { get; }
    bool TryGet(GeoCoordinate location, Instant forecastTime, double maximumDistanceKilometres, out WeatherConditions conditions);
    void Store(GeoCoordinate location, WeatherConditions conditions);
}

public sealed class GeographicWeatherCache(IClock clock) : IGeographicWeatherCache
{
    private sealed record Entry(GeoCoordinate Coordinate, WeatherConditions Conditions);
    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];

    public Duration MaximumAge { get; } = Duration.FromMinutes(10);

    public bool TryGet(GeoCoordinate location, Instant forecastTime, double maximumDistanceKilometres, out WeatherConditions conditions)
    {
        lock (_gate)
        {
            var now = clock.GetCurrentInstant();
            _entries.RemoveAll(entry => now - entry.Conditions.RetrievedAt >= MaximumAge);
            var match = _entries
                .Where(entry => Math.Abs((entry.Conditions.ForecastInstant - forecastTime).TotalMinutes) < 60)
                .Select(entry => (Entry: entry, Distance: Angles.GreatCircleDistanceMetres(location, entry.Coordinate)))
                .Where(candidate => candidate.Distance <= Math.Max(0, maximumDistanceKilometres) * 1_000)
                .OrderBy(candidate => candidate.Distance)
                .FirstOrDefault();

            if (match.Entry is not null)
            {
                conditions = match.Entry.Conditions with { IsStale = false };
                return true;
            }
        }

        conditions = null!;
        return false;
    }

    public void Store(GeoCoordinate location, WeatherConditions conditions)
    {
        lock (_gate)
        {
            _entries.RemoveAll(entry =>
                Angles.GreatCircleDistanceMetres(location, entry.Coordinate) < 10 &&
                Math.Abs((entry.Conditions.ForecastInstant - conditions.ForecastInstant).TotalMinutes) < 60);
            _entries.Add(new Entry(location, conditions));
            if (_entries.Count > 32)
                _entries.RemoveRange(0, _entries.Count - 32);
        }
    }
}

public sealed class OpenMeteoWeatherProvider(
    HttpClient httpClient,
    IGeographicWeatherCache cache,
    IClock clock,
    ILogger<OpenMeteoWeatherProvider> logger) : IWeatherProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<WeatherResult> GetWeatherAsync(WeatherRequest request, CancellationToken cancellationToken)
    {
        if (!request.ForceRefresh && cache.TryGet(request.Location, request.ApproximateTime,
                request.CacheDistanceKilometres, out var cached))
        {
            logger.LogInformation("Weather cache hit within {DistanceKm} km", request.CacheDistanceKilometres);
            return new WeatherResult(DataState.Ready, cached, "Cached Open-Meteo forecast");
        }

        logger.LogInformation(request.ForceRefresh
            ? "Forced Open-Meteo weather refresh"
            : "Weather cache miss; requesting Open-Meteo forecast");

        try
        {
            var uri = BuildUri(request);
            using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return new WeatherResult(DataState.Error, null, "Open-Meteo rate limit reached. Try again shortly.");
            if (!response.IsSuccessStatusCode)
                return new WeatherResult(DataState.Error, null, $"Open-Meteo returned HTTP {(int)response.StatusCode}.");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var dto = await JsonSerializer.DeserializeAsync<OpenMeteoResponse>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            var conditions = OpenMeteoMapper.Map(dto ?? throw new JsonException("Open-Meteo returned an empty response."),
                request.ApproximateTime, clock.GetCurrentInstant());
            cache.Store(request.Location, conditions);
            return new WeatherResult(DataState.Ready, conditions, "Open-Meteo forecast");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or FormatException)
        {
            logger.LogWarning(ex, "Open-Meteo request failed");
            return new WeatherResult(DataState.Error, null, "Weather unavailable: " + ex.Message);
        }
    }

    internal static string BuildUri(WeatherRequest request)
    {
        var variables = OpenMeteoVariables.For(request.EnabledFields);
        return FormattableString.Invariant($"forecast?latitude={request.Location.Latitude:F6}&longitude={request.Location.Longitude:F6}&hourly={Uri.EscapeDataString(string.Join(',', variables))}&timezone=UTC&timeformat=unixtime&wind_speed_unit=ms&past_days=1&forecast_days=16");
    }
}

internal static class OpenMeteoVariables
{
    public static IReadOnlyList<string> For(IReadOnlyList<WeatherField> fields)
    {
        var result = new HashSet<string>(StringComparer.Ordinal) { "weather_code" };
        foreach (var field in fields)
        {
            var variable = field switch
            {
                WeatherField.TotalCloudCover => "cloud_cover",
                WeatherField.LowCloudCover => "cloud_cover_low",
                WeatherField.MediumCloudCover => "cloud_cover_mid",
                WeatherField.HighCloudCover => "cloud_cover_high",
                WeatherField.PrecipitationProbability => "precipitation_probability",
                WeatherField.PrecipitationAmount => "precipitation",
                WeatherField.Visibility => "visibility",
                WeatherField.Temperature => "temperature_2m",
                WeatherField.DewPoint => "dew_point_2m",
                WeatherField.RelativeHumidity => "relative_humidity_2m",
                WeatherField.WindSpeed => "wind_speed_10m",
                WeatherField.WindGusts => "wind_gusts_10m",
                WeatherField.WindDirection => "wind_direction_10m",
                _ => null
            };
            if (variable is not null) result.Add(variable);
        }
        return result.ToArray();
    }
}

public static class OpenMeteoMapper
{
    public static WeatherConditions Map(OpenMeteoResponse response, Instant selectedTime, Instant retrievedAt)
    {
        var hourly = response.Hourly ?? throw new JsonException("Open-Meteo response has no hourly data.");
        if (hourly.Time is null || hourly.Time.Length == 0)
            throw new JsonException("Open-Meteo response has no forecast times.");

        var selectedUnix = selectedTime.ToUnixTimeSeconds();
        var index = Enumerable.Range(0, hourly.Time.Length)
            .MinBy(i => Math.Abs(hourly.Time[i] - selectedUnix));
        var forecastInstant = Instant.FromUnixTimeSeconds(hourly.Time[index]);
        if (Math.Abs((forecastInstant - selectedTime).TotalMinutes) > 90)
            throw new JsonException("The selected time is outside the available Open-Meteo forecast range.");
        var code = Value(hourly.WeatherCode, index);

        return new WeatherConditions(
            forecastInstant,
            Value(hourly.CloudCover, index), Value(hourly.CloudCoverLow, index),
            Value(hourly.CloudCoverMid, index), Value(hourly.CloudCoverHigh, index),
            Value(hourly.PrecipitationProbability, index), Value(hourly.Precipitation, index),
            PrecipitationType(code),
            Value(hourly.WindSpeed10m, index), Value(hourly.WindDirection10m, index),
            Value(hourly.WindGusts10m, index), Value(hourly.Temperature2m, index),
            Value(hourly.RelativeHumidity2m, index), Value(hourly.DewPoint2m, index),
            Divide(Value(hourly.Visibility, index), 1_000), Summary(code), retrievedAt);
    }

    private static double? Value(double?[]? values, int index) =>
        values is not null && index < values.Length ? values[index] : null;

    private static double? Divide(double? value, double divisor) => value.HasValue ? value.Value / divisor : null;

    private static string Summary(double? value) => value.HasValue ? (int)value.Value switch
    {
        0 => "Clear sky", 1 => "Mainly clear", 2 => "Partly cloudy", 3 => "Overcast",
        45 or 48 => "Fog", >= 51 and <= 57 => "Drizzle", >= 61 and <= 67 => "Rain",
        >= 71 and <= 77 => "Snow", >= 80 and <= 82 => "Rain showers",
        85 or 86 => "Snow showers", >= 95 and <= 99 => "Thunderstorm", _ => "Unknown conditions"
    } : "No summary";

    private static string? PrecipitationType(double? value) => value.HasValue ? (int)value.Value switch
    {
        >= 51 and <= 57 => "Drizzle", >= 61 and <= 67 => "Rain", >= 71 and <= 77 => "Snow",
        >= 80 and <= 82 => "Rain showers", 85 or 86 => "Snow showers",
        >= 95 and <= 99 => "Thunderstorm", _ => "None"
    } : null;
}

public sealed class OpenMeteoResponse
{
    [JsonPropertyName("hourly")]
    public OpenMeteoHourly? Hourly { get; init; }
}

public sealed class OpenMeteoHourly
{
    [JsonPropertyName("time")] public long[]? Time { get; init; }
    [JsonPropertyName("temperature_2m")] public double?[]? Temperature2m { get; init; }
    [JsonPropertyName("relative_humidity_2m")] public double?[]? RelativeHumidity2m { get; init; }
    [JsonPropertyName("dew_point_2m")] public double?[]? DewPoint2m { get; init; }
    [JsonPropertyName("precipitation_probability")] public double?[]? PrecipitationProbability { get; init; }
    [JsonPropertyName("precipitation")] public double?[]? Precipitation { get; init; }
    [JsonPropertyName("weather_code")] public double?[]? WeatherCode { get; init; }
    [JsonPropertyName("cloud_cover")] public double?[]? CloudCover { get; init; }
    [JsonPropertyName("cloud_cover_low")] public double?[]? CloudCoverLow { get; init; }
    [JsonPropertyName("cloud_cover_mid")] public double?[]? CloudCoverMid { get; init; }
    [JsonPropertyName("cloud_cover_high")] public double?[]? CloudCoverHigh { get; init; }
    [JsonPropertyName("visibility")] public double?[]? Visibility { get; init; }
    [JsonPropertyName("wind_speed_10m")] public double?[]? WindSpeed10m { get; init; }
    [JsonPropertyName("wind_direction_10m")] public double?[]? WindDirection10m { get; init; }
    [JsonPropertyName("wind_gusts_10m")] public double?[]? WindGusts10m { get; init; }
}

public sealed class OfflineWeatherProvider : IWeatherProvider
{
    public Task<WeatherResult> GetWeatherAsync(WeatherRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new WeatherResult(DataState.Error, null, "Weather provider is not available."));
}
