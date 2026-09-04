using SkiaSharp;

namespace Noctaxis.Desktop.Services;

public enum SettlementGalaxyStage
{
    MapBackground,
    GalaxyBodyAndHierarchy,
    GalaxyColourZoning,
    GalaxyLuminance,
    CoreRadiance,
    WhirlpoolCloudUnderlay,
    EmissionWisps,
    SettlementStars,
    StarColourAura,
    SatelliteTreatment,
    BackgroundAmbience,
    MapIntegration,
    OuterFalloff,
    Roads,
    Water,
    Tonemapping,
    Pin
}

public sealed record SettlementGalaxyFieldMap(int Width, int Height, float[] Body, float[] Dense,
    float[] UnderRed, float[] UnderGreen, float[] UnderBlue)
{
    public float At(float[] field, int x, int y) => field[y * Width + x];
}

public sealed record SettlementOuterFalloffFields(float[] Falloff, float[] Mid);

public sealed record SettlementGalaxyFeatureMasks(float[] Roads, float[] Water, float[] Pin);

/// <summary>
/// Literal sequential port of the selected Passes 1-13. Astronomical stages only add light or
/// replace chroma at constant luminance; the sole global photographic transform is Pass 13.
/// </summary>
public sealed class SettlementGlowCompositor
{
    private readonly IMapImageAcceleration _acceleration;

    public SettlementGlowCompositor(IMapImageAcceleration? acceleration = null) =>
        _acceleration = acceleration ?? OpenCvMapImageAcceleration.Shared;

    public static IReadOnlyList<SettlementGalaxyStage> ProductionLayerOrder { get; } =
    [
        SettlementGalaxyStage.MapBackground, SettlementGalaxyStage.GalaxyBodyAndHierarchy,
        SettlementGalaxyStage.GalaxyColourZoning, SettlementGalaxyStage.GalaxyLuminance,
        SettlementGalaxyStage.CoreRadiance, SettlementGalaxyStage.WhirlpoolCloudUnderlay,
        SettlementGalaxyStage.EmissionWisps, SettlementGalaxyStage.SettlementStars,
        SettlementGalaxyStage.StarColourAura, SettlementGalaxyStage.SatelliteTreatment,
        SettlementGalaxyStage.BackgroundAmbience, SettlementGalaxyStage.MapIntegration,
        SettlementGalaxyStage.OuterFalloff, SettlementGalaxyStage.Roads, SettlementGalaxyStage.Water,
        SettlementGalaxyStage.Tonemapping, SettlementGalaxyStage.Pin
    ];

