using System.Diagnostics;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Noctaxis.Core.Persistence;

namespace Noctaxis.Desktop.Services;

public enum BuildingStarStatus { Complete, Cached, Partial, Unavailable }

public sealed record BuildingStarFeature(
    string OsmType,
    long OsmId,
    double Latitude,
    double Longitude,
    string? Building,
    int? Levels,
    string? Name);

public sealed record BuildingCacheRegion(
    string CacheKey,
    MapGeographicBounds Bounds,
    int? Zoom,
    int? X,
    int? Y,
    int Depth,
    bool CacheHit,
    bool Complete,
    string? FailureCode = null,
    string? FailureReason = null);

public sealed record BuildingFeatureSourceMetadata(
    string ProviderId,
    string ProviderName,
    string AttributionText,
    string AttributionUrl,
    string LicenceName,
    string LicenceUrl,
    string EndpointId,
    int QueryVersion,
    DateTimeOffset FetchedAtUtc);

public sealed record BuildingFeatureDocument(
    int SchemaVersion,
    Guid LocationId,
    double SourceCentreLatitude,
    double SourceCentreLongitude,
    int SourceZoom,
    int SourceWidth,
    int SourceHeight,
    MapGeographicBounds Bounds,
    BuildingFeatureSourceMetadata Source,
    BuildingStarFeature[] Buildings,
    bool SubdivisionUsed,
    BuildingCacheRegion[] Regions)
{
    public int BuildingCount => Buildings.Length;
}

public sealed record BuildingFeatureFetchOutcome(
    BuildingStarStatus Status,
    int BuildingCount,
    string? FailureCode,
    string? FailureReason,
    int? HttpStatusCode,
    bool TimedOut,
    bool ResponseTooLarge,
    int AttemptCount,
    DateTimeOffset AttemptedAtUtc,
    bool SubdivisionUsed,
    int SubdivisionDepth,
    int RegionCount,
    int CompletedRegionCount,
    int FailedRegionCount,
    int CacheHitCount,
    int CacheMissCount,
    int DownloadedRegionCount)
{
    public static BuildingFeatureFetchOutcome Unavailable(string code, string reason, int attempts = 1,
        int? httpStatus = null, bool timedOut = false, bool responseTooLarge = false,
        bool subdivision = false, int depth = 0, int regions = 0, int completed = 0,
        int failed = 0, int hits = 0, int misses = 0, int downloaded = 0) => new(
        BuildingStarStatus.Unavailable, 0, code, reason, httpStatus, timedOut, responseTooLarge,
        attempts, DateTimeOffset.UtcNow, subdivision, depth, regions, completed, failed, hits, misses,
        downloaded);
}

public sealed record BuildingFeatureFetchResult(
    BuildingFeatureDocument? Data,
    BuildingFeatureFetchOutcome Outcome);

public interface IBuildingFeatureDataService
{
    string SharedCacheDirectory { get; }
    Task<BuildingFeatureFetchResult> FetchAsync(Guid locationId, string locationName,
        WebMercatorViewport viewport, bool forceRefresh, CancellationToken cancellationToken);
}

public sealed class NullBuildingFeatureDataService : IBuildingFeatureDataService
{
    public string SharedCacheDirectory => string.Empty;
    public Task<BuildingFeatureFetchResult> FetchAsync(Guid locationId, string locationName,
        WebMercatorViewport viewport, bool forceRefresh, CancellationToken cancellationToken) => Task.FromResult(
        new BuildingFeatureFetchResult(null, BuildingFeatureFetchOutcome.Unavailable(
            "not_configured", "Building-star data is not configured.")));
}

