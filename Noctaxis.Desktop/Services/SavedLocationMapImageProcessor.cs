using Noctaxis.Core.Environment;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

public sealed record SettlementRenderDiagnostics(
    int DensityCellCount,
    double MaximumDensityBeforeClamping,
    int ActiveSettlementCellCount,
    bool PrimaryComponentSelected,
    int PrimaryComponentCellCount,
    int SubCoreCount,
    int GeneratedStarCount,
    bool SettlementRendered);

/// <summary>Provider-neutral validation and Noctaxis card-art processing.</summary>
public sealed class SavedLocationMapImageProcessor
{
    public const int ThumbnailWidth = 512;
    public const int ThumbnailHeight = 280;
    public const string RendererId = "settlement-galaxy-passes-1-14";
    public const int RendererVersion = 3;
    public const int StyleVersion = 15;
    public const int FeatureOverlayStyleVersion = 1;
    public const int SettlementGlowStyleVersion = 8;
    private readonly SettlementDensityBuilder _densityBuilder;
    private readonly SettlementGlowGeometryCalculator _geometryCalculator;
    private readonly SettlementGlowCompositor _glowCompositor;
    private readonly SettlementStarGenerator _starGenerator;
    private readonly SettlementGalaxyStyle _style;
    private readonly ILogger<SavedLocationMapImageProcessor> _logger;

    public string SettlementStyleSettingsHash => _style.SettingsHash;
    public int SettlementPresetVersion => _style.StyleVersion;
    public string SettlementPresetName => _style.PresetName;

    public SavedLocationMapImageProcessor() : this(new SettlementDensityBuilder(),
        new SettlementGlowGeometryCalculator(), new SettlementGlowCompositor(), new SettlementStarGenerator(),
        SettlementGalaxyStyle.DefaultV1) { }

    public SavedLocationMapImageProcessor(SettlementDensityBuilder densityBuilder,
        SettlementGlowGeometryCalculator geometryCalculator, SettlementGlowCompositor glowCompositor)
        : this(densityBuilder, geometryCalculator, glowCompositor, new SettlementStarGenerator(),
            SettlementGalaxyStyle.DefaultV1) { }

    public SavedLocationMapImageProcessor(SettlementDensityBuilder densityBuilder,
        SettlementGlowGeometryCalculator geometryCalculator, SettlementGlowCompositor glowCompositor,
        SettlementStarGenerator starGenerator, SettlementGalaxyStyle? style = null,
        ILogger<SavedLocationMapImageProcessor>? logger = null)
    {
        _densityBuilder = densityBuilder;
        _geometryCalculator = geometryCalculator;
        _glowCompositor = glowCompositor;
        _starGenerator = starGenerator;
        _style = style ?? SettlementGalaxyStyle.DefaultV1;
        _logger = logger ?? NullLogger<SavedLocationMapImageProcessor>.Instance;
    }

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

    public byte[] Process(byte[] sourcePng) => ProcessSettlement(sourcePng, null, null, null, out _);

    public byte[] Process(byte[] sourcePng, MapFeatureDataDocument? features, WebMercatorViewport? viewport)
        => ProcessSettlement(sourcePng, features, null, viewport, out _);

    public byte[] ProcessSettlement(byte[] sourcePng, MapFeatureDataDocument? features,
        SettlementRaster? settlement, WebMercatorViewport? viewport,
        out SettlementRenderDiagnostics? settlementDiagnostics)
        => ProcessCore(sourcePng, features, settlement, viewport, Guid.Empty, null, out settlementDiagnostics);

    public byte[] ProcessSettlement(byte[] sourcePng, MapFeatureDataDocument? features,
        SettlementRaster? settlement, WebMercatorViewport? viewport, Guid locationId,
        out SettlementRenderDiagnostics? settlementDiagnostics)
        => ProcessCore(sourcePng, features, settlement, viewport, locationId, null, out settlementDiagnostics);

