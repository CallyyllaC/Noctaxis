using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Noctaxis.Desktop.Services;

public sealed record OverpassMapFeatureOptions(Uri Endpoint, string EndpointId)
{
    public static OverpassMapFeatureOptions CreateDefault()
    {
        var configured = Environment.GetEnvironmentVariable("NOCTAXIS_OVERPASS_URL");
        var endpoint = Uri.TryCreate(configured, UriKind.Absolute, out var candidate) &&
                       candidate.Scheme is "https"
            ? candidate
            : new Uri("https://overpass-api.de/api/interpreter");
        var endpointId = endpoint.Host.Equals("overpass-api.de", StringComparison.OrdinalIgnoreCase)
            ? "overpass-api-de"
            : "configured-overpass";
        return new OverpassMapFeatureOptions(endpoint, endpointId);
    }
}

public sealed class OverpassMapFeatureDataService(
    HttpClient httpClient,
    OverpassMapFeatureOptions options,
    ILogger<OverpassMapFeatureDataService> logger) : IMapFeatureDataService
{
    public const int FeatureSchemaVersion = 1;
    public const int FeatureQueryVersion = 1;
    public const int MaximumResponseBytes = 8 * 1024 * 1024;
    public const int MaximumElements = 60_000;
    public const int MaximumGeometryPoints = 500_000;

    public async Task<MapFeatureFetchResult> FetchAsync(Guid locationId, WebMercatorViewport viewport,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "OSM feature query started for {LocationId}; endpoint {EndpointId}; query version {QueryVersion}",
            locationId, options.EndpointId, FeatureQueryVersion);
        logger.LogDebug("OSM feature query bounds for {LocationId}: {South},{West},{North},{East}",
            locationId, viewport.Bounds.South, viewport.Bounds.West, viewport.Bounds.North, viewport.Bounds.East);
        try
        {
            var complete = await FetchCoreAsync(locationId, viewport, attempt: 1,
                cancellationToken).ConfigureAwait(false);
            return new MapFeatureFetchResult(complete,
                MapFeatureFetchOutcome.Success(complete, MapFeatureFetchStatus.Complete));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var outcome = ClassifyFailure(ex, attemptCount: 1, fallbackAttempted: false);
            logger.LogWarning(ex,
                "OSM semantic overlay unavailable for {LocationId}; code {FailureCode}; HTTP {HttpStatusCode}",
                locationId, outcome.FailureCode, outcome.HttpStatusCode);
            return new MapFeatureFetchResult(null, outcome);
        }
    }

    public static string BuildQuery(MapGeographicBounds bounds)
    {
        var bbox = string.Join(',',
            Format(bounds.South), Format(bounds.West), Format(bounds.North), Format(bounds.East));
        return "[out:json][timeout:25];\n(\n" +
               "  way[\"highway\"][\"ref\"~\"^[ABM][0-9]\"](" + bbox + ");\n" +
               "  way[\"highway\"~\"^(motorway|trunk|primary|secondary)$\"](" + bbox + ");\n" +
               "  way[\"waterway\"~\"^(river|canal|stream)$\"](" + bbox + ");" +
               "\n);\nout tags geom;";
    }

    public static MapFeatureDataDocument Parse(Guid locationId, WebMercatorViewport viewport,
        string endpointId, byte[] json, DateTimeOffset fetchedAtUtc)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64
        });
        if (!document.RootElement.TryGetProperty("elements", out var elements) ||
            elements.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Overpass response did not contain an elements array.");
        if (elements.GetArrayLength() > MaximumElements)
            throw new MapFeatureRequestException("response_too_large",
                "the Overpass response contained too many elements", responseTooLarge: true);

        var roads = new List<MapRoadFeature>();
        var waterways = new List<MapWaterwayFeature>();
        var seen = new HashSet<(string Type, long Id)>();
        var geometryPoints = 0;
        var malformed = 0;

        foreach (var element in elements.EnumerateArray())
        {
            var type = String(element, "type") ?? string.Empty;
            if (!element.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var id) ||
                !seen.Add((type, id)))
                continue;
            var tags = element.TryGetProperty("tags", out var tagsElement) &&
                       tagsElement.ValueKind == JsonValueKind.Object
                ? tagsElement
                : default;

            if (type == "way")
            {
                var geometry = ReadGeometry(element, ref geometryPoints);
                if (TryClassifyRoad(tags, out var roadClass) && geometry.Length >= 2)
                    roads.Add(new MapRoadFeature(id, type, roadClass, geometry,
                        Tag(tags, "highway"), Tag(tags, "ref"), Tag(tags, "name"), Tag(tags, "bridge"),
                        Tag(tags, "tunnel"), Tag(tags, "layer")));
                if (TryClassifyWaterway(tags, out var waterClass) && geometry.Length >= 2)
                    waterways.Add(new MapWaterwayFeature(id, type, waterClass, geometry,
                        Tag(tags, "waterway"), Tag(tags, "name"), Tag(tags, "intermittent"),
                        Tag(tags, "tunnel"), Tag(tags, "layer")));
            }
        }

        var source = new MapFeatureSourceMetadata(
            "openstreetmap-overpass", "OpenStreetMap", "© OpenStreetMap contributors",
            "https://www.openstreetmap.org/copyright", "Open Database License (ODbL)",
            "https://opendatacommons.org/licenses/odbl/", endpointId, FeatureQueryVersion,
            fetchedAtUtc, viewport.Bounds);
        return new MapFeatureDataDocument(FeatureSchemaVersion, locationId, source,
            roads.ToArray(), waterways.ToArray(), malformed);
    }

    private async Task<MapFeatureDataDocument> FetchCoreAsync(Guid locationId, WebMercatorViewport viewport,
        int attempt, CancellationToken cancellationToken)
    {
        var query = BuildQuery(viewport.Bounds);
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["data"] = query })
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
            throw new MapFeatureRequestException("timeout", "the Overpass request timed out", ex,
                timedOut: true);
        }
        using (response)
        {
        logger.LogInformation(
            "OSM road/water query attempt {Attempt} returned {StatusCode} from {EndpointId} in {ElapsedMilliseconds} ms",
            attempt, (int)response.StatusCode,
            options.EndpointId, stopwatch.ElapsedMilliseconds);
        if (response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout)
            throw new MapFeatureRequestException("timeout", $"Overpass returned {(int)response.StatusCode}",
                httpStatusCode: (int)response.StatusCode, timedOut: true);
        if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
            throw new MapFeatureRequestException("response_too_large", "the Overpass response exceeded its size limit",
                httpStatusCode: (int)response.StatusCode, responseTooLarge: true);
        if (!response.IsSuccessStatusCode)
            throw new MapFeatureRequestException("http_error", $"Overpass returned HTTP {(int)response.StatusCode}",
                httpStatusCode: (int)response.StatusCode);
        var bytes = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
        MapFeatureDataDocument parsed;
        try
        {
            parsed = Parse(locationId, viewport, options.EndpointId, bytes, DateTimeOffset.UtcNow);
        }
        catch (JsonException ex)
        {
            throw new MapFeatureRequestException("malformed_response", "the Overpass response was malformed", ex,
                parseFailed: true);
        }
        catch (InvalidDataException ex)
        {
            throw new MapFeatureRequestException("parse_failed", "the feature response could not be parsed", ex,
                parseFailed: true);
        }
        if (parsed.FeatureCount == 0)
            throw new MapFeatureRequestException("no_usable_features", "no usable map features were returned");
        logger.LogInformation(
            "OSM features parsed for {LocationId}: {RoadCount} roads, {WaterwayCount} waterways, {IgnoredGeometryCount} malformed; {ResponseBytes} bytes",
            locationId, parsed.Roads.Length, parsed.Waterways.Length,
            parsed.IgnoredGeometryCount, bytes.Length);
        return parsed;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes)
            throw new MapFeatureRequestException("response_too_large",
                "the Overpass response exceeded the configured byte limit", responseTooLarge: true);
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > MaximumResponseBytes)
                throw new MapFeatureRequestException("response_too_large",
                    "the Overpass response exceeded the configured byte limit", responseTooLarge: true);
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    internal static bool TryClassifyRoad(JsonElement tags, out MapRoadClassification classification)
    {
        var highway = Tag(tags, "highway");
        var reference = Tag(tags, "ref")?.Trim();
        if (highway == "motorway" || StartsWithRoadLetter(reference, 'M'))
        {
            classification = MapRoadClassification.Motorway;
            return true;
        }
        if (StartsWithRoadLetter(reference, 'A'))
        {
            classification = MapRoadClassification.ARoad;
            return true;
        }
        if (StartsWithRoadLetter(reference, 'B'))
        {
            classification = MapRoadClassification.BRoad;
            return true;
        }
        if (highway is "trunk" or "primary")
        {
            classification = MapRoadClassification.ARoad;
            return true;
        }
        if (highway == "secondary")
        {
            classification = MapRoadClassification.BRoad;
            return true;
        }
        classification = default;
        return false;
    }

    internal static bool TryClassifyWaterway(JsonElement tags, out MapWaterwayClassification classification)
    {
        classification = Tag(tags, "waterway") switch
        {
            "river" => MapWaterwayClassification.River,
            "canal" => MapWaterwayClassification.Canal,
            "stream" => MapWaterwayClassification.Stream,
            _ => default
        };
        return Tag(tags, "waterway") is "river" or "canal" or "stream";
    }


    private static MapFeatureCoordinate[] ReadGeometry(JsonElement element, ref int totalPoints)
    {
        if (!element.TryGetProperty("geometry", out var geometry) || geometry.ValueKind != JsonValueKind.Array)
            return [];
        var values = new List<MapFeatureCoordinate>();
        foreach (var point in geometry.EnumerateArray())
        {
            if (!point.TryGetProperty("lat", out var latitude) ||
                !point.TryGetProperty("lon", out var longitude) ||
                !latitude.TryGetDouble(out var lat) || !longitude.TryGetDouble(out var lon))
                continue;
            if (++totalPoints > MaximumGeometryPoints)
                throw new MapFeatureRequestException("response_too_large",
                    "the Overpass response contained too many geometry points", responseTooLarge: true);
            values.Add(new MapFeatureCoordinate(lat, lon));
        }
        return values.ToArray();
    }


    private static bool StartsWithRoadLetter(string? reference, char letter) =>
        reference?.Length > 1 && char.ToUpperInvariant(reference[0]) == letter && char.IsDigit(reference[1]);
    private static string? Tag(JsonElement tags, string name) => tags.ValueKind == JsonValueKind.Object &&
        tags.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string Format(double value) => value.ToString("0.#######", CultureInfo.InvariantCulture);

    private static MapFeatureFetchOutcome ClassifyFailure(Exception exception, int attemptCount,
        bool fallbackAttempted)
    {
        if (exception is MapFeatureRequestException request)
            return MapFeatureFetchOutcome.Failure(request.Code, request.SafeReason, attemptCount,
                request.HttpStatusCode, request.TimedOut, request.ResponseTooLarge, request.ParseFailed,
                fallbackAttempted);
        if (exception is TaskCanceledException)
            return MapFeatureFetchOutcome.Failure("timeout", "the Overpass request timed out", attemptCount,
                timedOut: true, fallbackAttempted: fallbackAttempted);
        if (exception is JsonException)
            return MapFeatureFetchOutcome.Failure("malformed_response", "the Overpass response was malformed",
                attemptCount, parseFailed: true, fallbackAttempted: fallbackAttempted);
        if (exception is InvalidDataException)
            return MapFeatureFetchOutcome.Failure("parse_failed", "the feature response could not be parsed",
                attemptCount, parseFailed: true, fallbackAttempted: fallbackAttempted);
        if (exception is HttpRequestException http)
            return MapFeatureFetchOutcome.Failure("http_error", "the Overpass request failed", attemptCount,
                http.StatusCode is null ? null : (int)http.StatusCode, fallbackAttempted: fallbackAttempted);
        return MapFeatureFetchOutcome.Failure("request_failed", "the semantic feature request failed",
            attemptCount, fallbackAttempted: fallbackAttempted);
    }
}

public sealed class MapFeatureRequestException : IOException
{
    public MapFeatureRequestException(string code, string safeReason, Exception? inner = null,
        int? httpStatusCode = null, bool timedOut = false, bool responseTooLarge = false,
        bool parseFailed = false)
        : base(safeReason, inner)
    {
        Code = code;
        SafeReason = safeReason;
        HttpStatusCode = httpStatusCode;
        TimedOut = timedOut;
        ResponseTooLarge = responseTooLarge;
        ParseFailed = parseFailed;
    }

    public string Code { get; }
    public string SafeReason { get; }
    public int? HttpStatusCode { get; }
    public bool TimedOut { get; }
    public bool ResponseTooLarge { get; }
    public bool ParseFailed { get; }
}
