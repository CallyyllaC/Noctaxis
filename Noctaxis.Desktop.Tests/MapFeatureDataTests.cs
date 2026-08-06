using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Services;
using SkiaSharp;

namespace Noctaxis.Desktop.Tests;

public sealed class MapFeatureDataTests
{
    [Fact]
    public void WebMercatorViewport_KnownCentreProducesExpectedBounds()
    {
        var viewport = new WebMercatorViewport(1, 256, 256, 256, 0, 0);

        Assert.Equal(-66.5132604431, viewport.Bounds.South, 8);
        Assert.Equal(-90, viewport.Bounds.West, 8);
        Assert.Equal(66.5132604431, viewport.Bounds.North, 8);
        Assert.Equal(90, viewport.Bounds.East, 8);
    }

    [Fact]
    public void WebMercatorViewport_ProjectAndUnprojectRoundTrip()
    {
        var viewport = new WebMercatorViewport(13, 256, 896, 504, 53.61, -0.43);
        var coordinate = new GeoCoordinate(53.6042, -0.4517);

        var pixel = viewport.Project(coordinate.Latitude, coordinate.Longitude);
        var roundTrip = viewport.Unproject(pixel.X, pixel.Y);

        Assert.Equal(coordinate.Latitude, roundTrip.Latitude, 8);
        Assert.Equal(coordinate.Longitude, roundTrip.Longitude, 8);
        Assert.True(viewport.Bounds.Contains(coordinate.Latitude, coordinate.Longitude));
    }