    public byte[] ProcessSettlementDebug(byte[] sourcePng, MapFeatureDataDocument? features,
        SettlementRaster? settlement, WebMercatorViewport? viewport, Guid locationId, string outputDirectory,
        out SettlementRenderDiagnostics? settlementDiagnostics)
        => ProcessCore(sourcePng, features, settlement, viewport, locationId,
            new SettlementGalaxyDebugWriter(outputDirectory), out settlementDiagnostics);

    private byte[] ProcessCore(byte[] sourcePng, MapFeatureDataDocument? features,
        SettlementRaster? settlement, WebMercatorViewport? viewport, Guid locationId,
        SettlementGalaxyDebugWriter? debug,
        out SettlementRenderDiagnostics? settlementDiagnostics)
    {
        var totalTimer = Stopwatch.StartNew();
        settlementDiagnostics = null;
        var validation = ValidateSource(sourcePng);
        if (!validation.IsValid) throw new InvalidDataException("Source map validation failed: " + validation.Reason);
        if ((features is not null || settlement is not null) && viewport is null)
            throw new ArgumentNullException(nameof(viewport), "A viewport is required to align semantic map features.");
        using var source = SKBitmap.Decode(sourcePng)!;
        using var scaled = ScaleAndCrop(source);
        using var roadOverlay = features is null ? null : RenderRoadOverlay(source.Width, source.Height, features, viewport!);
        using var waterOverlay = features is null ? null : RenderWaterOverlay(source.Width, source.Height, features, viewport!);
        using var scaledRoadOverlay = roadOverlay is null ? null : ScaleAndCrop(roadOverlay);
        using var scaledWaterOverlay = waterOverlay is null ? null : ScaleAndCrop(waterOverlay);
        var preparationMs = totalTimer.Elapsed.TotalMilliseconds;
        SettlementGlowGeometry? glowGeometry = null;
        SettlementDensityModel? density = null;
        if (settlement is not null)
        {
            density = _densityBuilder.Build(settlement, ThumbnailWidth, ThumbnailHeight, _style);
            glowGeometry = _geometryCalculator.Calculate(density, ThumbnailWidth, ThumbnailHeight, _style);
            settlementDiagnostics = new SettlementRenderDiagnostics(density.DensityCellCount,
                density.MaximumDensity, density.ActiveSettlementCellCount, density.HasPrimaryComponent,
                density.PrimaryComponentCellCount, glowGeometry?.SubCores.Length ?? 0, 0,
                false);
        }
        var densityMs = totalTimer.Elapsed.TotalMilliseconds - preparationMs;
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

        // Finish the styled base before any settlement light or semantic lines are added.
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
        if (debug is not null && density is not null) debug.BeginRun(output, density);
        var baseMs = totalTimer.Elapsed.TotalMilliseconds - preparationMs - densityMs;

        canvas.Flush();
        var renderViewport = viewport ?? new WebMercatorViewport(0, 256, source.Width, source.Height, 0, 0);
        var context = new SettlementGalaxyRenderContext(locationId, renderViewport);
        using var finalRoadOverlay = scaledRoadOverlay is null ? null : TransformOverlay(scaledRoadOverlay, skew);
        using var finalWaterOverlay = scaledWaterOverlay is null ? null : TransformOverlay(scaledWaterOverlay, skew);
        var featureMasks = BuildFeatureMasks(finalRoadOverlay, finalWaterOverlay, ThumbnailWidth, ThumbnailHeight);
        SettlementGalaxyFieldMap? galaxyFields = null;
        var stars = settlement is null
            ? Array.Empty<SettlementStar>()
            : _starGenerator.Generate(settlement, ThumbnailWidth, ThumbnailHeight, context, _style).ToArray();
        if (settlementDiagnostics is not null)
            settlementDiagnostics = settlementDiagnostics with
            {
                GeneratedStarCount = stars.Length,
                SettlementRendered = glowGeometry is not null || stars.Length > 0
            };
        if (settlement is not null && density is not null && glowGeometry is not null)
        {
            galaxyFields = _glowCompositor.CompositeAstronomicalLayers(output, density, glowGeometry,
                stars, context, _style, debug, featureMasks);
        }
        else
        {
            _glowCompositor.CompositeBackgroundAmbience(output, context, _style);
            if (density is not null && stars.Length > 0)
                _glowCompositor.CompositeSettlementStarsOnly(output, density, stars, _style);
        }
        var astronomicalMs = totalTimer.Elapsed.TotalMilliseconds - preparationMs - densityMs - baseMs;

        // The existing line renderers remain geometry driven. Roads and then water are separately
        // redrawn above the astronomical layers; density only controls their selected retention.
        if (finalRoadOverlay is not null)
            CompositeFeatureLayer(output, finalRoadOverlay, galaxyFields, isWater: false, _style.MapIntegration);
        if (finalWaterOverlay is not null)
            CompositeFeatureLayer(output, finalWaterOverlay, galaxyFields, isWater: true, _style.MapIntegration);
        // Pass 12 is captured again after real roads/water are redrawn so the diagnostic proves
        // that semantic lines remain above the completed positive-light envelope. Pass 11 keeps
        // the compositor's pre-falloff map-integration snapshot instead of being overwritten.
        debug?.WriteColour("12-falloff.png", output);
        var semanticMs = totalTimer.Elapsed.TotalMilliseconds - preparationMs - densityMs - baseMs - astronomicalMs;

        // Tonemapping changes only photographic response: highlight shoulder compression and
        // positive local detail. It precedes the pin so the location glyph is never filtered.
        _glowCompositor.ApplyTonemapping(output, _style, debug);
        var toneMs = totalTimer.Elapsed.TotalMilliseconds - preparationMs - densityMs - baseMs - astronomicalMs - semanticMs;

        // The source location is centred within the map artwork. In the 42/58 card split this
        // places the final pin at roughly 71% of the complete card width.
        DrawPin(canvas, ThumbnailWidth / 2f, ThumbnailHeight / 2f);
        using var image = SKImage.FromBitmap(output);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 90);
        var bytes = encoded.ToArray();
        var processedValidation = ValidateThumbnail(bytes);
        if (!processedValidation.IsValid)
            throw new InvalidDataException("Processed thumbnail validation failed: " + processedValidation.Reason);
        _logger.LogDebug(
            "SavedLocationThumbnail renderer timing: Renderer={Renderer}; Style={Style}; StyleVersion={StyleVersion}; Preparation={PreparationMs:F1} ms; Density={DensityMs:F1} ms; Base={BaseMs:F1} ms; Astronomical={AstronomicalMs:F1} ms; Semantic={SemanticMs:F1} ms; Tonemap={ToneMs:F1} ms; EncodeAndValidate={EncodeMs:F1} ms; Total={TotalMs:F1} ms",
            RendererId, _style.PresetName, StyleVersion, preparationMs, densityMs, baseMs, astronomicalMs,
            semanticMs, toneMs, totalTimer.Elapsed.TotalMilliseconds - preparationMs - densityMs - baseMs -
            astronomicalMs - semanticMs - toneMs, totalTimer.Elapsed.TotalMilliseconds);
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