    public SettlementGalaxyFieldMap CompositeAstronomicalLayers(SKBitmap bitmap,
        SettlementDensityModel density, SettlementGlowGeometry geometry, IReadOnlyList<SettlementStar> stars,
        SettlementGalaxyRenderContext context, SettlementGalaxyStyle style,
        SettlementGalaxyDebugWriter? debug = null, SettlementGalaxyFeatureMasks? featureMasks = null)
    {
        var image = FloatImage.Read(bitmap);
        var width = bitmap.Width;
        var height = bitmap.Height;
        var globalDensity = ResampleDensity(density.Density, density.Width, density.Height, width, height);
        var mainDensity = new float[globalDensity.Length];
        var mainMask = density.PrimaryComponent.Select(value => value ? 1f : 0f).ToArray();
        var mappedMask = ResampleDensity(mainMask, density.Width, density.Height, width, height);
        for (var i = 0; i < mainDensity.Length; i++) mainDensity[i] = globalDensity[i] * mappedMask[i];

        // Pass 1: selected hierarchy, with one combined positive emission field.
        var broadDensity = SettlementGalaxyPassMath.NormaliseMaximum(_acceleration.GaussianBlur(mainDensity,
            width, height, style.Galaxy.Hierarchy.BroadDensitySigma, GaussianBorderMode.Reflect));
        ApplyHierarchy(image, globalDensity, geometry, style.Galaxy.Hierarchy);
        debug?.WriteColour("01-hierarchy.png", image);
        debug?.WriteGray("density.png", globalDensity, width, height);
        debug?.WriteGray("broad-density.png", broadDensity, width, height);
        debug?.WriteLabels("component-map.png", density.ComponentLabels, density.Width, density.Height,
            width, height);

        // Pass 2: luminance-preserving YUV chroma replacement.
        var zoning = SettlementGalaxyPassMath.BuildColourZoning(globalDensity, style.Galaxy.ColourZoning);
        for (var i = 0; i < image.Count; i++)
        {
            var replaced = SettlementGalaxyPassMath.ReplaceChroma(image.At(i), zoning.Target[i],
                zoning.Alpha[i], style.Galaxy.ColourZoning.Strength);
            image.Set(i, replaced);
        }
        debug?.WriteColour("02-colour-zoning.png", image);

        // Pass 3: broad/dense/knot/core/bloom/hot responses followed by the reference soft compression.
        var luminanceEmission = SettlementGalaxyPassMath.BuildLuminosityEmission(globalDensity, width, height,
            style.Galaxy.Luminance, _acceleration);
        image.Screen(luminanceEmission);
        debug?.WriteColour("03-luminosity.png", image);

        // Pass 4: hero core derives from the strongest separated maxima, merging the top pair when close.
        var coreStyle = style.Galaxy.CoreRadiance;
        var hero = SettlementGalaxyPassMath.FindHeroCore(mainDensity, width, height, coreStyle.MergeDistance,
            coreStyle.PeakFilterSize, coreStyle.PeakThreshold, coreStyle.MaximumPeakCount,
            coreStyle.PeakMinimumDistance);
        var heroX = hero?.X ?? geometry.CentreX;
        var heroY = hero?.Y ?? geometry.CentreY;
        var coreMask = ApplyHeroCore(image, heroX, heroY, geometry, coreStyle);
        debug?.WriteColour("04-core-radiance.png", image);
        debug?.WriteGray("core-mask.png", coreMask, width, height);

        // Pass 5: broad density-gated spiral/noise underlay centred on the selected hero core.
        var cloud = BuildCloudField(globalDensity, width, height, heroX, heroY, geometry.AngleRadians,
            context, style);
        ScreenField(image, cloud, style.Clouds.Colour, style.Clouds.Gain);
        debug?.WriteColour("05-clouds.png", image);
        debug?.WriteGray("cloud-field.png", cloud, width, height);

        // Pass 6: deterministic density-weighted bright fibres, never negative lanes.
        var wisps = BuildWispField(globalDensity, width, height, geometry.AngleRadians, context, style);
        ScreenField(image, wisps, style.Wisps.Colour, 1);
        debug?.WriteColour("06-wisps.png", image);

        // Pass 7: all settlement impulses share one field before blur and percentile normalisation.
        var starFields = SettlementGalaxyPassMath.BuildStarHierarchyFields(stars, width, height, style.Stars,
            _acceleration);
        var starEmission = new SettlementEmissionFields(new float[image.Count], new float[image.Count],
            new float[image.Count]);
        for (var i = 0; i < image.Count; i++)
        {
            var intensity = starFields.StarCore[i] + starFields.Bloom[i] * style.Stars.BrightBloomGain;
            starEmission.Red[i] = (float)(style.Stars.NeutralColour[0] / 255d * intensity);
            starEmission.Green[i] = (float)(style.Stars.NeutralColour[1] / 255d * intensity);
            starEmission.Blue[i] = (float)(style.Stars.NeutralColour[2] / 255d * intensity);
        }
        image.Screen(starEmission);
        debug?.WriteColour("07-stars.png", image);
        debug?.WriteGray("star-impulses.png", starFields.CoreImpulses, width, height);

        // Pass 8: overlapping core/bridge/haze chroma zones preserve the current luminance.
        ApplyStarChroma(image, stars, globalDensity, width, height, style.Stars.ColourVariation);
        debug?.WriteColour("08-star-chroma.png", image);

        // Pass 9: compact but visibly astronomical subordinate component bodies.
        ApplySatellites(image, globalDensity, geometry, style.Satellites);
        debug?.WriteColour("09-satellites.png", image);

        // Pass 10: ambience is explicitly suppressed by real settlement density.
        ApplyAmbience(image, globalDensity, width, height, context, style, featureMasks);
        debug?.WriteColour("10-ambience.png", image);

        // Pass 11: map contrast and positive luminance lift. Real roads/water are drawn afterwards.
        var fields = ApplyMapIntegration(image, globalDensity, width, height, style.MapIntegration);
        debug?.WriteColour("11-map-integration.png", image);

        // Pass 12: continuous, broad, constant-border density falloff; no PCA edge and no darkness.
        var falloff = BuildOuterFalloffFields(globalDensity, width, height, style);
        var falloffEmission = new SettlementEmissionFields(new float[image.Count], new float[image.Count],
            new float[image.Count]);
        for (var i = 0; i < image.Count; i++)
        {
            AddColour(falloffEmission, i, style.OuterFalloff.OuterColour,
                falloff.Falloff[i] * style.OuterFalloff.Gain);
            AddColour(falloffEmission, i, style.OuterFalloff.MidColour,
                falloff.Mid[i] * style.OuterFalloff.Gain * style.OuterFalloff.MidGainFactor);
        }
        image.Screen(falloffEmission);
        debug?.WriteColour("12-falloff.png", image);
        debug?.WriteGray("outer-falloff.png", falloff.Falloff, width, height);

        image.Write(bitmap);
        return fields;
    }

    public void CompositeBackgroundAmbience(SKBitmap bitmap, SettlementGalaxyRenderContext context,
        SettlementGalaxyStyle style)
    {
        var image = FloatImage.Read(bitmap);
        ApplyAmbience(image, new float[image.Count], bitmap.Width, bitmap.Height, context, style, null);
        image.Write(bitmap);
    }

    public void CompositeSettlementStarsOnly(SKBitmap bitmap, SettlementDensityModel density,
        IReadOnlyList<SettlementStar> stars, SettlementGalaxyStyle style)
    {
        var image = FloatImage.Read(bitmap);
        var densityMap = ResampleDensity(density.Density, density.Width, density.Height, bitmap.Width, bitmap.Height);
        var fields = SettlementGalaxyPassMath.BuildStarHierarchyFields(stars, bitmap.Width, bitmap.Height,
            style.Stars, _acceleration);
        for (var i = 0; i < image.Count; i++)
        {
            var value = fields.StarCore[i] + fields.Bloom[i] * style.Stars.BrightBloomGain;
            image.Screen(i, style.Stars.NeutralColour, value);
        }
        ApplyStarChroma(image, stars, densityMap, bitmap.Width, bitmap.Height, style.Stars.ColourVariation);
        image.Write(bitmap);
    }

