namespace Noctaxis.Desktop.Services;

internal readonly record struct RgbFloat(double Red, double Green, double Blue);
internal sealed record SettlementColourZoningFields(RgbFloat[] Target, float[] Alpha);
internal sealed record SettlementEmissionFields(float[] Red, float[] Green, float[] Blue);
internal sealed record SettlementStarHierarchyFields(
    float[] CoreImpulses, float[] BrightImpulses, float[] StarCore, float[] Bloom);
internal sealed record SettlementHeroCore(double X, double Y, int MergedPeakCount);

/// <summary>Literal, testable equations shared by the authoritative Python-pass port.</summary>
internal static class SettlementGalaxyPassMath
{
    public static SettlementColourZoningFields BuildColourZoning(float[] density,
        GalaxyColourZoningStyle style)
    {
        var target = new RgbFloat[density.Length];
        var alpha = new float[density.Length];
        var t = style.Thresholds;
        for (var index = 0; index < density.Length; index++)
        {
            var d = Math.Clamp(density[index], 0, 1);
            var outer = SmoothStep(t.OuterStart, t.OuterFull, d) *
                        (1 - SmoothStep(t.OuterFadeStart, t.OuterFadeEnd, d));
            var body = SmoothStep(t.BodyStart, t.BodyFull, d) *
                       (1 - SmoothStep(t.BodyFadeStart, t.BodyFadeEnd, d));
            var dense = SmoothStep(t.DenseStart, t.DenseFull, d) *
                        (1 - SmoothStep(t.DenseFadeStart, t.DenseFadeEnd, d));
            var core = SmoothStep(t.CoreStart, t.CoreFull, d);
            var total = Math.Max(outer + body + dense + core, 1e-8);
            target[index] = new RgbFloat(
                (outer * style.Outer[0] + body * style.Body[0] + dense * style.Dense[0] + core * style.Core[0]) /
                (255 * total),
                (outer * style.Outer[1] + body * style.Body[1] + dense * style.Dense[1] + core * style.Core[1]) /
                (255 * total),
                (outer * style.Outer[2] + body * style.Body[2] + dense * style.Dense[2] + core * style.Core[2]) /
                (255 * total));
            alpha[index] = (float)Math.Clamp(outer + body + dense + core, 0, 1);
        }
        return new SettlementColourZoningFields(target, alpha);
    }

    public static SettlementEmissionFields BuildLuminosityEmission(float[] density, int width, int height,
        GalaxyLuminanceStyle style, IMapImageAcceleration acceleration)
    {
        var coreGate = new float[density.Length];
        var broadDenseKnots = new float[density.Length];
        var core = new float[density.Length];
        for (var index = 0; index < density.Length; index++)
        {
            var d = Math.Clamp(density[index], 0, 1);
            var broad = Math.Pow(d, style.BroadGamma) * style.BroadGain;
            var dense = Math.Pow(d, style.DenseGamma) * style.DenseGain;
            var knots = Math.Pow(d, style.KnotGamma) * style.KnotGain;
            coreGate[index] = (float)SmoothStep(style.CoreThreshold, style.CoreFull, d);
            core[index] = (float)(Math.Pow(coreGate[index], style.CoreGamma) * style.CoreGain);
            broadDenseKnots[index] = (float)(broad + dense + knots);
        }
        var bloom = NormaliseMaximum(acceleration.GaussianBlur(coreGate, width, height, style.BloomRadius));
        var hot = NormaliseMaximum(acceleration.GaussianBlur(coreGate, width, height, style.HotRadius));
        var red = new float[density.Length];
        var green = new float[density.Length];
        var blue = new float[density.Length];
        for (var index = 0; index < density.Length; index++)
        {
            red[index] = CompressEmission(style.LavenderColour[0] / 255d * broadDenseKnots[index] +
                style.PaleColour[0] / 255d * bloom[index] * style.BloomGain +
                style.WarmColour[0] / 255d * hot[index] * core[index], style);
            green[index] = CompressEmission(style.LavenderColour[1] / 255d * broadDenseKnots[index] +
                style.PaleColour[1] / 255d * bloom[index] * style.BloomGain +
                style.WarmColour[1] / 255d * hot[index] * core[index], style);
            blue[index] = CompressEmission(style.LavenderColour[2] / 255d * broadDenseKnots[index] +
                style.PaleColour[2] / 255d * bloom[index] * style.BloomGain +
                style.WarmColour[2] / 255d * hot[index] * core[index], style);
        }
        return new SettlementEmissionFields(red, green, blue);
    }

