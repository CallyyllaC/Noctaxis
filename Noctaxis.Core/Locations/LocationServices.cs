using Noctaxis.Core.Localisation;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
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

/// <summary>Location search could not be answered (network, provider limit, unreadable reply). Its message is localised.</summary>
public sealed class LocationSearchUnavailableException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);
