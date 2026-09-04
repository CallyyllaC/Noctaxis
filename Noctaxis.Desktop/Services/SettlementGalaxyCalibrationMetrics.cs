using SkiaSharp;

namespace Noctaxis.Desktop.Services;

public sealed record SettlementGalaxyPassMetrics(
    string Pass,
    double PositiveLightCoverage,
    double PositiveLightEnergy,
    double BlurredLuminanceMean,
    double BlurredLuminancePeak,
    double BlurredChromaMean,
    double MainBodyMeanLuminance,
    double MainBodyPeakLuminance,
    double CoreBodyLuminanceRatio,
    double OuterBodyLuminanceRatio,
    double LavenderHueFraction,
    double CyanHueFraction,
    double HighLuminance80Fraction,
    double HighLuminance90Fraction,
    double HighLuminance97Fraction,
    double LuminanceP95,
    double LuminanceP99,
    double LuminanceP997,
    int LargestNearWhiteRegion,
    int NearWhiteRegionCount,
    int RareBrightStarCount,
    double MedianBrightFootprint,
    double StarFieldToBodyEnergy);

/// <summary>
/// Development-only style statistics. They deliberately compare distributions and broad fields,
/// not exact WSF topology or individual star positions.
/// </summary>
public static class SettlementGalaxyCalibrationMetrics
{
    public static SettlementGalaxyPassMetrics Analyse(string pass, SKBitmap image, SKBitmap baseline,
        float[]? density = null, int densityWidth = 0, int densityHeight = 0,
        SKBitmap? previousPass = null)
    {
        if (image.Width != baseline.Width || image.Height != baseline.Height)
            throw new ArgumentException("Calibration image and baseline dimensions must match.");
        var width = image.Width;
        var height = image.Height;
        var count = checked(width * height);
        var luminance = new float[count];
        var positive = new float[count];
        var chroma = new float[count];
        var hue = new float[count];
        var imagePixels = new SkiaBitmapPixelBuffer(image);
        var baselinePixels = new SkiaBitmapPixelBuffer(baseline);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var index = y * width + x;
            var pixel = imagePixels.Read(x, y);
            var before = baselinePixels.Read(x, y);
            luminance[index] = Luminance(pixel);
            positive[index] = Math.Max(0, luminance[index] - Luminance(before));
            RgbToHueChroma(pixel, out hue[index], out chroma[index]);
        }

