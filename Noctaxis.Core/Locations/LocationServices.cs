using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Noctaxis.Core.Calculations;
using Noctaxis.Core.Domain;
using NodaTime;

namespace Noctaxis.Core.Locations;

public interface ILocationSearchProvider
{
    string Attribution { get; }
    Task<IReadOnlyList<LocationSearchResult>> SearchAsync(string query, CancellationToken cancellationToken);
}

public sealed class OpenMeteoLocationSearchProvider(
    HttpClient httpClient,
    IClock clock,
    ILogger<OpenMeteoLocationSearchProvider> logger) : ILocationSearchProvider
{
    private sealed record CacheEntry(Instant RetrievedAt, IReadOnlyList<LocationSearchResult> Results);
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    public string Attribution => "Location data: Open-Meteo / GeoNames";

    public async Task<IReadOnlyList<LocationSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        query = query.Trim();
        if (query.Length < 2) return [];
        lock (_gate)
        {
            if (_cache.TryGetValue(query, out var cached) && clock.GetCurrentInstant() - cached.RetrievedAt < Duration.FromMinutes(30))
                return cached.Results;
        }

        try
        {
            var uri = $"search?name={Uri.EscapeDataString(query)}&count=8&language={CultureInfo.CurrentUICulture.TwoLetterISOLanguageName}&format=json";
            using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new HttpRequestException("Location search rate limit reached.");
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var dto = await JsonSerializer.DeserializeAsync<OpenMeteoGeocodingResponse>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            IReadOnlyList<LocationSearchResult> results = dto?.Results?.Select(item => new LocationSearchResult(
                item.Id.ToString(CultureInfo.InvariantCulture), item.Name,
                new GeoCoordinate(item.Latitude, item.Longitude, item.Elevation ?? 0),
                JoinRegion(item.Admin1, item.Country), item.Country, item.TimeZone, Attribution)).ToArray() ?? [];
            lock (_gate) _cache[query] = new CacheEntry(clock.GetCurrentInstant(), results);
            return results;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
        {
            logger.LogWarning(ex, "Location search failed");
            throw new InvalidOperationException("Location search is unavailable. Check the network connection and try again.", ex);
        }
    }

    private static string? JoinRegion(string? region, string? country)
    {
        var values = new[] { region, country }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray();
        return values.Length == 0 ? null : string.Join(", ", values);
    }
}

public sealed class OpenMeteoGeocodingResponse
{
    [JsonPropertyName("results")] public OpenMeteoGeocodingResult[]? Results { get; init; }
}

public sealed class OpenMeteoGeocodingResult
{
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("latitude")] public double Latitude { get; init; }
    [JsonPropertyName("longitude")] public double Longitude { get; init; }
    [JsonPropertyName("elevation")] public double? Elevation { get; init; }
    [JsonPropertyName("country")] public string? Country { get; init; }
    [JsonPropertyName("admin1")] public string? Admin1 { get; init; }
    [JsonPropertyName("timezone")] public string? TimeZone { get; init; }
}

public sealed record ReverseGeocodingResult(string PlaceName, string? RegionDescription, string Attribution);

public interface IReverseGeocodingProvider
{
    Task<ReverseGeocodingResult?> ResolveAsync(GeoCoordinate coordinate, CancellationToken cancellationToken);
}