    private static SKBitmap RenderRoadOverlay(int width, int height, MapFeatureDataDocument features,
        WebMercatorViewport viewport)
    {
        var overlay = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        overlay.Erase(SKColors.Transparent);
        using var canvas = new SKCanvas(overlay);
        canvas.ClipRect(SKRect.Create(width, height));

        foreach (var road in features.Roads.OrderByDescending(road => road.Classification)
                     .ThenBy(road => road.ElementType, StringComparer.Ordinal).ThenBy(road => road.Id))
            DrawRoad(canvas, road, viewport);
        return overlay;
    }

    private static SKBitmap RenderWaterOverlay(int width, int height, MapFeatureDataDocument features,
        WebMercatorViewport viewport)
    {
        var overlay = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        overlay.Erase(SKColors.Transparent);
        using var canvas = new SKCanvas(overlay);
        canvas.ClipRect(SKRect.Create(width, height));
        foreach (var waterway in features.Waterways.OrderBy(waterway => waterway.ElementType,
                     StringComparer.Ordinal).ThenBy(waterway => waterway.Id))
            DrawWaterway(canvas, waterway, viewport);
        return overlay;
    }

    private static SKBitmap TransformOverlay(SKBitmap overlay, float skew)
    {
        var transformed = new SKBitmap(ThumbnailWidth, ThumbnailHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        transformed.Erase(SKColors.Transparent);
        using var canvas = new SKCanvas(transformed);
        canvas.Translate(ThumbnailWidth / 2f, ThumbnailHeight / 2f);
        canvas.Skew(skew, 0);
        canvas.Translate(-ThumbnailWidth / 2f, -ThumbnailHeight / 2f);
        using var image = SKImage.FromBitmap(overlay);
        canvas.DrawImage(image, new SKRect(-6, -2, ThumbnailWidth + 6, ThumbnailHeight + 2),
            new SKSamplingOptions(SKCubicResampler.Mitchell));
        return transformed;
    }

    private static SettlementGalaxyFeatureMasks BuildFeatureMasks(SKBitmap? roads, SKBitmap? water,
        int width, int height)
    {
        var roadMask = new float[width * height];
        var waterMask = new float[width * height];
        var pinMask = new float[width * height];
        CopyAlpha(roads, roadMask);
        CopyAlpha(water, waterMask);
        var centreX = width / 2d;
        var centreY = height / 2d;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            if (Square(x - centreX) + Square(y - centreY) <= Square(24))
                pinMask[y * width + x] = 1;
        return new SettlementGalaxyFeatureMasks(roadMask, waterMask, pinMask);

        static void CopyAlpha(SKBitmap? bitmap, float[] destination)
        {
            if (bitmap is null) return;
            var pixels = new SkiaBitmapPixelBuffer(bitmap);
            for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                destination[y * bitmap.Width + x] = pixels.Read(x, y).Alpha / 255f;
        }
    }

    private static void CompositeFeatureLayer(SKBitmap destination, SKBitmap overlay,
        SettlementGalaxyFieldMap? fields, bool isWater, MapIntegrationStyle integration)
    {
        var destinationPixels = new SkiaBitmapPixelBuffer(destination);
        var overlayPixels = new SkiaBitmapPixelBuffer(overlay);
        for (var y = 0; y < destination.Height; y++)
        for (var x = 0; x < destination.Width; x++)
        {
            var source = overlayPixels.Read(x, y);
            if (source.Alpha == 0) continue;
            var body = fields?.At(fields.Body, x, y) ?? 0;
            var dense = fields?.At(fields.Dense, x, y) ?? 0;
            var retention = isWater
                ? 1 - dense * (1 - integration.DenseRiverRetention)
                : 1 - body * (1 - integration.BodyRoadRetention) - dense *
                    (integration.BodyRoadRetention - integration.DenseRoadRetention);
            retention = Math.Clamp(retention, 0, 1);
            var alpha = source.Alpha / 255d;
            var target = destinationPixels.Read(x, y);
            var index = y * destination.Width + x;
            var underRed = fields is null ? target.Red : fields.UnderRed[index] * 255;
            var underGreen = fields is null ? target.Green : fields.UnderGreen[index] * 255;
            var underBlue = fields is null ? target.Blue : fields.UnderBlue[index] * 255;
            var roadRed = underRed * (1 - retention) + source.Red * retention;
            var roadGreen = underGreen * (1 - retention) + source.Green * retention;
            var roadBlue = underBlue * (1 - retention) + source.Blue * retention;
            destinationPixels.Write(x, y,
                BlendChannel(target.Red, roadRed, alpha),
                BlendChannel(target.Green, roadGreen, alpha),
                BlendChannel(target.Blue, roadBlue, alpha), target.Alpha);
        }
    }

    private static byte BlendChannel(double target, double source, double alpha) =>
        (byte)Math.Clamp((int)Math.Round(target * (1 - alpha) + source * alpha), 0, 255);

    private static double Square(double value) => value * value;

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