    public static SettlementHeroCore? FindHeroCore(float[] density, int width, int height,
        double mergeDistance, int filterSize = 17, double threshold = .40,
        int maximumPeakCount = 7, double minimumDistance = 12)
    {
        var maximum = SettlementGlowGeometryCalculator.MaximumFilter(density, width, height, filterSize);
        var peaks = new List<(int X, int Y, float Strength)>();
        for (var index = 0; index < density.Length; index++)
            if (density[index] >= threshold && density[index] == maximum[index])
                peaks.Add((index % width, index / width, density[index]));
        peaks.Sort(static (left, right) =>
        {
            var comparison = right.Strength.CompareTo(left.Strength);
            if (comparison != 0) return comparison;
            comparison = left.Y.CompareTo(right.Y);
            return comparison != 0 ? comparison : left.X.CompareTo(right.X);
        });
        var picked = new List<(int X, int Y, float Strength)>();
        foreach (var peak in peaks)
        {
            if (picked.Any(other => Square(peak.X - other.X) + Square(peak.Y - other.Y) <
                                    Square(minimumDistance))) continue;
            picked.Add(peak);
            if (picked.Count == maximumPeakCount) break;
        }
        if (picked.Count == 0) return null;
        var chosen = picked.Take(1).ToList();
        if (picked.Count > 1 && Math.Sqrt(Square(picked[0].X - picked[1].X) +
                                          Square(picked[0].Y - picked[1].Y)) <= mergeDistance)
            chosen.Add(picked[1]);
        var total = chosen.Sum(value => value.Strength);
        return new SettlementHeroCore(chosen.Sum(value => value.X * value.Strength) / total,
            chosen.Sum(value => value.Y * value.Strength) / total, chosen.Count);
    }

    public static SettlementStarHierarchyFields BuildStarHierarchyFields(IReadOnlyList<SettlementStar> stars,
        int width, int height, StarStyle style, IMapImageAcceleration acceleration)
    {
        var core = new float[width * height];
        var bright = new float[width * height];
        foreach (var star in stars)
        {
            var x = Math.Clamp((int)Math.Round(star.X), 0, width - 1);
            var y = Math.Clamp((int)Math.Round(star.Y), 0, height - 1);
            var index = y * width + x;
            core[index] += star.Brightness * Math.Max(star.Radius, (float)style.MinimumImpulseRadius);
            if (star.Class == SettlementStarClass.Bright) bright[index] += star.Brightness;
        }
        var starCore = SettlementDensityBuilder.Normalise(
            acceleration.GaussianBlur(core, width, height, style.CoreSigma), style.NormalisePercentile);
        var bloom = NormaliseMaximum(acceleration.GaussianBlur(bright, width, height, style.BrightBloomRadius));
        return new SettlementStarHierarchyFields(core, bright, starCore, bloom);
    }

    public static float[] BuildGalaxySuppression(float[] density, double exponent)
    {
        var output = new float[density.Length];
        for (var index = 0; index < density.Length; index++)
            output[index] = (float)Math.Clamp(1 - Math.Pow(Math.Clamp(density[index], 0, 1), exponent), 0, 1);
        return output;
    }

    public static RgbFloat ReplaceChroma(RgbFloat source, RgbFloat target, double alpha,
        double chromaStrength)
    {
        RgbToYuv(source, out var y, out var u, out var v);
        RgbToYuv(target, out _, out var targetU, out var targetV);
        var amount = Math.Clamp(alpha * chromaStrength, 0, 1);
        if (amount <= 0) return source;
        return YuvToRgb(y, u * (1 - amount) + targetU * amount, v * (1 - amount) + targetV * amount);
    }

    public static double ToneMapLuminance(double input, double local, TonemappingStyle style)
    {
        var shoulder = SmoothStep(style.HighlightThreshold, 1, input);
        var y1 = Math.Clamp(input - shoulder * style.HighlightCompression *
            (input - style.HighlightThreshold) * (1 - input), 0, 1);
        var positiveDetail = Math.Max(y1 - local, 0);
        var detailWeight = 1 - style.DetailSuppressionAmount *
            SmoothStep(style.DetailSuppressionStart, style.DetailSuppressionEnd, y1);
        var y2 = Math.Clamp(y1 + positiveDetail * style.LocalPositiveLightContrast * detailWeight, 0, 1);
        var centred = y2 - style.CurvePivot;
        var span = Math.Max(style.CurvePivot, 1 - style.CurvePivot);
        var curved = Math.Clamp(y2 + centred * (1 - Math.Abs(centred) / span) *
            style.GlobalCurveStrength, 0, 1);
        var toeProtect = 1 - SmoothStep(style.ToeStart, style.ToeEnd, y2);
        return curved * (1 - toeProtect) + y2 * toeProtect;
    }

    public static double Luminance(RgbFloat value) =>
        .299 * value.Red + .587 * value.Green + .114 * value.Blue;

    public static double SmoothStep(double edge0, double edge1, double value)
    {
        var t = Math.Clamp((value - edge0) / Math.Max(edge1 - edge0, 1e-12), 0, 1);
        return t * t * (3 - 2 * t);
    }

    public static float[] NormaliseMaximum(float[] values)
    {
        var maximum = values.Length == 0 ? 0 : values.Max();
        if (maximum <= 1e-9f) return new float[values.Length];
        var output = new float[values.Length];
        for (var index = 0; index < values.Length; index++)
            output[index] = Math.Clamp(values[index] / maximum, 0, 1);
        return output;
    }

    private static float CompressEmission(double value, GalaxyLuminanceStyle style) =>
        (float)(value / (1 + Math.Max(value - style.SoftClip, 0) * style.SoftClipSlope));

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

    private static double Square(double value) => value * value;
}
