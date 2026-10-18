using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Environment;
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
    public void SemanticFeatureContracts_ContainRoadsAndWaterwaysOnly()
    {
        var documentProperties = typeof(MapFeatureDataDocument).GetProperties().Select(value => value.Name).ToArray();
        var outcomeProperties = typeof(MapFeatureFetchOutcome).GetProperties().Select(value => value.Name).ToArray();

        Assert.Contains(nameof(MapFeatureDataDocument.Roads), documentProperties);
        Assert.Contains(nameof(MapFeatureDataDocument.Waterways), documentProperties);
        Assert.DoesNotContain(documentProperties, name => name.Contains("Building", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(outcomeProperties, name => name.Contains("Building", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RoadWaterCache_RemainsReadableWithLegacyBuildingGeometryMember()
    {
        var viewport = Viewport();
        var original = Features(viewport, roads:
        [
            new MapRoadFeature(7, "way", MapRoadClassification.ARoad,
                Line(viewport, 80, 100, 820, 110), "primary", "A7", null, null, null, null)
        ]);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonNode.Parse(JsonSerializer.Serialize(original, options))!.AsObject();
        json["buildings"] = new JsonArray(new JsonObject
        {
            ["id"] = 99,
            ["elementType"] = "way",
            ["rings"] = new JsonArray()
        });

        var restored = JsonSerializer.Deserialize<MapFeatureDataDocument>(json.ToJsonString(), options);

        Assert.NotNull(restored);
        Assert.Single(restored.Roads);
        Assert.Equal("A7", restored.Roads[0].Reference);
        Assert.Equal(LocationMapThumbnailService.LegacyOverpassFeatureSchemaVersion, restored.SchemaVersion);
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
    public void SemanticRenderer_DenseWsfSettlementRemainsRestrained()
    {
        var viewport = Viewport();
        var cells = new List<(int X, int Y, float Fraction, float Height)>();
        for (var y = 25; y < viewport.Height - 25; y += 12)
        for (var x = 25; x < viewport.Width - 25; x += 12)
        {
            cells.Add((x * 2, y * 2, .72f, 9));
        }
        var processor = new SavedLocationMapImageProcessor();

        using var rendered = SKBitmap.Decode(processor.ProcessSettlement(DetailedSource(),
            Features(viewport), Settlement(viewport, cells), viewport, out var diagnostics));

        Assert.NotNull(diagnostics);
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
        Assert.True(bright / (double)opaque < .12,
            $"Dense settlement saturated {bright / (double)opaque:P1} of map pixels.");
    }

    [Fact]
    public void SettlementRenderer_SparseOffPinCentreUsesDeterministicNearestFallback()
    {
        var viewport = Viewport();
        var settlement = Settlement(viewport, [(690 * 2, 180 * 2, .8f, 8)]);
        var processor = new SavedLocationMapImageProcessor();

        using var rendered = SKBitmap.Decode(processor.ProcessSettlement(DetailedSource(), Features(viewport),
            settlement, viewport, out var diagnostics));

        Assert.NotNull(diagnostics);
        Assert.True(diagnostics.DensityCellCount > 0);
        Assert.Equal(1, diagnostics.ActiveSettlementCellCount);
        Assert.True(diagnostics.PrimaryComponentSelected);
        Assert.Equal(SavedLocationMapImageProcessor.ThumbnailWidth, rendered.Width);
        Assert.Equal(SavedLocationMapImageProcessor.ThumbnailHeight, rendered.Height);
    }

    [Fact]
    public void RoadRendering_IsUnchangedWhenSettlementLayerIsAbsent()
    {
        var viewport = Viewport();
        var featureData = Features(viewport, roads:
        [
            new MapRoadFeature(1, "way", MapRoadClassification.ARoad,
                Line(viewport, 80, 100, 820, 110), "primary", "A15", null, null, null, null)
        ]);
        var processor = new SavedLocationMapImageProcessor();

        var compatibilityOverload = processor.Process(DetailedSource(), featureData, viewport);
        var separatedLayers = processor.ProcessSettlement(DetailedSource(), featureData, null, viewport, out var diagnostics);

        Assert.Equal(compatibilityOverload, separatedLayers);
        Assert.Null(diagnostics);
    }

    private static WebMercatorViewport Viewport() => new(13, 256, 896, 504, 53.61, -0.43);

    private static MapFeatureDataDocument Features(WebMercatorViewport viewport,
        MapRoadFeature[]? roads = null, MapWaterwayFeature[]? waterways = null) => new(1, Guid.NewGuid(),
        new MapFeatureSourceMetadata("openstreetmap-overpass", "OpenStreetMap", "© OpenStreetMap contributors",
            "https://www.openstreetmap.org/copyright", "ODbL", "https://opendatacommons.org/licenses/odbl/",
            "test", 1, DateTimeOffset.UnixEpoch, viewport.Bounds),
        roads ?? [], waterways ?? []);

    private static SettlementRaster Settlement(WebMercatorViewport viewport,
        IEnumerable<(int X, int Y, float Fraction, float Height)> cells)
    {
        var width = viewport.Width * SettlementDensityBuilder.Supersampling;
        var height = viewport.Height * SettlementDensityBuilder.Supersampling;
        var fractions = new float[width * height];
        var heights = new float[fractions.Length];
        foreach (var cell in cells)
        {
            fractions[cell.Y * width + cell.X] = cell.Fraction;
            heights[cell.Y * width + cell.X] = cell.Height;
        }
        return new SettlementRaster("test-wsf", "v02",
            new GeoRasterRequest(new GeoBounds(viewport.Bounds.South, viewport.Bounds.West,
                viewport.Bounds.North, viewport.Bounds.East), width, height,
                GeoRasterProjection.WebMercator), fractions, heights);
    }

    private static MapFeatureCoordinate[] Line(WebMercatorViewport viewport, double x1, double y1, double x2, double y2)
    {
        var first = viewport.Unproject(x1, y1);
        var second = viewport.Unproject(x2, y2);
        return [new(first.Latitude, first.Longitude), new(second.Latitude, second.Longitude)];
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
}