    [Fact]
    public void OverpassQuery_UsesSouthWestNorthEastAndOnlyRequiredGeometry()
    {
        var bounds = new MapGeographicBounds(51.1, -2.2, 51.4, -1.8);

        var query = OverpassMapFeatureDataService.BuildQuery(bounds, includeBuildings: false);

        Assert.Contains("(51.1,-2.2,51.4,-1.8)", query);
        Assert.Contains("[out:json][timeout:25]", query);
        Assert.Contains("[\"highway\"]", query);
        Assert.Contains("[\"waterway\"", query);
        Assert.DoesNotContain("building", query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("out tags geom", query);
        Assert.DoesNotContain("residential", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("key=", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OverpassParser_ClassifiesAndDeduplicatesSupportedFeatures()
    {
        var viewport = Viewport();
        var json = """
        {"elements":[
          {"type":"way","id":1,"tags":{"highway":"primary","ref":"A15","name":"A road"},"geometry":[{"lat":53.60,"lon":-0.45},{"lat":53.62,"lon":-0.41}]},
          {"type":"way","id":1,"tags":{"highway":"primary","ref":"A15"},"geometry":[{"lat":53.60,"lon":-0.45},{"lat":53.62,"lon":-0.41}]},
          {"type":"way","id":2,"tags":{"highway":"secondary","ref":"B1206"},"geometry":[{"lat":53.60,"lon":-0.45},{"lat":53.62,"lon":-0.41}]},
          {"type":"way","id":3,"tags":{"highway":"motorway","ref":"M1"},"geometry":[{"lat":53.60,"lon":-0.45},{"lat":53.62,"lon":-0.41}]},
          {"type":"way","id":4,"tags":{"highway":"primary"},"geometry":[{"lat":53.60,"lon":-0.45},{"lat":53.62,"lon":-0.41}]},
          {"type":"way","id":5,"tags":{"highway":"residential"},"geometry":[{"lat":53.60,"lon":-0.45},{"lat":53.62,"lon":-0.41}]},
          {"type":"way","id":10,"tags":{"waterway":"river"},"geometry":[{"lat":53.60,"lon":-0.45},{"lat":53.62,"lon":-0.41}]},
          {"type":"way","id":11,"tags":{"waterway":"canal"},"geometry":[{"lat":53.60,"lon":-0.45},{"lat":53.62,"lon":-0.41}]},
          {"type":"way","id":12,"tags":{"waterway":"stream"},"geometry":[{"lat":53.60,"lon":-0.45},{"lat":53.62,"lon":-0.41}]},
          {"type":"way","id":20,"tags":{"building":"yes"},"geometry":[{"lat":53.605,"lon":-0.435},{"lat":53.605,"lon":-0.434},{"lat":53.606,"lon":-0.434},{"lat":53.606,"lon":-0.435},{"lat":53.605,"lon":-0.435}]},
          {"type":"way","id":21,"tags":{"building":"yes"},"geometry":[{"lat":53.605,"lon":-0.435},{"lat":53.606,"lon":-0.434},{"lat":53.607,"lon":-0.433}]}
        ]}
        """;

        var data = OverpassMapFeatureDataService.Parse(Guid.NewGuid(), viewport, "test-endpoint",
            Encoding.UTF8.GetBytes(json), DateTimeOffset.UnixEpoch);

        Assert.Equal(4, data.Roads.Length);
        Assert.Contains(data.Roads, road => road.Reference == "A15" && road.Classification == MapRoadClassification.ARoad);
        Assert.Contains(data.Roads, road => road.Reference == "B1206" && road.Classification == MapRoadClassification.BRoad);
        Assert.Contains(data.Roads, road => road.Classification == MapRoadClassification.Motorway);
        Assert.Contains(data.Roads, road => road.Reference is null && road.Classification == MapRoadClassification.ARoad);
        Assert.DoesNotContain(data.Roads, road => road.Highway == "residential");
        Assert.Equal([MapWaterwayClassification.River, MapWaterwayClassification.Canal, MapWaterwayClassification.Stream],
            data.Waterways.Select(waterway => waterway.Classification));
        Assert.Empty(data.Buildings);
    }

    [Fact]
    public async Task OverpassClient_PostsBoundedQueryAndMapsResponse()
    {
        var handler = new SequenceHandler(_ => JsonResponse(UsableFeatureJson));
        using var client = new HttpClient(handler);
        var service = new OverpassMapFeatureDataService(client,
            new OverpassMapFeatureOptions(new Uri("https://overpass.test/api/interpreter"), "test-overpass"),
            NullLogger<OverpassMapFeatureDataService>.Instance);

        var result = await service.FetchAsync(Guid.NewGuid(), Viewport(), CancellationToken.None);

        Assert.Equal(MapFeatureFetchStatus.Complete, result.Status);
        Assert.NotNull(result.Data);
        Assert.Equal(1, result.Outcome.RoadCount);
        Assert.Equal(1, result.Outcome.AttemptCount);
        Assert.Equal(1, handler.RequestCount);
        Assert.Contains("data=", handler.Bodies[0]);
        Assert.DoesNotContain("token", handler.Bodies[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpMethod.Post, handler.Methods[0]);
    }

    [Fact]
    public async Task CoreOverpassClient_DoesNotRetryAnOversizedRoadAndWaterResponse()
    {
        var handler = new SequenceHandler(
            _ => OversizedResponse(),
            _ => JsonResponse(UsableFeatureJson));
        using var client = new HttpClient(handler);
        var service = new OverpassMapFeatureDataService(client,
            new OverpassMapFeatureOptions(new Uri("https://overpass.test/api/interpreter"), "test-overpass"),
            NullLogger<OverpassMapFeatureDataService>.Instance);

        var result = await service.FetchAsync(Guid.NewGuid(), Viewport(), CancellationToken.None);

        Assert.Equal(MapFeatureFetchStatus.Unavailable, result.Status);
        Assert.True(result.Outcome.ResponseTooLarge);
        Assert.False(result.Outcome.FallbackAttempted);
        Assert.Equal(1, result.Outcome.AttemptCount);
        Assert.Equal(1, handler.RequestCount);
        Assert.DoesNotContain("building", Uri.UnescapeDataString(handler.Bodies[0]));
    }

    [Fact]
    public async Task OverpassClient_HttpFailureRecordsStatusAndDoesNotFallback()
    {
        var handler = new SequenceHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        using var client = new HttpClient(handler);
        var service = Service(client);

        var result = await service.FetchAsync(Guid.NewGuid(), Viewport(), CancellationToken.None);

        Assert.Equal(MapFeatureFetchStatus.Unavailable, result.Status);
        Assert.Equal(502, result.Outcome.HttpStatusCode);
        Assert.Equal("http_error", result.Outcome.FailureCode);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task OverpassClient_MalformedResponseRecordsParseFailureWithoutFallback()
    {
        var handler = new SequenceHandler(_ => JsonResponse("not-json"));
        using var client = new HttpClient(handler);
        var service = Service(client);

        var result = await service.FetchAsync(Guid.NewGuid(), Viewport(), CancellationToken.None);

        Assert.Equal(MapFeatureFetchStatus.Unavailable, result.Status);
        Assert.True(result.Outcome.ParseFailed);
        Assert.Equal("malformed_response", result.Outcome.FailureCode);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task CoreOverpassClient_TimeoutDoesNotRetryBecauseBuildingsAreSeparate()
    {
        var handler = new SequenceHandler(
            _ => throw new TaskCanceledException("timeout"),
            _ => JsonResponse(UsableFeatureJson),
            _ => throw new InvalidOperationException("A third attempt is forbidden."));
        using var client = new HttpClient(handler);
        var service = Service(client);

        var result = await service.FetchAsync(Guid.NewGuid(), Viewport(), CancellationToken.None);

        Assert.Equal(MapFeatureFetchStatus.Unavailable, result.Status);
        Assert.True(result.Outcome.TimedOut);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task CoreOverpassClient_GatewayTimeoutRetainsHttpDiagnosticWithoutBuildingFallback()
    {
        var handler = new SequenceHandler(
            _ => new HttpResponseMessage(HttpStatusCode.GatewayTimeout),
            _ => JsonResponse(UsableFeatureJson));
        using var client = new HttpClient(handler);

        var result = await Service(client).FetchAsync(Guid.NewGuid(), Viewport(), CancellationToken.None);

        Assert.Equal(MapFeatureFetchStatus.Unavailable, result.Status);
        Assert.Equal(504, result.Outcome.HttpStatusCode);
        Assert.Equal("timeout", result.Outcome.FailureCode);
        Assert.True(result.Outcome.TimedOut);
        Assert.False(result.Outcome.FallbackAttempted);
    }

    [Fact]
    public void SemanticRenderer_UsesFuchsiaRoadsCyanWaterAndDrawsPinLast()
    {
        var viewport = Viewport();
        var source = DetailedSource();
        var aRoad = Line(viewport, 90, 90, 820, 105);
        var bRoad = Line(viewport, 90, 150, 820, 165);
        var river = Line(viewport, 90, 220, 820, 230);
        var data = Features(viewport,
            roads:
            [
                new(1, "way", MapRoadClassification.ARoad, aRoad, "primary", "A15", null, null, null, null),
                new(2, "way", MapRoadClassification.BRoad, bRoad, "secondary", "B1", null, null, null, null)
            ],
            waterways: [new(3, "way", MapWaterwayClassification.River, river, "river", null, null, null, null)]);
        var processor = new SavedLocationMapImageProcessor();

        using var rendered = SKBitmap.Decode(processor.Process(source, data, viewport));

        Assert.True(CountPixels(rendered, IsFuchsia, excludePin: true) > 35);
        Assert.True(CountPixels(rendered, IsCyan, excludePin: true) > 25);
        var pin = rendered.GetPixel(rendered.Width / 2, rendered.Height / 2);
        Assert.True(pin.Blue > 200 && pin.Red > 130, $"Unexpected pin colour {pin}.");
    }

    [Fact]
    public void SemanticRenderer_ARoadIsMoreProminentThanBRoadAndBaseHasNoGenericFuchsiaEdges()
    {
        var viewport = Viewport();
        var source = DetailedSource();
        var geometry = Line(viewport, 70, 120, 825, 120);
        var processor = new SavedLocationMapImageProcessor();
        using var baseMap = SKBitmap.Decode(processor.Process(source));
        using var aRoad = SKBitmap.Decode(processor.Process(source,
            Features(viewport, roads: [new(1, "way", MapRoadClassification.ARoad, geometry, "primary", "A1", null, null, null, null)]), viewport));
        using var bRoad = SKBitmap.Decode(processor.Process(source,
            Features(viewport, roads: [new(2, "way", MapRoadClassification.BRoad, geometry, "secondary", "B1", null, null, null, null)]), viewport));

        var baseCount = CountPixels(baseMap, IsFuchsia, excludePin: true);
        var aCount = CountPixels(aRoad, IsFuchsia, excludePin: true);
        var bCount = CountPixels(bRoad, IsFuchsia, excludePin: true);
        Assert.True(aCount > bCount, $"A-road pixels {aCount}; B-road pixels {bCount}.");
        Assert.True(baseCount < bCount, $"Base fuchsia pixels {baseCount}; B-road pixels {bCount}.");
    }

    [Fact]
    public void SemanticRenderer_ClipsCrossingGeometryAndKeepsItAlignedThroughPerspectiveTransform()
    {
        var viewport = Viewport();
        var crossing = Line(viewport, -120, 82, viewport.Width + 120, 82);
        var processor = new SavedLocationMapImageProcessor();

        using var rendered = SKBitmap.Decode(processor.Process(DetailedSource(),
            Features(viewport, roads:
                [new(7, "way", MapRoadClassification.ARoad, crossing, "primary", "A7", null, null, null, null)]),
            viewport));

        var upperRightFuchsia = 0;
        for (var y = 15; y < rendered.Height / 2; y++)
        for (var x = rendered.Width / 2; x < rendered.Width; x++)
            if (IsFuchsia(rendered.GetPixel(x, y))) upperRightFuchsia++;
        Assert.True(upperRightFuchsia > 20,
            "The clipped source-space road did not remain aligned after crop, scale, and skew.");
    }

    [Fact]
    public void SemanticRenderer_DenseBuildingsRemainRestrained()
    {
        var viewport = Viewport();
        var buildings = new List<MapBuildingFeature>();
        var id = 1L;
        for (var y = 25; y < viewport.Height - 25; y += 12)
        for (var x = 25; x < viewport.Width - 25; x += 12)
        {
            var ring = Ring(viewport, x, y, 6, 5);
            buildings.Add(new MapBuildingFeature(id++, "way", [new MapFeatureRing(ring)], "yes", null,
                ring[0] with
                {
                    Latitude = ring.Take(4).Average(point => point.Latitude),
                    Longitude = ring.Take(4).Average(point => point.Longitude)
                }));
        }
        var processor = new SavedLocationMapImageProcessor();

        var centres = buildings.Select(building => new BuildingStarFeature(
            building.ElementType, building.Id, building.Centroid.Latitude,
            building.Centroid.Longitude, building.Building, null, building.Name)).ToArray();
        var buildingDocument = Buildings(viewport, centres);
        using var rendered = SKBitmap.Decode(processor.Process(DetailedSource(),
            Features(viewport), buildingDocument, viewport, out var diagnostics));

        Assert.True(diagnostics.DensityCellCount > 100);

        var bright = 0;
        var opaque = 0;
        for (var y = 0; y < rendered.Height; y += 2)
        for (var x = rendered.Width / 2; x < rendered.Width; x += 2)
        {
            var pixel = rendered.GetPixel(x, y);
            if (pixel.Alpha < 200) continue;
            opaque++;
            if (.2126 * pixel.Red + .7152 * pixel.Green + .0722 * pixel.Blue > 185) bright++;
        }
        Assert.True(bright / (double)opaque < .12, $"Dense buildings saturated {bright / (double)opaque:P1} of map pixels.");
    }

    [Fact]
    public void BuildingRenderer_IsolatedCentresProduceAlignedHabitationPoints()
    {
        var viewport = Viewport();
        var coordinate = viewport.Unproject(690, 180);
        var buildings = Buildings(viewport,
        [
            new BuildingStarFeature("way", 1, coordinate.Latitude, coordinate.Longitude,
                "house", 2, null)
        ]);
        var processor = new SavedLocationMapImageProcessor();

        using var baseMap = SKBitmap.Decode(processor.Process(DetailedSource(), Features(viewport),
            null, viewport, out _));
        using var rendered = SKBitmap.Decode(processor.Process(DetailedSource(), Features(viewport),
            buildings, viewport, out var diagnostics));

        Assert.Equal(1, diagnostics.DensityCellCount);
        var difference = 0;
        for (var y = 60; y < 160; y++)
        for (var x = 350; x < 480; x++)
        {
            var before = baseMap.GetPixel(x, y);
            var after = rendered.GetPixel(x, y);
            if (after.Red + after.Green + after.Blue > before.Red + before.Green + before.Blue + 15)
                difference++;
        }
        Assert.True(difference > 2, "The isolated building did not produce a visible aligned point.");
    }

    [Fact]
    public void RoadRendering_IsUnchangedWhenBuildingLayerIsAbsent()
    {
        var viewport = Viewport();
        var featureData = Features(viewport, roads:
        [
            new MapRoadFeature(1, "way", MapRoadClassification.ARoad,
                Line(viewport, 80, 100, 820, 110), "primary", "A15", null, null, null, null)
        ]);
        var processor = new SavedLocationMapImageProcessor();

        var compatibilityOverload = processor.Process(DetailedSource(), featureData, viewport);
        var separatedLayers = processor.Process(DetailedSource(), featureData, null, viewport, out var diagnostics);

        Assert.Equal(compatibilityOverload, separatedLayers);
        Assert.Equal(0, diagnostics.DensityCellCount);
    }

    private static WebMercatorViewport Viewport() => new(13, 256, 896, 504, 53.61, -0.43);

    private static MapFeatureDataDocument Features(WebMercatorViewport viewport,
        MapRoadFeature[]? roads = null, MapWaterwayFeature[]? waterways = null,
        MapBuildingFeature[]? buildings = null) => new(1, Guid.NewGuid(),
        new MapFeatureSourceMetadata("openstreetmap-overpass", "OpenStreetMap", "© OpenStreetMap contributors",
            "https://www.openstreetmap.org/copyright", "ODbL", "https://opendatacommons.org/licenses/odbl/",
            "test", 1, DateTimeOffset.UnixEpoch, viewport.Bounds),
        roads ?? [], waterways ?? [], buildings ?? []);

    private static BuildingFeatureDocument Buildings(WebMercatorViewport viewport,
        BuildingStarFeature[] buildings) => new(
        OverpassBuildingFeatureDataService.SchemaVersion,
        Guid.NewGuid(),
        viewport.CentreLatitude, viewport.CentreLongitude, viewport.Zoom, viewport.Width,
        viewport.Height, viewport.Bounds,
        new BuildingFeatureSourceMetadata(
            "openstreetmap-overpass-buildings", "OpenStreetMap", "© OpenStreetMap contributors",
            "https://www.openstreetmap.org/copyright", "Open Database License (ODbL)",
            "https://opendatacommons.org/licenses/odbl/", "test",
            OverpassBuildingFeatureDataService.QueryVersion, DateTimeOffset.UnixEpoch),
        buildings, false, []);

    private static MapFeatureCoordinate[] Line(WebMercatorViewport viewport, double x1, double y1, double x2, double y2)
    {
        var first = viewport.Unproject(x1, y1);
        var second = viewport.Unproject(x2, y2);
        return [new(first.Latitude, first.Longitude), new(second.Latitude, second.Longitude)];
    }

    private static MapFeatureCoordinate[] Ring(WebMercatorViewport viewport, double x, double y, double width, double height)
    {
        var pixels = new[] { (x, y), (x + width, y), (x + width, y + height), (x, y + height), (x, y) };
        return pixels.Select(pixel => viewport.Unproject(pixel.Item1, pixel.Item2))
            .Select(point => new MapFeatureCoordinate(point.Latitude, point.Longitude)).ToArray();
    }

    private static int CountPixels(SKBitmap bitmap, Func<SKColor, bool> predicate, bool excludePin)
    {
        var count = 0;
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            if (excludePin && Math.Abs(x - bitmap.Width / 2) < 34 && Math.Abs(y - bitmap.Height / 2) < 42)
                continue;
            if (predicate(bitmap.GetPixel(x, y))) count++;
        }
        return count;
    }

    private static bool IsFuchsia(SKColor colour) => colour.Alpha > 45 && colour.Red > 90 &&
        colour.Red > colour.Green * 1.3 && colour.Blue > colour.Green * 1.25;
    private static bool IsCyan(SKColor colour) => colour.Alpha > 45 && colour.Blue > 90 &&
        colour.Blue > colour.Red * 1.25 && colour.Green > colour.Red * 1.25;

    private static byte[] DetailedSource()
    {
        using var bitmap = new SKBitmap(896, 504);
        using var canvas = new SKCanvas(bitmap);
        using (var terrain = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(bitmap.Width, bitmap.Height),
                [SKColor.Parse("#C9D3BC"), SKColor.Parse("#E2D5BD"), SKColor.Parse("#B8C9B5")],
                [0, .52f, 1], SKShaderTileMode.Clamp)
        })
            canvas.DrawRect(SKRect.Create(bitmap.Width, bitmap.Height), terrain);
        using var grid = new SKPaint { Color = SKColor.Parse("#889887"), StrokeWidth = 2, IsAntialias = true };
        for (var x = 0; x < bitmap.Width; x += 28) canvas.DrawLine(x, 0, x + 180, bitmap.Height, grid);
        for (var y = 0; y < bitmap.Height; y += 32) canvas.DrawLine(0, y, bitmap.Width, y + 110, grid);
        for (var index = 0; index < 24; index++)
        {
            using var detail = new SKPaint
            {
                Color = new SKColor((byte)(70 + index * 6), (byte)(95 + index * 4),
                    (byte)(80 + index * 5)),
                IsAntialias = true
            };
            canvas.DrawCircle(18 + index * 35, 30 + index % 4 * 36, 8, detail);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage OversizedResponse()
    {
        var response = JsonResponse("{\"elements\":[]}");
        response.Content.Headers.ContentLength = OverpassMapFeatureDataService.MaximumResponseBytes + 1;
        return response;
    }

    private static OverpassMapFeatureDataService Service(HttpClient client) => new(client,
        new OverpassMapFeatureOptions(new Uri("https://overpass.test/api/interpreter"), "test-overpass"),
        NullLogger<OverpassMapFeatureDataService>.Instance);

    private const string UsableFeatureJson =
        "{\"elements\":[{\"type\":\"way\",\"id\":1,\"tags\":{\"highway\":\"primary\",\"ref\":\"A15\"}," +
        "\"geometry\":[{\"lat\":53.60,\"lon\":-0.45},{\"lat\":53.62,\"lon\":-0.41}]}]}";

    private sealed class SequenceHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public List<string> Bodies { get; } = [];
        public List<HttpMethod> Methods { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Methods.Add(request.Method);
            Bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            var index = RequestCount++;
            return responses[Math.Min(index, responses.Length - 1)](request);
        }
    }
}