    public void ApplyTonemapping(SKBitmap bitmap, SettlementGalaxyStyle style,
        SettlementGalaxyDebugWriter? debug = null)
    {
        var image = FloatImage.Read(bitmap);
        var luminance = new float[image.Count];
        for (var i = 0; i < image.Count; i++) luminance[i] = (float)SettlementGalaxyPassMath.Luminance(image.At(i));
        // Python computes the local field from shoulder-compressed luminance, not the original image.
        var shoulder = new float[image.Count];
        for (var i = 0; i < image.Count; i++)
        {
            var y = luminance[i];
            var amount = SettlementGalaxyPassMath.SmoothStep(style.Tonemapping.HighlightThreshold, 1, y);
            shoulder[i] = (float)Math.Clamp(y - amount * style.Tonemapping.HighlightCompression *
                (y - style.Tonemapping.HighlightThreshold) * (1 - y), 0, 1);
        }
        var local = _acceleration.GaussianBlur(shoulder, bitmap.Width, bitmap.Height,
            style.Tonemapping.LocalRadius, GaussianBorderMode.Reflect);
        for (var i = 0; i < image.Count; i++)
        {
            var source = image.At(i);
            RgbToYuv(source, out _, out var u, out var v);
            var mappedY = SettlementGalaxyPassMath.ToneMapLuminance(luminance[i], local[i], style.Tonemapping);
            var saturationWeight = SettlementGalaxyPassMath.SmoothStep(style.Tonemapping.SaturationStart,
                style.Tonemapping.SaturationFull, mappedY) *
                (1 - SettlementGalaxyPassMath.SmoothStep(style.Tonemapping.SaturationFadeStart,
                    style.Tonemapping.SaturationFadeEnd, mappedY));
            var saturation = 1 + (style.Tonemapping.Saturation - 1) * saturationWeight;
            image.Set(i, YuvToRgb(mappedY, u * saturation, v * saturation));
        }
        image.Write(bitmap);
        debug?.WriteColour("13-tonemapping.png", image);
    }

    internal static double CompressHighlight(double luminance, TonemappingStyle style)
    {
        var shoulder = SettlementGalaxyPassMath.SmoothStep(style.HighlightThreshold, 1, luminance);
        return Math.Clamp(luminance - shoulder * style.HighlightCompression *
            (luminance - style.HighlightThreshold) * (1 - luminance), 0, 1);
    }

    internal static double ApplyPositiveLocalContrast(double luminance, double local,
        TonemappingStyle style)
    {
        var weight = 1 - style.DetailSuppressionAmount * SettlementGalaxyPassMath.SmoothStep(
            style.DetailSuppressionStart, style.DetailSuppressionEnd, luminance);
        return Math.Clamp(luminance + Math.Max(0, luminance - local) *
            style.LocalPositiveLightContrast * weight, 0, 1);
    }

    internal SettlementOuterFalloffFields BuildOuterFalloffFields(float[] density, int width, int height,
        SettlementGalaxyStyle style)
    {
        var parameters = style.OuterFalloff;
        var envelope = SettlementGalaxyPassMath.NormaliseMaximum(_acceleration.GaussianBlur(density, width,
            height, parameters.EnvelopeSigma, GaussianBorderMode.Reflect));
        var halo = SettlementDensityBuilder.Normalise(_acceleration.GaussianBlur(envelope, width, height,
            parameters.OuterHaloRadius, GaussianBorderMode.ConstantZero), parameters.NormalisePercentile);
        var falloff = new float[density.Length];
        var mid = new float[density.Length];
        for (var i = 0; i < density.Length; i++)
        {
            var inner = SettlementGalaxyPassMath.SmoothStep(parameters.InnerPresenceStart,
                parameters.InnerPresenceFull, envelope[i]);
            var powered = Math.Pow(Math.Clamp(halo[i], 0, 1), parameters.FalloffGamma);
            var opacity = parameters.MinimumOpacity + (1 - parameters.MinimumOpacity) * powered;
            var outerWeight = parameters.OuterBaseWeight + parameters.OuterAbsenceWeight * (1 - inner);
            var weightedHalo = powered * opacity;
            falloff[i] = (float)Math.Clamp(weightedHalo * outerWeight, 0, 1);
            mid[i] = (float)Math.Clamp(falloff[i] *
                Math.Pow(envelope[i], parameters.MidDensityExponent), 0, 1);
        }
        return new SettlementOuterFalloffFields(falloff, mid);
    }

    private void ApplyHierarchy(FloatImage image, float[] density, SettlementGlowGeometry geometry,
        GalaxyHierarchyStyle style)
    {
        var broad = SettlementGalaxyPassMath.NormaliseMaximum(_acceleration.GaussianBlur(density, image.Width,
            image.Height, style.BroadDensitySigma, GaussianBorderMode.Reflect));
        var emission = NewEmission(image.Count);
        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
        {
            var i = y * image.Width + x;
            var gate = Math.Pow(Math.Clamp(broad[i], 0, 1), style.DensityGateExponent);
            var halo = Ellipse(x, y, geometry.CentreX, geometry.CentreY, geometry.AngleRadians,
                geometry.HaloMajor, geometry.HaloMinor) * (style.HaloDensityFloor + style.HaloDensityWeight * gate);
            var body = Ellipse(x, y, geometry.CentreX, geometry.CentreY, geometry.AngleRadians,
                geometry.BodyMajor, geometry.BodyMinor) * Math.Pow(density[i], style.BodyDensityExponent);
            var core = Ellipse(x, y, geometry.CentreX, geometry.CentreY, geometry.AngleRadians,
                geometry.CoreMajor, geometry.CoreMinor) * Math.Pow(density[i], style.CoreDensityExponent);
            AddColour(emission, i, style.HaloColour, halo * style.HaloGain);
            AddColour(emission, i, style.BodyColour, body * style.BodyGain);
            AddColour(emission, i, style.CoreColour, core * style.CoreGain);
            foreach (var subcore in geometry.SubCores)
            {
                var sub = Ellipse(x, y, subcore.CentreX, subcore.CentreY, subcore.AngleRadians,
                    subcore.MajorRadius, subcore.MinorRadius);
                AddColour(emission, i, style.CoreColour, sub * style.SubcoreGain * subcore.RelativeStrength);
            }
        }
        image.Screen(emission);
    }