        var blurSigma = Math.Max(6, Math.Min(width, height) * .055);
        var acceleration = OpenCvMapImageAcceleration.Shared;
        var broadLight = acceleration.GaussianBlur(positive, width, height, blurSigma,
            GaussianBorderMode.Reflect);
        var broadChroma = acceleration.GaussianBlur(chroma, width, height, blurSigma,
            GaussianBorderMode.Reflect);
        var mappedDensity = density is null ? null : SettlementDensityBuilder.ResizeLinear(density,
            densityWidth, densityHeight, width, height);
        var bodyValues = new List<float>();
        var coreValues = new List<float>();
        var outerValues = new List<float>();
        var broadMaximum = Math.Max(broadLight.DefaultIfEmpty().Max(), 1e-9f);
        for (var index = 0; index < count; index++)
        {
            var d = mappedDensity?[index] ?? broadLight[index] / broadMaximum;
            if (d >= .72) coreValues.Add(positive[index]);
            else if (d >= .14) bodyValues.Add(positive[index]);
            else if (d >= .025) outerValues.Add(positive[index]);
        }
        if (bodyValues.Count == 0) bodyValues.AddRange(positive);
        var bodyMean = Mean(bodyValues);
        var lavender = 0;
        var cyan = 0;
        var chromatic = 0;
        var meaningfulLight = 0;
        for (var index = 0; index < count; index++)
        {
            if (positive[index] >= .015f &&
                ((hue[index] is >= 175 and <= 315 && chroma[index] >= .035f) || luminance[index] >= .55f))
                meaningfulLight++;
            if (chroma[index] < .055f || positive[index] < .008f) continue;
            chromatic++;
            if (hue[index] is >= 255 and <= 315) lavender++;
            if (hue[index] is >= 175 and < 255) cyan++;
        }
        var componentSizes = ConnectedRegions(luminance, chroma, width, height);
        var sortedLuminance = luminance.Select(value => (double)value).Order().ToArray();
        var starEnergy = 0d;
        if (previousPass is not null)
        {
            var previousPixels = new SkiaBitmapPixelBuffer(previousPass);
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var index = y * width + x;
                starEnergy += Math.Max(0, luminance[index] - Luminance(previousPixels.Read(x, y)));
            }
        }
        var broadEnergy = broadLight.Sum(value => (double)value);
        return new SettlementGalaxyPassMetrics(pass,
            Ratio(meaningfulLight, count), positive.Sum(value => (double)value),
            broadLight.Average(value => (double)value), broadLight.DefaultIfEmpty().Max(),
            broadChroma.Average(value => (double)value), bodyMean, bodyValues.Max(),
            Ratio(Mean(coreValues), bodyMean), Ratio(Mean(outerValues), bodyMean),
            Ratio(lavender, chromatic), Ratio(cyan, chromatic),
            Fraction(luminance, value => value >= .80f), Fraction(luminance, value => value >= .90f),
            Fraction(luminance, value => value >= .97f), Quantile(sortedLuminance, .95),
            Quantile(sortedLuminance, .99), Quantile(sortedLuminance, .997),
            componentSizes.DefaultIfEmpty().Max(), componentSizes.Count,
            componentSizes.Count(size => size <= 25), Median(componentSizes), Ratio(starEnergy, broadEnergy));
    }

    private static List<int> ConnectedRegions(float[] luminance, float[] chroma, int width, int height)
    {
        var visited = new bool[luminance.Length];
        var queue = new int[luminance.Length];
        var sizes = new List<int>();
        for (var start = 0; start < luminance.Length; start++)
        {
            if (visited[start] || luminance[start] < .86f || chroma[start] > .24f) continue;
            var head = 0;
            var tail = 0;
            queue[tail++] = start;
            visited[start] = true;
            while (head < tail)
            {
                var index = queue[head++];
                var x = index % width;
                var y = index / width;
                Add(x - 1, y); Add(x + 1, y); Add(x, y - 1); Add(x, y + 1);
            }
            sizes.Add(tail);

            void Add(int x, int y)
            {
                if (x < 0 || x >= width || y < 0 || y >= height) return;
                var candidate = y * width + x;
                if (visited[candidate] || luminance[candidate] < .86f || chroma[candidate] > .24f) return;
                visited[candidate] = true;
                queue[tail++] = candidate;
            }
        }
        return sizes;
    }

    private static float Luminance(UnpremultipliedPixel colour) =>
        (float)(.299 * colour.Red / 255d + .587 * colour.Green / 255d + .114 * colour.Blue / 255d);

    private static void RgbToHueChroma(UnpremultipliedPixel colour, out float hue, out float chroma)
    {
        var red = colour.Red / 255f;
        var green = colour.Green / 255f;
        var blue = colour.Blue / 255f;
        var maximum = Math.Max(red, Math.Max(green, blue));
        var minimum = Math.Min(red, Math.Min(green, blue));
        chroma = maximum - minimum;
        if (chroma <= 1e-9f) { hue = 0; return; }
        var raw = maximum == red ? (green - blue) / chroma % 6 :
            maximum == green ? (blue - red) / chroma + 2 : (red - green) / chroma + 4;
        hue = (raw * 60 + 360) % 360;
    }

    private static double Fraction(IEnumerable<float> values, Func<float, bool> predicate)
    {
        var total = 0;
        var matches = 0;
        foreach (var value in values) { total++; if (predicate(value)) matches++; }
        return Ratio(matches, total);
    }

    private static double Mean(IEnumerable<float> values)
    {
        var count = 0;
        var total = 0d;
        foreach (var value in values) { count++; total += value; }
        return count == 0 ? 0 : total / count;
    }

    private static double Ratio(double numerator, double denominator) =>
        denominator <= 1e-12 ? 0 : numerator / denominator;

    private static double Quantile(double[] sorted, double quantile)
    {
        if (sorted.Length == 0) return 0;
        var rank = Math.Clamp(quantile, 0, 1) * (sorted.Length - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (rank - lower);
    }

    private static double Median(List<int> values)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        var middle = values.Count / 2;
        return values.Count % 2 == 0 ? (values[middle - 1] + values[middle]) / 2d : values[middle];
    }
}