public sealed class OverpassBuildingFeatureDataService(
    HttpClient httpClient,
    OverpassMapFeatureOptions options,
    IUserDataPathProvider paths,
    ILogger<OverpassBuildingFeatureDataService> logger) : IBuildingFeatureDataService
{
    public const int SchemaVersion = 1;
    public const int QueryVersion = 1;
    public const int BuildingCacheZoom = 12;
    public const int MaximumSubdivisionDepth = 2;
    public const int MaximumRequestsPerRefresh = 16;
    public const int MaximumResponseBytes = 8 * 1024 * 1024;
    public const int MaximumBuildings = 100_000;
    private static readonly TimeSpan BulkDeduplicationWindow = TimeSpan.FromSeconds(30);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentForcedRefreshes = new();

    public string SharedCacheDirectory { get; } = Path.Combine(paths.GetApplicationDataDirectory(),
        "BuildingFeatureCache", "osm", $"building-query-v{QueryVersion}");

    public async Task<BuildingFeatureFetchResult> FetchAsync(Guid locationId, string locationName,
        WebMercatorViewport viewport, bool forceRefresh, CancellationToken cancellationToken)
    {
        var attemptedAt = DateTimeOffset.UtcNow;
        var fullKey = RegionKey(viewport.Bounds);
        logger.LogInformation(
            "Building-star refresh started for {LocationId} ({LocationName}); full-region key {CacheKey}",
            locationId, locationName, fullKey);
        logger.LogDebug("Building viewport bounds for {LocationId}: {South},{West},{North},{East}",
            locationId, viewport.Bounds.South, viewport.Bounds.West, viewport.Bounds.North, viewport.Bounds.East);

        var recent = await TryReadRegionAsync(FullRegionPath(fullKey), viewport.Bounds, cancellationToken)
            .ConfigureAwait(false);
        if (recent is not null && (!forceRefresh || WasRecentlyRefreshed(fullKey, attemptedAt)))
        {
            var document = CreateDocument(locationId, viewport, recent.Buildings, false,
                [new BuildingCacheRegion(fullKey, viewport.Bounds, null, null, null, 0, true, true)],
                recent.FetchedAtUtc);
            return new BuildingFeatureFetchResult(document, new BuildingFeatureFetchOutcome(
                BuildingStarStatus.Cached, document.BuildingCount, null, null, null, false, false, 0,
                attemptedAt, false, 0, 1, 1, 0, 1, 0, 0));
        }

        var requestCount = 0;
        try
        {
            requestCount++;
            var full = await QueryRegionAsync(viewport.Bounds, "full", fullKey, 0, cancellationToken)
                .ConfigureAwait(false);
            await WriteRegionAsync(FullRegionPath(fullKey), fullKey, viewport.Bounds, full.Buildings,
                full.ResponseBytes, full.HttpStatusCode, cancellationToken).ConfigureAwait(false);
            if (forceRefresh) MarkRecentlyRefreshed(fullKey);
            var document = CreateDocument(locationId, viewport, full.Buildings, false,
                [new BuildingCacheRegion(fullKey, viewport.Bounds, null, null, null, 0, false, true)],
                DateTimeOffset.UtcNow);
            return new BuildingFeatureFetchResult(document, new BuildingFeatureFetchOutcome(
                BuildingStarStatus.Complete, document.BuildingCount, null, null, full.HttpStatusCode,
                false, false, requestCount, attemptedAt, false, 0, 1, 1, 0, 0, 1, 1));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (MapFeatureRequestException ex) when (ex.DensityRelated)
        {
            logger.LogWarning(ex,
                "Full building query failed with {FailureCode}; using bounded tile subdivision for {LocationId}",
                ex.Code, locationId);
            return await FetchSubdividedAsync(locationId, viewport, attemptedAt, ex, requestCount,
                forceRefresh,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var failure = Classify(ex);
            logger.LogWarning(ex, "Building-star request failed for {LocationId}: {FailureCode}",
                locationId, failure.Code);
            return new BuildingFeatureFetchResult(null, BuildingFeatureFetchOutcome.Unavailable(
                failure.Code, failure.Reason, requestCount, failure.HttpStatus, failure.TimedOut,
                failure.ResponseTooLarge));
        }
    }

    public static string BuildQuery(MapGeographicBounds bounds)
    {
        var bbox = string.Join(',', F(bounds.South), F(bounds.West), F(bounds.North), F(bounds.East));
        return "[out:json][timeout:25];\n(\n" +
               $"  way[\"building\"]({bbox});\n" +
               $"  relation[\"building\"]({bbox});\n" +
               ");\nout tags center qt;";
    }

    public static BuildingStarFeature[] Parse(byte[] json, out int malformed)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        if (document.RootElement.TryGetProperty("remark", out var remark) &&
            remark.ValueKind == JsonValueKind.String && IsDensityMessage(remark.GetString()))
            throw new MapFeatureRequestException("overpass_density_limit",
                "the Overpass building query exceeded a runtime or memory limit", densityRelated: true);
        if (!document.RootElement.TryGetProperty("elements", out var elements) ||
            elements.ValueKind != JsonValueKind.Array)
            throw new MapFeatureRequestException("parse_failed",
                "the building response did not contain an elements array", parseFailed: true);
        if (elements.GetArrayLength() > MaximumBuildings)
            throw new MapFeatureRequestException("response_too_large",
                "the building response contained too many elements", responseTooLarge: true,
                densityRelated: true);

        malformed = 0;
        var values = new Dictionary<(string Type, long Id), BuildingStarFeature>();
        foreach (var element in elements.EnumerateArray())
        {
            var type = Text(element, "type");
            if (type is not ("way" or "relation") ||
                !element.TryGetProperty("id", out var idNode) || !idNode.TryGetInt64(out var id))
            { malformed++; continue; }
            var coordinate = ReadCentre(element);
            if (coordinate is null)
            { malformed++; continue; }
            var tags = element.TryGetProperty("tags", out var tagNode) && tagNode.ValueKind == JsonValueKind.Object
                ? tagNode : default;
            var building = Tag(tags, "building");
            if (string.IsNullOrWhiteSpace(building))
            { malformed++; continue; }
            values.TryAdd((type, id), new BuildingStarFeature(type, id, coordinate.Value.Latitude,
                coordinate.Value.Longitude, building, ParseLevels(Tag(tags, "building:levels")),
                Tag(tags, "name")));
        }
        return values.Values.ToArray();
    }

    private async Task<BuildingFeatureFetchResult> FetchSubdividedAsync(Guid locationId,
        WebMercatorViewport viewport, DateTimeOffset attemptedAt, MapFeatureRequestException fullFailure,
        int initialRequestCount, bool forceRefresh, CancellationToken cancellationToken)
    {
        var state = new SubdivisionState(initialRequestCount);
        var regions = InitialRegions(viewport.Bounds).ToArray();
        foreach (var region in regions)
        {
            if (state.RequestCount >= MaximumRequestsPerRefresh)
            {
                state.Results.Add(FailedRegion(region, 0, "request_limit",
                    "the bounded building request limit was reached"));
                continue;
            }
            await ResolveRegionAsync(region, depth: 0, state, forceRefresh, cancellationToken).ConfigureAwait(false);
        }

        var buildings = state.Results.Where(result => result.Complete)
            .SelectMany(result => result.Buildings)
            .Where(building => viewport.Bounds.Contains(building.Latitude, building.Longitude))
            .DistinctBy(building => (building.OsmType, building.OsmId)).ToArray();
        var failed = state.Results.Count(result => !result.Complete);
        var completed = state.Results.Count(result => result.Complete);
        var maxDepth = state.Results.Count == 0 ? 0 : state.Results.Max(result => result.Region.Depth);
        var status = failed == 0 ? BuildingStarStatus.Complete
            : buildings.Length > 0 ? BuildingStarStatus.Partial : BuildingStarStatus.Unavailable;
        var regionMetadata = state.Results.Select(result => result.Region with
        {
            CacheHit = result.CacheHit,
            Complete = result.Complete,
            FailureCode = result.FailureCode,
            FailureReason = result.FailureReason
        }).ToArray();
        var reason = failed == 0 ? $"Full viewport failed; subdivision loaded {completed} regions."
            : $"Building viewport query failed; subdivision loaded {completed} of {completed + failed} regions.";
        var document = buildings.Length == 0 ? null : CreateDocument(locationId, viewport, buildings, true,
            regionMetadata, DateTimeOffset.UtcNow);
        var outcome = new BuildingFeatureFetchOutcome(status, buildings.Length,
            failed == 0 ? fullFailure.Code : "partial_regions", reason, fullFailure.HttpStatusCode,
            fullFailure.TimedOut, fullFailure.ResponseTooLarge, state.RequestCount, attemptedAt, true,
            maxDepth, completed + failed, completed, failed, state.CacheHits, state.CacheMisses,
            state.Downloaded);
        logger.LogInformation(
            "Building subdivision completed for {LocationId}: status {Status}, {BuildingCount} buildings, {CompletedRegions}/{RegionCount} regions, depth {Depth}, cache {CacheHits} hits/{CacheMisses} misses",
            locationId, status, buildings.Length, completed, completed + failed, maxDepth,
            state.CacheHits, state.CacheMisses);
        return new BuildingFeatureFetchResult(document, outcome);
    }

    private async Task ResolveRegionAsync(TileRegion region, int depth, SubdivisionState state,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var cacheKey = TileKey(region.Zoom, region.X, region.Y);
        var path = TilePath(region.Zoom, region.X, region.Y);
        var cached = await TryReadRegionAsync(path, region.Bounds, cancellationToken).ConfigureAwait(false);
        if (cached is not null && (!forceRefresh || WasRecentlyRefreshed(cacheKey, DateTimeOffset.UtcNow)))
        {
            state.CacheHits++;
            state.Results.Add(new RegionWork(new BuildingCacheRegion(cacheKey, region.Bounds, region.Zoom,
                region.X, region.Y, depth, true, true), cached.Buildings, true, true, null, null));
            return;
        }
        state.CacheMisses++;
        if (state.RequestCount >= MaximumRequestsPerRefresh)
        {
            state.Results.Add(FailedRegion(region, depth, "request_limit",
                "the bounded building request limit was reached"));
            return;
        }
        try
        {
            state.RequestCount++;
            var response = await QueryRegionAsync(region.Bounds, "tile", cacheKey, depth, cancellationToken)
                .ConfigureAwait(false);
            await WriteRegionAsync(path, cacheKey, region.Bounds, response.Buildings, response.ResponseBytes,
                response.HttpStatusCode, cancellationToken).ConfigureAwait(false);
            if (forceRefresh) MarkRecentlyRefreshed(cacheKey);
            state.Downloaded++;
            state.Results.Add(new RegionWork(new BuildingCacheRegion(cacheKey, region.Bounds, region.Zoom,
                region.X, region.Y, depth, false, true), response.Buildings, false, true, null, null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (MapFeatureRequestException ex) when (ex.DensityRelated && depth < MaximumSubdivisionDepth &&
                                                     state.RequestCount < MaximumRequestsPerRefresh)
        {
            logger.LogWarning(
                "Building cache cell {CacheKey} remained dense at depth {Depth}; subdividing only that cell",
                cacheKey, depth);
            foreach (var child in Children(region))
            {
                if (state.RequestCount >= MaximumRequestsPerRefresh)
                {
                    state.Results.Add(FailedRegion(child, depth + 1, "request_limit",
                        "the bounded building request limit was reached"));
                    continue;
                }
                await ResolveRegionAsync(child, depth + 1, state, forceRefresh, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            var failure = Classify(ex);
            state.Results.Add(FailedRegion(region, depth, failure.Code, failure.Reason));
        }
    }

    private async Task<QueryResponse> QueryRegionAsync(MapGeographicBounds bounds, string queryType,
        string cacheKey, int depth, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["data"] = BuildQuery(bounds) })
        };
        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MapFeatureRequestException("timeout", "the Overpass building request timed out", ex,
                timedOut: true, densityRelated: true);
        }
        using (response)
        {
            logger.LogInformation(
                "Building query {QueryType} cache {CacheKey} depth {Depth} returned {StatusCode} in {ElapsedMs} ms from {EndpointId}",
                queryType, cacheKey, depth, (int)response.StatusCode, stopwatch.ElapsedMilliseconds,
                options.EndpointId);
            if (response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout)
                throw new MapFeatureRequestException("timeout", $"Overpass returned {(int)response.StatusCode}",
                    httpStatusCode: (int)response.StatusCode, timedOut: true, densityRelated: true);
            if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
                throw new MapFeatureRequestException("response_too_large",
                    "the building response exceeded the server size limit",
                    httpStatusCode: (int)response.StatusCode, responseTooLarge: true, densityRelated: true);
            if (!response.IsSuccessStatusCode)
                throw new MapFeatureRequestException("http_error",
                    $"Overpass returned HTTP {(int)response.StatusCode}",
                    httpStatusCode: (int)response.StatusCode);
            var bytes = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            BuildingStarFeature[] buildings;
            int malformed;
            try { buildings = Parse(bytes, out malformed); }
            catch (JsonException ex)
            {
                throw new MapFeatureRequestException("malformed_response",
                    "the building response was malformed", ex, parseFailed: true);
            }
            logger.LogInformation(
                "Building query {CacheKey} returned {ResponseBytes} bytes, {BuildingCount} centres, {MalformedCount} malformed",
                cacheKey, bytes.Length, buildings.Length, malformed);
            return new QueryResponse(buildings, bytes.Length, (int)response.StatusCode, malformed);
        }
    }

    private async Task<BuildingRegionCacheEntry?> TryReadRegionAsync(string path,
        MapGeographicBounds expectedBounds, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                16 * 1024, FileOptions.Asynchronous);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            var entry = await JsonSerializer.DeserializeAsync<BuildingRegionCacheEntry>(gzip, _json,
                cancellationToken).ConfigureAwait(false);
            if (entry is null || entry.SchemaVersion != SchemaVersion || entry.QueryVersion != QueryVersion ||
                entry.ProviderId != "openstreetmap-overpass-buildings" || !BoundsMatch(entry.Bounds, expectedBounds))
                return null;
            var expectedHash = Hash(JsonSerializer.SerializeToUtf8Bytes(entry with { ContentHash = null }, _json));
            if (!expectedHash.Equals(entry.ContentHash, StringComparison.OrdinalIgnoreCase)) return null;
            logger.LogDebug("Building cache hit at {CachePath}; key {CacheKey}; {BuildingCount} buildings",
                path, entry.CacheKey, entry.Buildings.Length);
            return entry;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Rejected invalid building cache entry at {CachePath}", path);
            return null;
        }
    }

    private async Task WriteRegionAsync(string path, string key, MapGeographicBounds bounds,
        BuildingStarFeature[] buildings, int responseBytes, int httpStatus, CancellationToken cancellationToken)
    {
        var entry = new BuildingRegionCacheEntry(SchemaVersion, QueryVersion,
            "openstreetmap-overpass-buildings", key, bounds, DateTimeOffset.UtcNow, buildings,
            responseBytes, httpStatus, null);
        entry = entry with { ContentHash = Hash(JsonSerializer.SerializeToUtf8Bytes(entry, _json)) };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             16 * 1024, FileOptions.Asynchronous))
            await using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
                await JsonSerializer.SerializeAsync(gzip, entry, _json, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, true);
            logger.LogInformation("Building cache committed at {CachePath}; key {CacheKey}; hash {ContentHash}",
                path, key, entry.ContentHash);
        }
        finally { TryDelete(temporary); }
    }

    private BuildingFeatureDocument CreateDocument(Guid locationId, WebMercatorViewport viewport,
        BuildingStarFeature[] buildings, bool subdivision, BuildingCacheRegion[] regions,
        DateTimeOffset fetchedAt) => new(SchemaVersion, locationId, viewport.CentreLatitude,
        viewport.CentreLongitude, viewport.Zoom, viewport.Width, viewport.Height, viewport.Bounds,
        new BuildingFeatureSourceMetadata("openstreetmap-overpass-buildings", "OpenStreetMap",
            "© OpenStreetMap contributors", "https://www.openstreetmap.org/copyright",
            "Open Database License (ODbL)", "https://opendatacommons.org/licenses/odbl/",
            options.EndpointId, QueryVersion, fetchedAt), buildings, subdivision, regions);

    private IEnumerable<TileRegion> InitialRegions(MapGeographicBounds bounds)
    {
        var northWest = TileFor(bounds.North, bounds.West, BuildingCacheZoom);
        var southEast = TileFor(bounds.South, bounds.East, BuildingCacheZoom);
        for (var y = northWest.Y; y <= southEast.Y; y++)
        for (var x = northWest.X; x <= southEast.X; x++)
            yield return new TileRegion(BuildingCacheZoom, x, y, TileBounds(BuildingCacheZoom, x, y));
    }

    private static IEnumerable<TileRegion> Children(TileRegion parent)
    {
        var zoom = parent.Zoom + 1;
        for (var dy = 0; dy < 2; dy++)
        for (var dx = 0; dx < 2; dx++)
        {
            var x = parent.X * 2 + dx;
            var y = parent.Y * 2 + dy;
            yield return new TileRegion(zoom, x, y, TileBounds(zoom, x, y));
        }
    }

    private static (int X, int Y) TileFor(double latitude, double longitude, int zoom)
    {
        var scale = 1 << zoom;
        var x = Math.Clamp((int)Math.Floor((longitude + 180d) / 360d * scale), 0, scale - 1);
        var radians = Math.Clamp(latitude, -WebMercatorViewport.MaximumLatitude,
            WebMercatorViewport.MaximumLatitude) * Math.PI / 180d;
        var y = Math.Clamp((int)Math.Floor((1d - Math.Asinh(Math.Tan(radians)) / Math.PI) / 2d * scale),
            0, scale - 1);
        return (x, y);
    }

    public static MapGeographicBounds TileBounds(int zoom, int x, int y)
    {
        var scale = 1 << zoom;
        static double Latitude(int tileY, int count) =>
            Math.Atan(Math.Sinh(Math.PI * (1d - 2d * tileY / count))) * 180d / Math.PI;
        var west = x / (double)scale * 360d - 180d;
        var east = (x + 1d) / scale * 360d - 180d;
        return new MapGeographicBounds(Latitude(y + 1, scale), west, Latitude(y, scale), east);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken token)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes)
            throw new MapFeatureRequestException("response_too_large",
                "the building response exceeded the configured size limit", responseTooLarge: true,
                densityRelated: true);
        await using var input = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > MaximumResponseBytes)
                throw new MapFeatureRequestException("response_too_large",
                    "the building response exceeded the configured size limit", responseTooLarge: true,
                    densityRelated: true);
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private string FullRegionPath(string key) => Path.Combine(SharedCacheDirectory, "regions", key + ".json.gz");
    private string TilePath(int zoom, int x, int y) => Path.Combine(SharedCacheDirectory, "tiles",
        zoom.ToString(CultureInfo.InvariantCulture), x.ToString(CultureInfo.InvariantCulture),
        y.ToString(CultureInfo.InvariantCulture) + ".json.gz");
    private static string TileKey(int zoom, int x, int y) => $"z{zoom}-{x}-{y}";
    private static string RegionKey(MapGeographicBounds bounds) => "region-" + Hash(Encoding.UTF8.GetBytes(
        $"openstreetmap-overpass-buildings|{QueryVersion}|{F(bounds.South)}|{F(bounds.West)}|{F(bounds.North)}|{F(bounds.East)}"));
    private static bool BoundsMatch(MapGeographicBounds a, MapGeographicBounds b) =>
        Math.Abs(a.South - b.South) < 1e-9 && Math.Abs(a.West - b.West) < 1e-9 &&
        Math.Abs(a.North - b.North) < 1e-9 && Math.Abs(a.East - b.East) < 1e-9;
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private bool WasRecentlyRefreshed(string key, DateTimeOffset now) =>
        _recentForcedRefreshes.TryGetValue(key, out var refreshedAt) &&
        refreshedAt >= now - BulkDeduplicationWindow;
    private void MarkRecentlyRefreshed(string key) => _recentForcedRefreshes[key] = DateTimeOffset.UtcNow;
    private static string F(double value) => value.ToString("0.#######", CultureInfo.InvariantCulture);
    private static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var node) &&
        node.ValueKind == JsonValueKind.String ? node.GetString() : null;
    private static string? Tag(JsonElement tags, string name) => tags.ValueKind == JsonValueKind.Object &&
        tags.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String ? node.GetString() : null;
    private static (double Latitude, double Longitude)? ReadCentre(JsonElement element)
    {
        var value = element.TryGetProperty("center", out var centre) && centre.ValueKind == JsonValueKind.Object
            ? centre : element;
        return value.TryGetProperty("lat", out var lat) && lat.TryGetDouble(out var latitude) &&
               value.TryGetProperty("lon", out var lon) && lon.TryGetDouble(out var longitude)
            ? (latitude, longitude) : null;
    }
    private static int? ParseLevels(string? value) => double.TryParse(value, NumberStyles.Float,
        CultureInfo.InvariantCulture, out var levels) && levels >= 0 ? (int?)Math.Clamp((int)Math.Round(levels), 0, 100) : null;
    private static bool IsDensityMessage(string? value) => value?.Contains("timeout", StringComparison.OrdinalIgnoreCase) == true ||
        value?.Contains("runtime", StringComparison.OrdinalIgnoreCase) == true ||
        value?.Contains("memory", StringComparison.OrdinalIgnoreCase) == true ||
        value?.Contains("too many", StringComparison.OrdinalIgnoreCase) == true;
    private static (string Code, string Reason, int? HttpStatus, bool TimedOut, bool ResponseTooLarge) Classify(Exception ex) =>
        ex is MapFeatureRequestException map
            ? (map.Code, map.SafeReason, map.HttpStatusCode, map.TimedOut, map.ResponseTooLarge)
            : ex is JsonException
                ? ("malformed_response", "the building response was malformed", null, false, false)
                : ex is HttpRequestException http
                    ? ("http_error", "the building request failed", http.StatusCode is null ? null : (int)http.StatusCode, false, false)
                    : ("request_failed", "the building request failed", null, false, false);
    private static RegionWork FailedRegion(TileRegion region, int depth, string code, string reason) => new(
        new BuildingCacheRegion(TileKey(region.Zoom, region.X, region.Y), region.Bounds, region.Zoom,
            region.X, region.Y, depth, false, false, code, reason), [], false, false, code, reason);
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    private sealed record BuildingRegionCacheEntry(int SchemaVersion, int QueryVersion, string ProviderId,
        string CacheKey, MapGeographicBounds Bounds, DateTimeOffset FetchedAtUtc,
        BuildingStarFeature[] Buildings, int ResponseBytes, int HttpStatusCode, string? ContentHash);
    private sealed record QueryResponse(BuildingStarFeature[] Buildings, int ResponseBytes,
        int HttpStatusCode, int MalformedCount);
    private sealed record TileRegion(int Zoom, int X, int Y, MapGeographicBounds Bounds);
    private sealed record RegionWork(BuildingCacheRegion Region, BuildingStarFeature[] Buildings,
        bool CacheHit, bool Complete, string? FailureCode, string? FailureReason);
    private sealed class SubdivisionState(int requestCount)
    {
        public int RequestCount { get; set; } = requestCount;
        public int CacheHits { get; set; }
        public int CacheMisses { get; set; }
        public int Downloaded { get; set; }
        public List<RegionWork> Results { get; } = [];
    }
}