    private static float[] ApplyHeroCore(FloatImage image, double centreX, double centreY,
        SettlementGlowGeometry geometry, CoreRadianceStyle style)
    {
        var baseMajor = Math.Max(style.MinimumMajorSigma, geometry.SigmaMajor);
        var baseMinor = Math.Max(style.MinimumMinorSigma, geometry.SigmaMinor);
        var bloomAxes = ClampAxes(baseMajor * style.BloomRadiusScale, baseMinor * style.BloomRadiusScale,
            style.BloomAxisRatioClamp);
        var auraAxes = ClampAxes(baseMajor * style.AuraRadiusScale, baseMinor * style.AuraRadiusScale,
            style.AuraAxisRatioClamp);
        var hotAxes = ClampAxes(baseMajor * style.HotRadiusScale, baseMinor * style.HotRadiusScale,
            style.HotAxisRatioClamp);
        var emission = NewEmission(image.Count);
        var mask = new float[image.Count];
        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
        {
            var i = y * image.Width + x;
            var bloom = Ellipse(x, y, centreX, centreY, geometry.AngleRadians, bloomAxes.Major, bloomAxes.Minor);
            var aura = Ellipse(x, y, centreX, centreY, geometry.AngleRadians, auraAxes.Major, auraAxes.Minor);
            var hot = Ellipse(x, y, centreX, centreY, geometry.AngleRadians, hotAxes.Major, hotAxes.Minor);
            AddColour(emission, i, style.BloomColour, bloom * style.BloomGain);
            AddColour(emission, i, style.AuraColour, aura * style.AuraGain);
            AddColour(emission, i, style.HotColour, hot * style.HotGain);
            mask[i] = (float)Math.Max(bloom, Math.Max(aura, hot));
        }
        image.Screen(emission);
        return mask;
    }

