using Noctaxis.Core.Localisation;
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
    internal const int MaximumCachedQueries = 128;
    public string Attribution => "Location data: Open-Meteo / GeoNames";

    public async Task<IReadOnlyList<LocationSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
                throw new LocationSearchUnavailableException(Strings.Get("Places_Search_RateLimited"));
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var dto = await JsonSerializer.DeserializeAsync<OpenMeteoGeocodingResponse>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            IReadOnlyList<LocationSearchResult> results = dto?.Results?.Select(item => new LocationSearchResult(
                item.Id.ToString(CultureInfo.InvariantCulture), item.Name,
                new GeoCoordinate(item.Latitude, item.Longitude, item.Elevation ?? 0),
                JoinRegion(item.Admin1, item.Country), item.Country, item.TimeZone, Attribution)).ToArray() ?? [];
            lock (_gate)
            {
                var now = clock.GetCurrentInstant();
                foreach (var expired in _cache.Where(item => now - item.Value.RetrievedAt >= Duration.FromMinutes(30))
                             .Select(item => item.Key).ToArray())
                    _cache.Remove(expired);
                _cache[query] = new CacheEntry(now, results);
                while (_cache.Count > MaximumCachedQueries)
                    _cache.Remove(_cache.MinBy(item => item.Value.RetrievedAt).Key);
            }
            return results;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            logger.LogWarning(ex, "Location search failed");
            throw new LocationSearchUnavailableException(Strings.Get("Places_Search_Unavailable"), ex);
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

/// <summary>Location search could not be answered (network, provider limit, unreadable reply). Its message is localised.</summary>
public sealed class LocationSearchUnavailableException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

public interface IReverseGeocodingProvider
{
    Task<ReverseGeocodingResult?> ResolveAsync(GeoCoordinate coordinate, CancellationToken cancellationToken);
}

/// <summary>
/// Lightweight, end-user-triggered reverse lookup. Calls are serialised and cached to respect the
/// public Nominatim service limit; provider DTOs never escape this boundary. When the service answers that it is
/// rate limiting or refusing this application (HTTP 429 or 403), no further request is sent for
/// <see cref="RefusalBackoff"/>: the policy limits are for the whole application, so one client must not keep asking.
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
    private Instant _suspendedUntil = Instant.MinValue;
    public const string Attribution = "Location data: OpenStreetMap contributors";
    /// <summary>How long lookups stop after the service rate-limits or refuses this application.</summary>
    public static readonly Duration RefusalBackoff = Duration.FromHours(1);

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
            if (clock.GetCurrentInstant() < _suspendedUntil) return null;
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
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
            {
                _suspendedUntil = clock.GetCurrentInstant() + RefusalBackoff;
                logger.LogWarning("Reverse geocoding refused (HTTP {Status}); no further lookups for {Backoff}",
                    (int)response.StatusCode, RefusalBackoff);
                return null;
            }
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
