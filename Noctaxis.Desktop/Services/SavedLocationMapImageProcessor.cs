using SkiaSharp;

namespace Noctaxis.Desktop.Services;

public sealed record MapImageValidationResult(
    bool IsValid,
    string Reason,
    double LuminanceStandardDeviation,
    double LuminanceRange,
    double TransparentPixelRatio,
    int QuantisedColourCount,
    double MeanEdgeDifference);

public sealed record BuildingRenderDiagnostics(int DensityCellCount, double MaximumDensityBeforeClamping);

/// <summary>Provider-neutral validation and Noctaxis card-art processing.</summary>
public sealed class SavedLocationMapImageProcessor
{
    public const int ThumbnailWidth = 512;
    public const int ThumbnailHeight = 280;
    public const int StyleVersion = 8;
    public const int FeatureOverlayStyleVersion = 1;
    public const int BuildingOverlayStyleVersion = 1;

    public MapImageValidationResult ValidateSource(byte[] png)
    {
        using var bitmap = SKBitmap.Decode(png);
        if (bitmap is null) return Invalid("Image data is not a readable PNG.");
        if (bitmap.Width < 640 || bitmap.Height < 360)
            return Invalid($"Source dimensions {bitmap.Width}x{bitmap.Height} are smaller than expected.");
        return Analyse(bitmap, maximumTransparentRatio: .02, minimumDeviation: 4.0,
            minimumRange: 22, minimumColours: 18, minimumEdgeDifference: 1.6);
    }

    public MapImageValidationResult ValidateThumbnail(byte[] png)
    {
        using var bitmap = SKBitmap.Decode(png);
        if (bitmap is null) return Invalid("Thumbnail data is not a readable PNG.");
        if (bitmap.Width != ThumbnailWidth || bitmap.Height != ThumbnailHeight)
            return Invalid($"Thumbnail dimensions {bitmap.Width}x{bitmap.Height} are unexpected.");
        // The base raster intentionally recedes before the longer-lived semantic layers. The
        // combined editorial masks leave roughly three fifths of the canvas partially transparent;
        // validation of useful artwork is handled separately against the fully visible right side.
        return Analyse(bitmap, maximumTransparentRatio: .62, minimumDeviation: 2.0,
            minimumRange: 12, minimumColours: 10, minimumEdgeDifference: .40);
    }

    public byte[] Process(byte[] sourcePng) => Process(sourcePng, null, null, null, out _);

    public byte[] Process(byte[] sourcePng, MapFeatureDataDocument? features, WebMercatorViewport? viewport)
        => Process(sourcePng, features, null, viewport, out _);

    public byte[] Process(byte[] sourcePng, MapFeatureDataDocument? features,
        BuildingFeatureDocument? buildings, WebMercatorViewport? viewport,
        out BuildingRenderDiagnostics buildingDiagnostics)
    {
        buildingDiagnostics = new BuildingRenderDiagnostics(0, 0);
        var validation = ValidateSource(sourcePng);
        if (!validation.IsValid) throw new InvalidDataException("Source map validation failed: " + validation.Reason);
        if (features is not null && viewport is null)
            throw new ArgumentNullException(nameof(viewport), "A viewport is required to align semantic map features.");
        using var source = SKBitmap.Decode(sourcePng)!;
        using var scaled = ScaleAndCrop(source);
        using var semanticOverlay = features is null
            ? null
            : RenderFeatureOverlay(source.Width, source.Height, features, viewport!);
        using var scaledOverlay = semanticOverlay is null ? null : ScaleAndCrop(semanticOverlay);
        using var buildingOverlay = buildings is null
            ? null
            : RenderBuildingOverlay(source.Width, source.Height, buildings, viewport!, out buildingDiagnostics);
        using var scaledBuildings = buildingOverlay is null ? null : ScaleAndCrop(buildingOverlay);
        using var output = new SKBitmap(ThumbnailWidth, ThumbnailHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(output);
        canvas.Clear(SKColors.Transparent);

        const float skew = -0.025f;
        canvas.Save();
        canvas.Translate(ThumbnailWidth / 2f, ThumbnailHeight / 2f);
        canvas.Skew(skew, 0);
        canvas.Translate(-ThumbnailWidth / 2f, -ThumbnailHeight / 2f);
        using var scaledImage = SKImage.FromBitmap(scaled);
        using (var grade = new SKPaint
        {
            IsAntialias = true,
            ColorFilter = SKColorFilter.CreateColorMatrix(
            [
                .066f, .222f, .022f, 0, 6f / 255f,
                .085f, .286f, .029f, 0, 8f / 255f,
                .117f, .393f, .040f, 0, 12f / 255f,
                0, 0, 0, 1, 0
            ])
        })
            canvas.DrawImage(scaledImage,
                new SKRect(-6, -2, ThumbnailWidth + 6, ThumbnailHeight + 2),
                new SKSamplingOptions(SKCubicResampler.Mitchell), grade);
        canvas.Restore();

        // The raster recedes farther beneath the editorial content. Semantic lines and stars are
        // drawn after this mask and therefore retain the approved, longer-lived fade reach.
        using (var baseFade = new SKPaint
        {
            BlendMode = SKBlendMode.DstIn,
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0),
                new SKPoint(ThumbnailWidth * .62f, 0),
                [new SKColor(255, 255, 255, 0), new SKColor(255, 255, 255, 12),
                    new SKColor(255, 255, 255, 132), SKColors.White],
                [0f, .28f, .74f, 1f], SKShaderTileMode.Clamp)
        })
            canvas.DrawRect(SKRect.Create(ThumbnailWidth, ThumbnailHeight), baseFade);