    private float[] BuildCloudField(float[] density, int width, int height, double centreX, double centreY,
        double angle, SettlementGalaxyRenderContext context, SettlementGalaxyStyle style)
    {
        var c = style.Clouds;
        var noise = BuildNoise(width, height, c.NoiseScale,
            SettlementGalaxyDeterminism.DeriveSeed("cloud", context, style));
        noise = _acceleration.GaussianBlur(noise, width, height, c.NoiseBlurSigma,
            GaussianBorderMode.Reflect);
        noise = MinMaxNormalise(noise);
        var haze = SettlementGalaxyPassMath.NormaliseMaximum(_acceleration.GaussianBlur(density, width, height,
            c.HazeSigma, GaussianBorderMode.Reflect));
        var result = new float[density.Length];
        var cos = Math.Cos(angle); var sin = Math.Sin(angle);
        var radialScale = Math.Max(width, height);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var dx = x - centreX; var dy = y - centreY;
            var u = dx * cos + dy * sin; var v = -dx * sin + dy * cos;
            var r = Math.Sqrt(Square(u / width) + Square(v / height)) * radialScale;
            var theta = Math.Atan2(v, u);
            var phase = c.SpiralArms * theta + c.SpiralTwist * Math.Log(1 + r * c.RadialLogScale) +
                        c.RadialFrequency * r;
            var spiral = Math.Pow(.5 + .5 * Math.Cos(phase), c.SpiralPower);
            var structure = c.StructureFloor + (1 - c.StructureFloor) *
                ((1 - c.NoiseMix) * spiral + c.NoiseMix * noise[y * width + x]);
            result[y * width + x] = (float)Math.Clamp(haze[y * width + x] * structure, 0, 1);
        }
        return result;
    }

    private float[] BuildWispField(float[] density, int width, int height, double mainAngle,
        SettlementGalaxyRenderContext context, SettlementGalaxyStyle style)
    {
        var p = style.Wisps;
        var gradientX = new float[density.Length]; var gradientY = new float[density.Length];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = y * width + x;
            gradientX[i] = (Sample(density, width, height, x + 1, y) -
                            Sample(density, width, height, x - 1, y)) * .5f;
            gradientY[i] = (Sample(density, width, height, x, y + 1) -
                            Sample(density, width, height, x, y - 1)) * .5f;
        }
        var weights = density.Select(value => (float)Math.Pow(Math.Clamp(value, 0, 1),
            p.DensityWeightExponent)).ToArray();
        var total = weights.Sum(value => (double)value);
        var scale = Math.Max(1, p.RasterScale);
        using var raster = new SKBitmap(width * scale, height * scale, SKColorType.Bgra8888,
            SKAlphaType.Premul);
        raster.Erase(SKColors.Transparent);
        using (var canvas = new SKCanvas(raster))
        {
            for (var n = 0; n < p.Count && total > 0; n++)
            {
                var seed = SettlementGalaxyDeterminism.DeriveSeed("wisp:" + n, context, style);
                var target = SettlementGalaxyDeterminism.Unit(seed, 0) * total;
                var selected = 0; double cumulative = 0;
                for (; selected < weights.Length - 1; selected++)
                    if ((cumulative += weights[selected]) >= target) break;
                if (density[selected] < p.MinimumDensity) continue;
                var x = selected % width; var y = selected / width;
                var tangent = Math.Atan2(gradientY[selected], gradientX[selected]) + Math.PI / 2;
                var jitter = Normal(seed, 1) * p.AngleJitterSigma;
                var angle = p.MajorAxisInfluence * mainAngle + p.GradientTangentInfluence * tangent + jitter;
                var length = Lerp(p.MinLength, p.MaxLength, SettlementGalaxyDeterminism.Unit(seed, 3));
                var lineWidth = Lerp(p.MinWidth, p.MaxWidth, SettlementGalaxyDeterminism.Unit(seed, 4));
                var gain = Lerp(p.MinGain, p.MaxGain, SettlementGalaxyDeterminism.Unit(seed, 5));
                var dx = Math.Cos(angle) * length / 2; var dy = Math.Sin(angle) * length / 2;
                using var paint = new SKPaint { Color = new SKColor(255, 255, 255, (byte)Math.Round(255 * gain)),
                    StrokeWidth = (float)(lineWidth * scale), StrokeCap = SKStrokeCap.Round, IsAntialias = true };
                canvas.DrawLine((float)((x - dx) * scale), (float)((y - dy) * scale),
                    (float)((x + dx) * scale), (float)((y + dy) * scale), paint);
            }
        }
        var high = new float[raster.Width * raster.Height];
        var pixels = new SkiaBitmapPixelBuffer(raster);
        for (var y = 0; y < raster.Height; y++)
        for (var x = 0; x < raster.Width; x++) high[y * raster.Width + x] = pixels.Read(x, y).Alpha / 255f;
        high = _acceleration.GaussianBlur(high, raster.Width, raster.Height,
            p.MaskBlurRadius * scale, GaussianBorderMode.ConstantZero);
        var result = ResizeLanczos(high, raster.Width, raster.Height, width, height);
        for (var i = 0; i < result.Length; i++) result[i] *= MathF.Sqrt(density[i]);
        return result;
    }

    private void ApplyStarChroma(FloatImage image, IReadOnlyList<SettlementStar> stars, float[] density,
        int width, int height, StarColourStyle style)
    {
        var weight = new float[image.Count];
        var red = new float[image.Count]; var green = new float[image.Count]; var blue = new float[image.Count];
        foreach (var star in stars)
        {
            var x = Math.Clamp((int)Math.Round(star.X), 0, width - 1);
            var y = Math.Clamp((int)Math.Round(star.Y), 0, height - 1);
            var i = y * width + x;
            var local = Math.Max(style.DensityWeightFloor, Math.Pow(density[i], style.DensityWeightExponent));
            weight[i] += (float)local;
            red[i] += (float)(star.Red / 255d * local);
            green[i] += (float)(star.Green / 255d * local);
            blue[i] += (float)(star.Blue / 255d * local);
        }
        ApplyZone(style.HazeSigma, style.HazeChroma);
        ApplyZone(style.BridgeSigma, style.BridgeChroma);
        ApplyZone(style.CoreSigma, style.StarChroma);

        void ApplyZone(double sigma, double chroma)
        {
            var ww = _acceleration.GaussianBlur(weight, width, height, sigma, GaussianBorderMode.Reflect);
            var rr = _acceleration.GaussianBlur(red, width, height, sigma, GaussianBorderMode.Reflect);
            var gg = _acceleration.GaussianBlur(green, width, height, sigma, GaussianBorderMode.Reflect);
            var bb = _acceleration.GaussianBlur(blue, width, height, sigma, GaussianBorderMode.Reflect);
            var maximum = Math.Max(ww.DefaultIfEmpty().Max(), 1e-9f);
            for (var i = 0; i < image.Count; i++)
            {
                if (ww[i] <= 1e-9) continue;
                var target = new RgbFloat(rr[i] / ww[i], gg[i] / ww[i], bb[i] / ww[i]);
                image.Set(i, SettlementGalaxyPassMath.ReplaceChroma(image.At(i), target,
                    Math.Clamp(ww[i] / maximum, 0, 1), chroma));
            }
        }
    }

    private static void ApplySatellites(FloatImage image, float[] density, SettlementGlowGeometry geometry,
        SatelliteStyle style)
    {
        var emission = NewEmission(image.Count);
        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
        {
            var i = y * image.Width + x;
            foreach (var component in geometry.Satellites)
            {
                var ellipse = Ellipse(x, y, component.CentreX, component.CentreY, component.AngleRadians,
                    component.MajorRadius, component.MinorRadius);
                var shaped = ellipse * (style.ShapedDensityFloor + style.ShapedDensityWeight * Math.Sqrt(density[i]));
                var inner = Math.Pow(ellipse, style.InnerEllipseExponent) *
                            Math.Pow(density[i], style.InnerDensityExponent);
                var core = Math.Pow(ellipse, style.CoreEllipseExponent) *
                           Math.Pow(density[i], style.CoreDensityExponent);
                var halo = Math.Pow(ellipse, style.HaloEllipseExponent);
                var strength = Math.Min(1, Math.Max(style.MinimumStrength,
                    Math.Pow(component.RelativeStrength, style.StrengthExponent)));
                AddColour(emission, i, style.BodyColour, shaped * style.BodyGain * strength);
                AddColour(emission, i, style.InnerColour, inner * style.InnerGain * strength);
                if (component.PeakDensity >= style.CorePeakThreshold)
                    AddColour(emission, i, style.CoreColour, core * style.CoreGain * strength);
                AddColour(emission, i, style.HaloColour, halo * style.HaloGain * strength);
            }
            foreach (var component in geometry.MinorComponents)
            {
                var ellipse = Ellipse(x, y, component.CentreX, component.CentreY, component.AngleRadians,
                    component.MajorRadius, component.MinorRadius);
                var strength = Math.Min(style.MinorMaximumStrength,
                    Math.Pow(component.RelativeStrength, style.MinorStrengthExponent));
                AddColour(emission, i, style.HaloColour, ellipse * style.BackgroundComponentGain * strength);
            }
        }
        image.Screen(emission);
    }

    private void ApplyAmbience(FloatImage image, float[] density, int width, int height,
        SettlementGalaxyRenderContext context, SettlementGalaxyStyle style,
        SettlementGalaxyFeatureMasks? featureMasks)
    {
        var p = style.BackgroundAmbience;
        var suppression = SettlementGalaxyPassMath.BuildGalaxySuppression(density, p.GalaxySuppressionExponent);
        var noise = BuildNoise(width, height, p.NoiseScale,
            SettlementGalaxyDeterminism.DeriveSeed("ambience", context, style));
        noise = MinMaxNormalise(_acceleration.GaussianBlur(noise, width, height, p.HazeBlurSigma,
            GaussianBorderMode.Reflect));
        var emission = NewEmission(image.Count);
        for (var i = 0; i < image.Count; i++)
        {
            AddColour(emission, i, p.LiftColour, p.BackgroundLiftGain * suppression[i]);
            AddColour(emission, i, p.HazeColour, p.BroadHazeGain * noise[i] * suppression[i]);
        }
        var impulses = new float[image.Count];
        var accepted = 0;
        for (var attempt = 0; attempt < p.BackgroundStarCount * p.StarAttemptMultiplier &&
                              accepted < p.BackgroundStarCount; attempt++)
        {
            var seed = SettlementGalaxyDeterminism.DeriveSeed("background:" + attempt, context, style);
            var x = Math.Min(width - 1, (int)(SettlementGalaxyDeterminism.Unit(seed, 0) * width));
            var y = Math.Min(height - 1, (int)(SettlementGalaxyDeterminism.Unit(seed, 1) * height));
            var i = y * width + x;
            if (density[i] > p.StarAvoidDensity || featureMasks?.Roads[i] > p.RoadAvoidAlpha ||
                featureMasks?.Water[i] > p.WaterAvoidAlpha || featureMasks?.Pin[i] > p.PinAvoidAlpha) continue;
            impulses[i] += (float)Lerp(p.StarIntensityMin, p.StarIntensityMax,
                SettlementGalaxyDeterminism.Unit(seed, 2));
            accepted++;
        }
        var stars = SettlementGalaxyPassMath.NormaliseMaximum(_acceleration.GaussianBlur(impulses, width,
            height, p.StarSigma, GaussianBorderMode.Reflect));
        for (var i = 0; i < image.Count; i++)
            AddColour(emission, i, p.StarColour, stars[i] * p.BackgroundStarGain);
        image.Screen(emission);
    }

    private SettlementGalaxyFieldMap ApplyMapIntegration(FloatImage image, float[] density, int width, int height,
        MapIntegrationStyle style)
    {
        var body = new float[density.Length]; var dense = new float[density.Length];
        var luminance = new float[density.Length];
        for (var i = 0; i < density.Length; i++)
        {
            body[i] = (float)SettlementGalaxyPassMath.SmoothStep(style.BodyStart, style.BodyFull, density[i]);
            dense[i] = (float)SettlementGalaxyPassMath.SmoothStep(style.DenseStart, style.DenseFull, density[i]);
            luminance[i] = (float)SettlementGalaxyPassMath.Luminance(image.At(i));
        }
        var local = _acceleration.GaussianBlur(luminance, width, height, style.LocalContrastSigma,
            GaussianBorderMode.Reflect);
        for (var i = 0; i < density.Length; i++)
        {
            var source = image.At(i);
            RgbToYuv(source, out _, out var u, out var v);
            var contrast = style.BackgroundMapContrast + (1 - style.BackgroundMapContrast) * body[i];
            var y = local[i] + (luminance[i] - local[i]) * contrast;
            image.Set(i, YuvToRgb(y, u, v));
            image.ScreenWhite(i, style.GalaxyLuminanceLift * body[i] + style.CoreLuminanceLift * dense[i]);
        }
        var underRed = _acceleration.GaussianBlur(image.Red, width, height, style.UnderColourSigma,
            GaussianBorderMode.Reflect);
        var underGreen = _acceleration.GaussianBlur(image.Green, width, height, style.UnderColourSigma,
            GaussianBorderMode.Reflect);
        var underBlue = _acceleration.GaussianBlur(image.Blue, width, height, style.UnderColourSigma,
            GaussianBorderMode.Reflect);
        return new SettlementGalaxyFieldMap(width, height, body, dense, underRed, underGreen, underBlue);
    }

    private float[] ResampleDensity(float[] source, int sourceWidth, int sourceHeight, int width, int height)
    {
        if (sourceWidth == width && sourceHeight == height) return (float[])source.Clone();
        var transform = SettlementGlowGeometryCalculator.ThumbnailTransform.Create(sourceWidth, sourceHeight,
            width, height);
        var output = new float[width * height];
        Parallel.For(0, height, y =>
        {
            for (var x = 0; x < width; x++)
                output[y * width + x] = SettlementDensityBuilder.SampleLinear(source, sourceWidth, sourceHeight,
                    x / transform.Scale + transform.Left, y / transform.Scale + transform.Top);
        });
        return output;
    }

    private static float[] BuildNoise(int width, int height, double scale, ulong seed)
    {
        var gridWidth = (int)Math.Ceiling(width / scale) + 2;
        var gridHeight = (int)Math.Ceiling(height / scale) + 2;
        var grid = new float[gridWidth * gridHeight];
        for (var i = 0; i < grid.Length; i++) grid[i] = (float)SettlementGalaxyDeterminism.Unit(seed, i);
        var output = new float[width * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            // Preserve the reference's complete coarse-grid extent with deterministic cubic
            // interpolation before its explicit Gaussian smoothing stage.
            var gx = width <= 1 ? 0 : x * (gridWidth - 1d) / (width - 1d);
            var gy = height <= 1 ? 0 : y * (gridHeight - 1d) / (height - 1d);
            var ix = (int)Math.Floor(gx); var iy = (int)Math.Floor(gy);
            var tx = gx - ix; var ty = gy - iy;
            var row0 = CubicRow(grid, gridWidth, gridHeight, ix, iy - 1, tx);
            var row1 = CubicRow(grid, gridWidth, gridHeight, ix, iy, tx);
            var row2 = CubicRow(grid, gridWidth, gridHeight, ix, iy + 1, tx);
            var row3 = CubicRow(grid, gridWidth, gridHeight, ix, iy + 2, tx);
            output[y * width + x] = (float)Cubic(row0, row1, row2, row3, ty);
        }
        return MinMaxNormalise(output);
    }

    private static float[] ResizeLanczos(float[] source, int sourceWidth, int sourceHeight,
        int width, int height)
    {
        const int lobes = 3;
        var output = new float[width * height];
        var scaleX = sourceWidth / (double)width;
        var scaleY = sourceHeight / (double)height;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var sourceX = (x + .5) * scaleX - .5;
            var sourceY = (y + .5) * scaleY - .5;
            var left = (int)Math.Floor(sourceX) - lobes + 1;
            var top = (int)Math.Floor(sourceY) - lobes + 1;
            double sum = 0, weightSum = 0;
            for (var yy = top; yy <= top + lobes * 2; yy++)
            {
                var wy = Lanczos(sourceY - yy, lobes);
                if (Math.Abs(wy) <= 1e-12) continue;
                var sy = Math.Clamp(yy, 0, sourceHeight - 1);
                for (var xx = left; xx <= left + lobes * 2; xx++)
                {
                    var weight = wy * Lanczos(sourceX - xx, lobes);
                    if (Math.Abs(weight) <= 1e-12) continue;
                    sum += source[sy * sourceWidth + Math.Clamp(xx, 0, sourceWidth - 1)] * weight;
                    weightSum += weight;
                }
            }
            output[y * width + x] = (float)Math.Clamp(sum / Math.Max(weightSum, 1e-12), 0, 1);
        }
        return output;
    }

    private static double Lanczos(double value, int lobes)
    {
        value = Math.Abs(value);
        if (value < 1e-12) return 1;
        if (value >= lobes) return 0;
        var piValue = Math.PI * value;
        return Math.Sin(piValue) / piValue * Math.Sin(piValue / lobes) / (piValue / lobes);
    }

    private static double Cubic(double a, double b, double c, double d, double t) =>
        b + .5 * t * (c - a + t * (2 * a - 5 * b + 4 * c - d + t * (3 * (b - c) + d - a)));

    private static double CubicRow(float[] grid, int width, int height, int x, int y, double t)
    {
        y = Math.Clamp(y, 0, height - 1);
        var row = y * width;
        return Cubic(grid[row + Math.Clamp(x - 1, 0, width - 1)],
            grid[row + Math.Clamp(x, 0, width - 1)],
            grid[row + Math.Clamp(x + 1, 0, width - 1)],
            grid[row + Math.Clamp(x + 2, 0, width - 1)], t);
    }

    private static float[] MinMaxNormalise(float[] values)
    {
        if (values.Length == 0) return [];
        var minimum = values.Min(); var maximum = values.Max();
        var span = Math.Max(1e-9f, maximum - minimum);
        var result = new float[values.Length];
        for (var i = 0; i < result.Length; i++) result[i] = Math.Clamp((values[i] - minimum) / span, 0, 1);
        return result;
    }

    private static void ScreenField(FloatImage image, float[] field, int[] colour, double gain)
    {
        for (var i = 0; i < field.Length; i++) image.Screen(i, colour, field[i] * gain);
    }

    private static SettlementEmissionFields NewEmission(int count) =>
        new(new float[count], new float[count], new float[count]);

    private static void AddColour(SettlementEmissionFields field, int i, int[] colour, double amount)
    {
        field.Red[i] += (float)(colour[0] / 255d * amount);
        field.Green[i] += (float)(colour[1] / 255d * amount);
        field.Blue[i] += (float)(colour[2] / 255d * amount);
    }

    private static double Ellipse(double x, double y, double centreX, double centreY, double angle,
        double major, double minor)
    {
        var dx = x - centreX; var dy = y - centreY;
        var cos = Math.Cos(angle); var sin = Math.Sin(angle);
        var u = dx * cos + dy * sin; var v = -dx * sin + dy * cos;
        return Math.Exp(-.5 * (Square(u / Math.Max(major, 1e-9)) + Square(v / Math.Max(minor, 1e-9))));
    }

    private static (double Major, double Minor) ClampAxes(double major, double minor, double maximumRatio)
    {
        if (minor > major) (major, minor) = (minor, major);
        major = Math.Min(major, minor * maximumRatio);
        return (Math.Max(1, major), Math.Max(1, minor));
    }

    private static float Sample(float[] source, int width, int height, int x, int y) =>
        source[Math.Clamp(y, 0, height - 1) * width + Math.Clamp(x, 0, width - 1)];

    private static double Normal(ulong seed, int lane)
    {
        var u1 = Math.Max(1e-12, SettlementGalaxyDeterminism.Unit(seed, lane));
        var u2 = SettlementGalaxyDeterminism.Unit(seed, lane + 1);
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
    private static double Square(double value) => value * value;

    private static void RgbToYuv(RgbFloat rgb, out double y, out double u, out double v)
    {
        y = .299 * rgb.Red + .587 * rgb.Green + .114 * rgb.Blue;
        u = -.14713 * rgb.Red - .28886 * rgb.Green + .436 * rgb.Blue;
        v = .615 * rgb.Red - .51499 * rgb.Green - .10001 * rgb.Blue;
    }

    private static RgbFloat YuvToRgb(double y, double u, double v) => new(
        Math.Clamp(y + 1.13983 * v, 0, 1),
        Math.Clamp(y - .39465 * u - .58060 * v, 0, 1),
        Math.Clamp(y + 2.03211 * u, 0, 1));

    internal sealed class FloatImage
    {
        private readonly float[] _red;
        private readonly float[] _green;
        private readonly float[] _blue;
        private readonly byte[] _alpha;
        public int Width { get; }
        public int Height { get; }
        public int Count => _red.Length;
        internal float[] Red => _red;
        internal float[] Green => _green;
        internal float[] Blue => _blue;

        private FloatImage(int width, int height)
        {
            Width = width; Height = height;
            _red = new float[width * height]; _green = new float[width * height];
            _blue = new float[width * height]; _alpha = new byte[width * height];
        }

        public static FloatImage Read(SKBitmap bitmap)
        {
            var result = new FloatImage(bitmap.Width, bitmap.Height);
            var pixels = new SkiaBitmapPixelBuffer(bitmap);
            for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                var i = y * bitmap.Width + x; var p = pixels.Read(x, y);
                result._red[i] = p.Red / 255f; result._green[i] = p.Green / 255f;
                result._blue[i] = p.Blue / 255f; result._alpha[i] = p.Alpha;
            }
            return result;
        }

        public RgbFloat At(int i) => new(_red[i], _green[i], _blue[i]);
        public void Set(int i, RgbFloat value)
        {
            _red[i] = (float)value.Red; _green[i] = (float)value.Green; _blue[i] = (float)value.Blue;
        }

        public void Screen(int i, int[] colour, double amount)
        {
            var a = Math.Max(0, amount);
            _red[i] = (float)(1 - (1 - _red[i]) * (1 - Math.Clamp(colour[0] / 255d * a, 0, 1)));
            _green[i] = (float)(1 - (1 - _green[i]) * (1 - Math.Clamp(colour[1] / 255d * a, 0, 1)));
            _blue[i] = (float)(1 - (1 - _blue[i]) * (1 - Math.Clamp(colour[2] / 255d * a, 0, 1)));
        }

        public void ScreenWhite(int i, double amount)
        {
            var emission = Math.Clamp(amount, 0, 1);
            _red[i] = (float)(1 - (1 - _red[i]) * (1 - emission));
            _green[i] = (float)(1 - (1 - _green[i]) * (1 - emission));
            _blue[i] = (float)(1 - (1 - _blue[i]) * (1 - emission));
        }

        public void Screen(SettlementEmissionFields fields)
        {
            for (var i = 0; i < Count; i++)
            {
                _red[i] = 1 - (1 - _red[i]) * (1 - Math.Clamp(fields.Red[i], 0, 1));
                _green[i] = 1 - (1 - _green[i]) * (1 - Math.Clamp(fields.Green[i], 0, 1));
                _blue[i] = 1 - (1 - _blue[i]) * (1 - Math.Clamp(fields.Blue[i], 0, 1));
            }
        }

        public void Write(SKBitmap bitmap)
        {
            var pixels = new SkiaBitmapPixelBuffer(bitmap);
            for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
            {
                var i = y * Width + x;
                pixels.Write(x, y, _red[i] * 255, _green[i] * 255, _blue[i] * 255, _alpha[i]);
            }
        }

        internal SKBitmap ToBitmap()
        {
            var bitmap = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            Write(bitmap);
            return bitmap;
        }
    }
}