/// <summary>
/// Lightweight, end-user-triggered reverse lookup. Calls are serialised and cached to respect the
/// public Nominatim service limit; provider DTOs never escape this boundary.
/// </summary>
public sealed class NominatimReverseGeocodingProvider(
    HttpClient httpClient,
    IClock clock,
    ILogger<NominatimReverseGeocodingProvider> logger) : IReverseGeocodingProvider
{
    private sealed record CacheEntry(GeoCoordinate Coordinate, Instant RetrievedAt, ReverseGeocodingResult? Result);
    private readonly List<CacheEntry> _cache = [];
    private readonly object _cacheGate = new();
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private DateTimeOffset _lastRequestUtc = DateTimeOffset.MinValue;
    public const string Attribution = "Location data: OpenStreetMap contributors";

    public async Task<ReverseGeocodingResult?> ResolveAsync(GeoCoordinate coordinate, CancellationToken cancellationToken)
    {
        coordinate = coordinate.Normalised();
        lock (_cacheGate)
        {
            var cached = _cache.LastOrDefault(entry =>
                clock.GetCurrentInstant() - entry.RetrievedAt < Duration.FromHours(24) &&
                Angles.GreatCircleDistanceMetres(coordinate, entry.Coordinate) <= 50);
            if (cached is not null) return cached.Result;
        }

        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var delay = TimeSpan.FromSeconds(1) - (DateTimeOffset.UtcNow - _lastRequestUtc);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            var language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            var uri = $"reverse?format=jsonv2&lat={coordinate.Latitude.ToString("F7", CultureInfo.InvariantCulture)}" +
                      $"&lon={coordinate.Longitude.ToString("F7", CultureInfo.InvariantCulture)}" +
                      $"&zoom=14&addressdetails=1&accept-language={Uri.EscapeDataString(language)}";
            using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            _lastRequestUtc = DateTimeOffset.UtcNow;
            if (response.StatusCode == HttpStatusCode.NotFound) return Cache(coordinate, null);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new HttpRequestException("Reverse-geocoding rate limit reached.");
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var dto = await JsonSerializer.DeserializeAsync<NominatimReverseResponse>(stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var place = dto?.Address?.PreferredLocality();
            if (string.IsNullOrWhiteSpace(place)) place = dto?.DisplayName?.Split(',')[0].Trim();
            var result = string.IsNullOrWhiteSpace(place)
                ? null
                : new ReverseGeocodingResult(place, dto?.Address?.Region(), Attribution);
            return Cache(coordinate, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
        {
            logger.LogWarning(ex, "Reverse geocoding failed");
            return null;
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private ReverseGeocodingResult? Cache(GeoCoordinate coordinate, ReverseGeocodingResult? result)
    {
        lock (_cacheGate)
        {
            _cache.Add(new CacheEntry(coordinate, clock.GetCurrentInstant(), result));
            if (_cache.Count > 128) _cache.RemoveRange(0, _cache.Count - 128);
        }
        return result;
    }
}

public sealed class NominatimReverseResponse
{
    [JsonPropertyName("display_name")] public string? DisplayName { get; init; }
    [JsonPropertyName("address")] public NominatimAddress? Address { get; init; }
}

public sealed class NominatimAddress
{
    [JsonPropertyName("village")] public string? Village { get; init; }
    [JsonPropertyName("town")] public string? Town { get; init; }
    [JsonPropertyName("city")] public string? City { get; init; }
    [JsonPropertyName("municipality")] public string? Municipality { get; init; }
    [JsonPropertyName("hamlet")] public string? Hamlet { get; init; }
    [JsonPropertyName("suburb")] public string? Suburb { get; init; }
    [JsonPropertyName("neighbourhood")] public string? Neighbourhood { get; init; }
    [JsonPropertyName("county")] public string? County { get; init; }
    [JsonPropertyName("state")] public string? State { get; init; }
    [JsonPropertyName("country")] public string? Country { get; init; }

    public string? PreferredLocality() => First(Village, Town, City, Municipality, Hamlet, Suburb, Neighbourhood, County);
    public string? Region() => First(State, County, Country);
    private static string? First(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}

public interface IDeviceLocationProvider
{
    Task<LocationResolution?> TryGetLocationAsync(CancellationToken cancellationToken);
}

public enum DeviceLocationAvailabilityState
{
    Available,
    PermissionRequestable,
    PermissionPermanentlyDenied,
    Unsupported
}

public sealed record DeviceLocationAvailability(DeviceLocationAvailabilityState State, string? Reason = null)
{
    public bool CanRequest => State is DeviceLocationAvailabilityState.Available or DeviceLocationAvailabilityState.PermissionRequestable;
}

public interface IDeviceLocationAvailabilityService
{
    Task<DeviceLocationAvailability> GetAvailabilityAsync(CancellationToken cancellationToken);
}

public sealed class PlatformDeviceLocationProvider : IDeviceLocationProvider, IDeviceLocationAvailabilityService
{
    public Task<DeviceLocationAvailability> GetAvailabilityAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reason = OperatingSystem.IsLinux()
            ? "Device location is unavailable because no supported GeoClue service is configured."
            : OperatingSystem.IsWindows()
                ? "Device location is unavailable in this desktop build."
                : "Device location is not supported on this platform.";
        return Task.FromResult(new DeviceLocationAvailability(DeviceLocationAvailabilityState.Unsupported, reason));
    }

    public Task<LocationResolution?> TryGetLocationAsync(CancellationToken cancellationToken) => Task.FromResult<LocationResolution?>(null);
}

public sealed class UnavailableDeviceLocationProvider : IDeviceLocationProvider, IDeviceLocationAvailabilityService
{
    public Task<DeviceLocationAvailability> GetAvailabilityAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new DeviceLocationAvailability(DeviceLocationAvailabilityState.Unsupported, "Device location is unavailable."));
    public Task<LocationResolution?> TryGetLocationAsync(CancellationToken cancellationToken) => Task.FromResult<LocationResolution?>(null);
}

public interface ILocationResolver
{
    Task<LocationResolution> ResolveDefaultAsync(GeoCoordinate? lastCustomCoordinate, CancellationToken cancellationToken);
    Task<LocationResolution> ResolveDeviceOrFallbackAsync(GeoCoordinate? lastCustomCoordinate, CancellationToken cancellationToken);
}

public sealed class LocationResolver(IDeviceLocationProvider deviceLocation, ILogger<LocationResolver> logger) : ILocationResolver
{
    private static readonly IReadOnlyDictionary<string, GeoCoordinate> CountryCentres = new Dictionary<string, GeoCoordinate>(StringComparer.OrdinalIgnoreCase)
    {
        ["GB"] = new(54.7, -3.5), ["US"] = new(39.8, -98.6), ["CA"] = new(56.1, -106.3),
        ["AU"] = new(-25.3, 133.8), ["NZ"] = new(-41.3, 174.8), ["DE"] = new(51.2, 10.4),
        ["FR"] = new(46.2, 2.2), ["ES"] = new(40.4, -3.7), ["IT"] = new(42.8, 12.8),
        ["NL"] = new(52.2, 5.3), ["NO"] = new(61.0, 8.0), ["SE"] = new(62.0, 15.0),
        ["IN"] = new(22.6, 79.0), ["JP"] = new(36.2, 138.3), ["BR"] = new(-10.8, -52.9)
    };
    private static readonly GeoCoordinate GlobalFallback = new(20, 0);

    public Task<LocationResolution> ResolveDefaultAsync(GeoCoordinate? lastCustomCoordinate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (lastCustomCoordinate.HasValue)
            return Task.FromResult(new LocationResolution(lastCustomCoordinate.Value, LocationResolutionSource.LastCustomPosition));
        try
        {
            var region = RegionInfo.CurrentRegion;
            if (CountryCentres.TryGetValue(region.TwoLetterISORegionName, out var coordinate))
                return Task.FromResult(new LocationResolution(coordinate, LocationResolutionSource.SystemRegion,
                    IsApproximate: true, DisplayName: region.DisplayName));
        }
        catch (ArgumentException) { }
        return Task.FromResult(new LocationResolution(GlobalFallback, LocationResolutionSource.ApplicationFallback,
            IsApproximate: true, DisplayName: "Global map"));
    }

    public async Task<LocationResolution> ResolveDeviceOrFallbackAsync(GeoCoordinate? lastCustomCoordinate, CancellationToken cancellationToken)
    {
        try
        {
            var result = await deviceLocation.TryGetLocationAsync(cancellationToken).ConfigureAwait(false);
            if (result is not null) return result;
            logger.LogInformation("Operating-system location is unavailable; using location fallback");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { logger.LogWarning(ex, "Operating-system location failed; using location fallback"); }
        return await ResolveDefaultAsync(lastCustomCoordinate, cancellationToken).ConfigureAwait(false);
    }
}