        canvas.Save();
        canvas.Translate(ThumbnailWidth / 2f, ThumbnailHeight / 2f);
        canvas.Skew(skew, 0);
        canvas.Translate(-ThumbnailWidth / 2f, -ThumbnailHeight / 2f);
        if (scaledOverlay is not null)
        {
            using var overlayImage = SKImage.FromBitmap(scaledOverlay);
            canvas.DrawImage(overlayImage,
                new SKRect(-6, -2, ThumbnailWidth + 6, ThumbnailHeight + 2),
                new SKSamplingOptions(SKCubicResampler.Mitchell));
        }
        if (scaledBuildings is not null)
        {
            using var buildingImage = SKImage.FromBitmap(scaledBuildings);
            canvas.DrawImage(buildingImage,
                new SKRect(-6, -2, ThumbnailWidth + 6, ThumbnailHeight + 2),
                new SKSamplingOptions(SKCubicResampler.Mitchell));
        }
        canvas.Restore();

        using (var atmosphere = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, ThumbnailHeight),
                [SKColor.Parse("#060C1628"), SKColor.Parse("#10050B16")],
                [0, 1], SKShaderTileMode.Clamp)
        })
            canvas.DrawRect(SKRect.Create(ThumbnailWidth, ThumbnailHeight), atmosphere);
        using (var vignette = new SKPaint
        {
            Shader = SKShader.CreateRadialGradient(
                new SKPoint(ThumbnailWidth * .62f, ThumbnailHeight * .50f), ThumbnailWidth * .82f,
                [SKColor.Parse("#00111A2B"), SKColor.Parse("#26050A12")],
                [0f, 1f], SKShaderTileMode.Clamp)
        })
            canvas.DrawRect(SKRect.Create(ThumbnailWidth, ThumbnailHeight), vignette);
        using (var leftShade = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(ThumbnailWidth * .58f, 0),
                [SKColor.Parse("#52050A13"), SKColor.Parse("#26050A13"), SKColors.Transparent],
                [0f, .48f, 1f], SKShaderTileMode.Clamp)
        })
            canvas.DrawRect(SKRect.Create(ThumbnailWidth, ThumbnailHeight), leftShade);
        using (var fade = new SKPaint
        {
            BlendMode = SKBlendMode.DstIn,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(ThumbnailWidth * .52f, 0),
                [new SKColor(255, 255, 255, 0), new SKColor(255, 255, 255, 18),
                    new SKColor(255, 255, 255, 150), SKColors.White],
                [0f, .24f, .72f, 1f], SKShaderTileMode.Clamp)
        })
            canvas.DrawRect(SKRect.Create(ThumbnailWidth, ThumbnailHeight), fade);

        EnsureMapArtworkIsVisible(output);

        // The source location is centred within the map artwork. In the 42/58 card split this
        // places the final pin at roughly 71% of the complete card width.
        DrawPin(canvas, ThumbnailWidth / 2f, ThumbnailHeight / 2f);
        using var image = SKImage.FromBitmap(output);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 90);
        var bytes = encoded.ToArray();
        var processedValidation = ValidateThumbnail(bytes);
        if (!processedValidation.IsValid)
            throw new InvalidDataException("Processed thumbnail validation failed: " + processedValidation.Reason);
        return bytes;
    }

    private static SKBitmap ScaleAndCrop(SKBitmap source)
    {
        var targetAspect = ThumbnailWidth / (float)ThumbnailHeight;
        var sourceAspect = source.Width / (float)source.Height;
        SKRect sourceRect;
        if (sourceAspect > targetAspect)
        {
            var cropWidth = source.Height * targetAspect;
            var left = (source.Width - cropWidth) / 2f;
            sourceRect = new SKRect(left, 0, left + cropWidth, source.Height);
        }
        else
        {
            var cropHeight = source.Width / targetAspect;
            var top = (source.Height - cropHeight) / 2f;
            sourceRect = new SKRect(0, top, source.Width, top + cropHeight);
        }

        var scaled = new SKBitmap(ThumbnailWidth, ThumbnailHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(scaled);
        canvas.Clear(SKColors.Transparent);
        using var sourceImage = SKImage.FromBitmap(source);
        canvas.DrawImage(sourceImage, sourceRect, SKRect.Create(ThumbnailWidth, ThumbnailHeight),
            new SKSamplingOptions(SKCubicResampler.Mitchell));
        return scaled;
    }

    private static SKBitmap RenderFeatureOverlay(int width, int height, MapFeatureDataDocument features,
        WebMercatorViewport viewport)
    {
        var overlay = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        overlay.Erase(SKColors.Transparent);
        using var canvas = new SKCanvas(overlay);
        canvas.ClipRect(SKRect.Create(width, height));

        foreach (var waterway in features.Waterways)
            DrawWaterway(canvas, waterway, viewport);
        foreach (var road in features.Roads.OrderByDescending(road => road.Classification))
            DrawRoad(canvas, road, viewport);
        return overlay;
    }

    private static void DrawRoad(SKCanvas canvas, MapRoadFeature road, WebMercatorViewport viewport)
    {
        using var path = CreatePath(road.Geometry, viewport, close: false);
        if (path is null) return;
        var (width, alpha) = road.Classification switch
        {
            MapRoadClassification.Motorway => (6.2f, (byte)166),
            MapRoadClassification.ARoad => (5.0f, (byte)148),
            MapRoadClassification.BRoad => (3.0f, (byte)82),
            _ => (3.8f, (byte)105)
        };
        if (road.Classification is MapRoadClassification.Motorway or MapRoadClassification.ARoad)
        {
            using var bloom = LinePaint(new SKColor(217, 75, 184, 24), width + 3.2f);
            canvas.DrawPath(path, bloom);
        }
        using var paint = LinePaint(new SKColor(223, 85, 196, alpha), width);
        canvas.DrawPath(path, paint);
    }

    private static void DrawWaterway(SKCanvas canvas, MapWaterwayFeature waterway,
        WebMercatorViewport viewport)
    {
        using var path = CreatePath(waterway.Geometry, viewport, close: false);
        if (path is null) return;
        var (width, alpha) = waterway.Classification switch
        {
            MapWaterwayClassification.River => (4.6f, (byte)142),
            MapWaterwayClassification.Canal => (3.5f, (byte)112),
            _ => (2.0f, (byte)68)
        };
        using var paint = LinePaint(new SKColor(70, 203, 232, alpha), width);
        canvas.DrawPath(path, paint);
    }

    private static SKBitmap RenderBuildingOverlay(int width, int height,
        BuildingFeatureDocument buildings, WebMercatorViewport viewport,
        out BuildingRenderDiagnostics diagnostics)
    {
        const int cellSize = 4;
        var columns = (width + cellSize - 1) / cellSize;
        var rows = (height + cellSize - 1) / cellSize;
        var density = new float[columns * rows];
        foreach (var building in buildings.Buildings)
        {
            var centre = viewport.Project(building.Latitude, building.Longitude);
            if (!viewport.ContainsPixel(centre)) continue;
            var column = Math.Clamp((int)(centre.X / cellSize), 0, columns - 1);
            var row = Math.Clamp((int)(centre.Y / cellSize), 0, rows - 1);
            density[row * columns + column] += BuildingWeight(building);
        }
        var maxDensity = density.Length == 0 ? 0 : density.Max();
        var nonEmpty = density.Count(value => value > 0);
        diagnostics = new BuildingRenderDiagnostics(nonEmpty, maxDensity);

        var overlay = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        overlay.Erase(SKColors.Transparent);
        using var canvas = new SKCanvas(overlay);
        using var glow = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.SrcOver };
        using var point = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.SrcOver };
        for (var row = 0; row < rows; row++)
        for (var column = 0; column < columns; column++)
        {
            var value = density[row * columns + column];
            if (value <= 0) continue;
            var intensity = (float)Math.Clamp(Math.Log2(1 + value) / 4.2, .16, 1);
            var x = column * cellSize + cellSize / 2f;
            var y = row * cellSize + cellSize / 2f;
            glow.Color = new SKColor(190, 180, 255, (byte)Math.Round(38 + 74 * intensity));
            point.Color = new SKColor(239, 235, 255, (byte)Math.Round(82 + 116 * intensity));
            canvas.DrawCircle(x, y, 2.0f + 2.4f * intensity, glow);
            canvas.DrawCircle(x, y, .65f + .85f * intensity, point);
        }
        return overlay;
    }

    private static float BuildingWeight(BuildingStarFeature building)
    {
        var type = building.Building?.ToLowerInvariant();
        var typeWeight = type switch
        {
            "industrial" or "warehouse" or "commercial" or "retail" or "hospital" or
                "school" or "civic" or "office" => 1.65f,
            "shed" or "garage" or "garages" or "carport" or "hut" or "greenhouse" => .20f,
            _ => 1f
        };
        var levels = Math.Clamp(building.Levels ?? 1, 1, 12);
        return typeWeight * (1f + MathF.Log2(levels) * .16f);
    }

    private static SKPath? CreatePath(MapFeatureCoordinate[] geometry, WebMercatorViewport viewport, bool close)
    {
        if (geometry.Length < (close ? 4 : 2)) return null;
        var path = new SKPath();
        for (var index = 0; index < geometry.Length; index++)
        {
            var projected = viewport.Project(geometry[index].Latitude, geometry[index].Longitude);
            if (index == 0) path.MoveTo((float)projected.X, (float)projected.Y);
            else path.LineTo((float)projected.X, (float)projected.Y);
        }
        if (close) path.Close();
        return path;
    }

    private static SKPaint LinePaint(SKColor colour, float width) => new()
    {
        Color = colour,
        StrokeWidth = width,
        Style = SKPaintStyle.Stroke,
        StrokeCap = SKStrokeCap.Round,
        StrokeJoin = SKStrokeJoin.Round,
        IsAntialias = true
    };

    private static void EnsureMapArtworkIsVisible(SKBitmap bitmap)
    {
        var startX = (int)(bitmap.Width * .55f);
        const int step = 2;
        var samples = 0;
        var opaqueSamples = 0;
        var edgeSamples = 0;
        double luminanceSum = 0;
        double luminanceSquaredSum = 0;
        double edgeDifferenceSum = 0;

        for (var y = 0; y < bitmap.Height; y += step)
        for (var x = startX; x < bitmap.Width; x += step)
        {
            samples++;
            var pixel = bitmap.GetPixel(x, y);
            if (pixel.Alpha < 200) continue;
            opaqueSamples++;
            var luminance = Luminance(pixel);
            luminanceSum += luminance;
            luminanceSquaredSum += luminance * luminance;
            if (x + step >= bitmap.Width) continue;
            var adjacent = bitmap.GetPixel(x + step, y);
            if (adjacent.Alpha < 200) continue;
            edgeDifferenceSum += Math.Abs(luminance - Luminance(adjacent));
            edgeSamples++;
        }

        if (samples < 1_000 || opaqueSamples < 800 || edgeSamples < 500)
            throw new InvalidDataException("Styled map artwork did not contain enough meaningful samples.");

        var opaqueRatio = opaqueSamples / (double)samples;
        var mean = luminanceSum / opaqueSamples;
        var deviation = Math.Sqrt(Math.Max(0, luminanceSquaredSum / opaqueSamples - mean * mean));
        var meanEdgeDifference = edgeDifferenceSum / edgeSamples;
        if (opaqueRatio < .80 || mean < 28 || deviation < 3.5 || meanEdgeDifference < .65)
            throw new InvalidDataException(
                $"Styled map artwork is not sufficiently visible: opacity {opaqueRatio:P1}, " +
                $"mean luminance {mean:F2}, deviation {deviation:F2}, edge difference {meanEdgeDifference:F2}.");
    }

    private static double Luminance(SKColor pixel) =>
        .2126 * pixel.Red + .7152 * pixel.Green + .0722 * pixel.Blue;

    private static void DrawPin(SKCanvas canvas, float x, float y)
    {
        using var glow = new SKPaint { Color = SKColor.Parse("#3D7868FF"), IsAntialias = true };
        canvas.DrawCircle(x, y + 4, 19, glow);
        using var innerGlow = new SKPaint { Color = SKColor.Parse("#487F6BFF"), IsAntialias = true };
        canvas.DrawCircle(x, y + 4, 12, innerGlow);
        using var path = new SKPath();
        path.MoveTo(x, y + 18);
        path.CubicTo(x - 4, y + 10, x - 10, y + 3, x - 10, y - 5);
        path.CubicTo(x - 10, y - 13, x - 4, y - 18, x, y - 18);
        path.CubicTo(x + 4, y - 18, x + 10, y - 13, x + 10, y - 5);
        path.CubicTo(x + 10, y + 3, x + 4, y + 10, x, y + 18);
        path.Close();
        using var outline = new SKPaint
        {
            Color = SKColor.Parse("#D0080D18"), IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = 3, StrokeJoin = SKStrokeJoin.Round
        };
        canvas.DrawPath(path, outline);
        using var pin = new SKPaint { Color = SKColor.Parse("#A99CFF"), IsAntialias = true };
        canvas.DrawPath(path, pin);
        using var centreOutline = new SKPaint { Color = SKColor.Parse("#C0050911"), IsAntialias = true };
        canvas.DrawCircle(x, y - 5, 5.2f, centreOutline);
        using var centre = new SKPaint { Color = SKColor.Parse("#11182A"), IsAntialias = true };
        canvas.DrawCircle(x, y - 5, 3.5f, centre);
    }

    private static MapImageValidationResult Analyse(SKBitmap bitmap, double maximumTransparentRatio,
        double minimumDeviation, double minimumRange, int minimumColours, double minimumEdgeDifference)
    {
        var step = Math.Max(2, Math.Min(bitmap.Width, bitmap.Height) / 120);
        var colours = new HashSet<int>();
        double sum = 0, sumSquared = 0, edgeSum = 0;
        var min = 255d;
        var max = 0d;
        var samples = 0;
        var transparent = 0;
        var edgeSamples = 0;
        for (var y = 0; y < bitmap.Height; y += step)
        for (var x = 0; x < bitmap.Width; x += step)
        {
            var pixel = bitmap.GetPixel(x, y);
            samples++;
            if (pixel.Alpha < 230) transparent++;
            var luminance = .2126 * pixel.Red + .7152 * pixel.Green + .0722 * pixel.Blue;
            sum += luminance;
            sumSquared += luminance * luminance;
            min = Math.Min(min, luminance);
            max = Math.Max(max, luminance);
            colours.Add((pixel.Red / 16 << 8) | (pixel.Green / 16 << 4) | pixel.Blue / 16);
            if (x + step < bitmap.Width)
            {
                var adjacent = bitmap.GetPixel(x + step, y);
                var adjacentLuminance = .2126 * adjacent.Red + .7152 * adjacent.Green + .0722 * adjacent.Blue;
                edgeSum += Math.Abs(luminance - adjacentLuminance);
                edgeSamples++;
            }
        }
        var mean = sum / Math.Max(1, samples);
        var deviation = Math.Sqrt(Math.Max(0, sumSquared / Math.Max(1, samples) - mean * mean));
        var range = max - min;
        var transparentRatio = transparent / (double)Math.Max(1, samples);
        var meanEdge = edgeSum / Math.Max(1, edgeSamples);
        var reason = transparentRatio > maximumTransparentRatio
            ? $"Transparent pixel ratio {transparentRatio:P1} is too high."
            : deviation < minimumDeviation
                ? $"Luminance deviation {deviation:F2} is too low."
                : range < minimumRange
                    ? $"Luminance range {range:F1} is too narrow."
                    : colours.Count < minimumColours
                        ? $"Only {colours.Count} quantised colours were detected."
                        : meanEdge < minimumEdgeDifference
                            ? $"Mean edge difference {meanEdge:F2} contains insufficient map detail."
                            : "Image contains usable map detail.";
        var valid = transparentRatio <= maximumTransparentRatio && deviation >= minimumDeviation &&
                    range >= minimumRange && colours.Count >= minimumColours && meanEdge >= minimumEdgeDifference;
        return new MapImageValidationResult(valid, reason, deviation, range, transparentRatio, colours.Count, meanEdge);
    }

    private static MapImageValidationResult Invalid(string reason) => new(false, reason, 0, 0, 1, 0, 0);
}
