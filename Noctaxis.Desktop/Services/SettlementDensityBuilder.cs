using Noctaxis.Core.Environment;

namespace Noctaxis.Desktop.Services;

public sealed record SettlementComponent(
    int Label,
    int CellCount,
    double IntegratedDensity,
    float PeakDensity,
    double CentreX,
    double CentreY,
    double CovarianceX,
    double CovarianceY,
    double CovarianceXy,
    int MinX,
    int MinY,
    int MaxX,
    int MaxY,
    double Strength);

public sealed record SettlementDensityModel(
    int Width,
    int Height,
    float[] Density,
    float[] BodyField,
    bool[] PrimaryComponent,
    int[] ComponentLabels,
    SettlementComponent[] Components,
    int MainComponentLabel,
    int[] SatelliteComponentLabels,
    int[] MinorComponentLabels,
    int ActiveSettlementCellCount,
    int DensityCellCount,
    float MaximumDensity,
    int PrimaryComponentCellCount)
{
    public bool HasPrimaryComponent => MainComponentLabel > 0 && PrimaryComponentCellCount > 0;
    public SettlementComponent? MainComponent => Components.FirstOrDefault(value => value.Label == MainComponentLabel);
}

/// <summary>Builds a deterministic, continuous supersampled settlement field from WSF fraction cells.</summary>
public sealed class SettlementDensityBuilder
{
    public const int Supersampling = 2;
    private readonly IMapImageAcceleration _acceleration;

    public SettlementDensityBuilder(IMapImageAcceleration? acceleration = null) =>
        _acceleration = acceleration ?? OpenCvMapImageAcceleration.Shared;

    public SettlementDensityModel Build(SettlementRaster settlement) =>
        Build(settlement, Math.Max(1, settlement.Grid.Width / Supersampling),
            Math.Max(1, settlement.Grid.Height / Supersampling), SettlementGalaxyStyle.DefaultV1);

    public SettlementDensityModel Build(SettlementRaster settlement, SettlementGalaxyStyle style)
        => Build(settlement, Math.Max(1, settlement.Grid.Width / Supersampling),
            Math.Max(1, settlement.Grid.Height / Supersampling), style);

    public SettlementDensityModel Build(SettlementRaster settlement, int outputWidth, int outputHeight,
        SettlementGalaxyStyle style)
    {
        if (outputWidth <= 0) throw new ArgumentOutOfRangeException(nameof(outputWidth));
        if (outputHeight <= 0) throw new ArgumentOutOfRangeException(nameof(outputHeight));
        var expected = checked(settlement.Grid.Width * settlement.Grid.Height);
        if (settlement.BuildingFraction.Length != expected || settlement.BuildingHeightMetres.Length != expected)
            throw new InvalidDataException("WSF settlement arrays do not match their grid dimensions.");

        var sourceMass = new float[expected];
        Parallel.For(0, expected, index => sourceMass[index] = SettlementMass(
            settlement.BuildingFraction[index], settlement.BuildingHeightMetres[index]));
        var activeCells = 0;
        var maximumDensity = 0f;
        for (var index = 0; index < sourceMass.Length; index++)
        {
            if (sourceMass[index] > 0) activeCells++;
            if (sourceMass[index] > maximumDensity) maximumDensity = sourceMass[index];
        }
        var outputCount = checked(outputWidth * outputHeight);
        if (activeCells == 0)
            return new SettlementDensityModel(outputWidth, outputHeight, new float[outputCount],
                new float[outputCount], new bool[outputCount], new int[outputCount], [], 0, [], [],
                0, 0, 0, 0);

        // Python foundation: first form the continuous WSF mass at exactly 2x thumbnail resolution,
        // then sigma-7.5 blur, p99.65 normalisation and bilinear reduction to final pass resolution.
        var workingScale = Math.Max(1, style.Density.WorkingScale);
        var workingWidth = checked(outputWidth * workingScale);
        var workingHeight = checked(outputHeight * workingScale);
        var workingMass = ResampleToWorkingField(sourceMass, settlement.Grid.Width, settlement.Grid.Height,
            workingWidth, workingHeight);
        var workingDensity = Normalise(_acceleration.GaussianBlur(workingMass, workingWidth, workingHeight,
            style.Density.GaussianSigma, GaussianBorderMode.Reflect), style.Density.NormalisePercentile);
        var body = ResizeLinear(workingDensity, workingWidth, workingHeight, outputWidth, outputHeight);
        for (var index = 0; index < body.Length; index++) body[index] = Math.Clamp(body[index], 0, 1);
        var threshold = new bool[body.Length];
        var componentThreshold = (float)style.Satellites.ComponentThreshold;
        Parallel.For(0, body.Length, index => threshold[index] = body[index] >= componentThreshold);
        var (labels, components) = LabelComponents(threshold, body, outputWidth, outputHeight, style.Satellites);
        var mainLabel = SelectMainComponentLabel(labels, components, outputWidth, outputHeight);
        var primary = new bool[outputCount];
        var primaryCount = 0;
        if (mainLabel > 0)
        {
            for (var index = 0; index < labels.Length; index++)
                if (labels[index] == mainLabel) { primary[index] = true; primaryCount++; }
        }

        var mainStrength = components.FirstOrDefault(value => value.Label == mainLabel)?.Strength ?? 0;
        var remaining = components.Where(value => value.Label != mainLabel)
            .OrderByDescending(value => value.Strength).ThenBy(value => value.Label).ToArray();
        var meaningful = remaining.Where(value => mainStrength <= 0 ||
            value.Strength >= mainStrength * style.Satellites.MinimumMeaningfulStrengthFraction).ToArray();
        var satellites = meaningful.Take(style.Satellites.SatelliteCount).Select(value => value.Label).ToArray();
        var satelliteSet = satellites.ToHashSet();
        var minors = meaningful.Where(value => !satelliteSet.Contains(value.Label))
            .Select(value => value.Label).ToArray();

        return new SettlementDensityModel(outputWidth, outputHeight, body, body, primary, labels, components,
            mainLabel, satellites, minors, activeCells, activeCells, maximumDensity, primaryCount);
    }

    /// <summary>WSF height has deliberately bounded, mild influence over building-fraction mass.</summary>
    public static float SettlementMass(float buildingFraction, float buildingHeightMetres)
    {
        var fraction = Math.Clamp(buildingFraction, 0, 1);
        var boundedHeight = Math.Clamp(buildingHeightMetres, 0, 100);
        var heightFactor = 1 + .30 * Math.Log(1 + boundedHeight) / Math.Log(101);
        return (float)(fraction * heightFactor);
    }

    public static (int[] Labels, SettlementComponent[] Components) LabelComponents(bool[] source,
        float[] density, int width, int height, SatelliteStyle style)
    {
        var labels = new int[source.Length];
        var queue = new int[source.Length];
        var components = new List<SettlementComponent>();
        var nextLabel = 0;
        for (var start = 0; start < source.Length; start++)
        {
            if (!source[start] || labels[start] != 0) continue;
            nextLabel++;
            var head = 0;
            var tail = 0;
            labels[start] = nextLabel;
            queue[tail++] = start;
            double weight = 0, weightedX = 0, weightedY = 0;
            var peak = 0f;
            var minX = width; var minY = height; var maxX = 0; var maxY = 0;
            while (head < tail)
            {
                var index = queue[head++];
                var x = index % width;
                var y = index / width;
                var sample = Math.Max(1e-9f, density[index]);
                weight += sample;
                weightedX += x * sample;
                weightedY += y * sample;
                peak = Math.Max(peak, density[index]);
                minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
                Add(x - 1, y); Add(x + 1, y); Add(x, y - 1); Add(x, y + 1);
            }
            var centreX = weightedX / weight;
            var centreY = weightedY / weight;
            double covarianceX = 0, covarianceY = 0, covarianceXy = 0;
            for (var cursor = 0; cursor < tail; cursor++)
            {
                var index = queue[cursor];
                var x = index % width;
                var y = index / width;
                var normalised = Math.Max(1e-9f, density[index]) / weight;
                var dx = x - centreX; var dy = y - centreY;
                covarianceX += normalised * dx * dx;
                covarianceY += normalised * dy * dy;
                covarianceXy += normalised * dx * dy;
            }
            var strength = Math.Pow(weight, style.Ranking.IntegratedDensityExponent) *
                           Math.Pow(Math.Max(peak, 1e-9), style.Ranking.PeakDensityExponent);
            components.Add(new SettlementComponent(nextLabel, tail, weight, peak, centreX, centreY,
                covarianceX, covarianceY, covarianceXy, minX, minY, maxX, maxY, strength));

            void Add(int x, int y)
            {
                if (x < 0 || x >= width || y < 0 || y >= height) return;
                var candidate = y * width + x;
                if (!source[candidate] || labels[candidate] != 0) return;
                labels[candidate] = nextLabel;
                queue[tail++] = candidate;
            }
        }
        return (labels, components.ToArray());
    }

    public static int SelectMainComponentLabel(int[] labels, IReadOnlyList<SettlementComponent> components,
        int width, int height)
    {
        if (components.Count == 0) return 0;
        return components.OrderByDescending(value => value.Strength).ThenBy(value => value.Label).First().Label;
    }

    public static bool[] SelectPinComponent(bool[] source, int width, int height, out int count)
    {
        var density = source.Select(value => value ? 1f : 0f).ToArray();
        var style = SettlementGalaxyStyle.DefaultV1.Satellites;
        var (labels, components) = LabelComponents(source, density, width, height, style);
        var label = SelectMainComponentLabel(labels, components, width, height);
        var selected = new bool[source.Length];
        count = 0;
        for (var index = 0; index < labels.Length; index++)
            if (labels[index] == label) { selected[index] = true; count++; }
        return selected;
    }

    public static float[] GaussianBlur(float[] source, int width, int height, double sigma)
        => OpenCvMapImageAcceleration.Shared.GaussianBlur(source, width, height, sigma);

    private static float[] ResampleToWorkingField(float[] source, int sourceWidth, int sourceHeight,
        int workingWidth, int workingHeight)
    {
        var transform = SettlementGlowGeometryCalculator.ThumbnailTransform.Create(
            sourceWidth, sourceHeight, workingWidth, workingHeight);
        var output = new float[workingWidth * workingHeight];
        Parallel.For(0, workingHeight, y =>
        {
            for (var x = 0; x < workingWidth; x++)
            {
                var sourceX = x / transform.Scale + transform.Left;
                var sourceY = y / transform.Scale + transform.Top;
                output[y * workingWidth + x] = SampleLinear(source, sourceWidth, sourceHeight, sourceX, sourceY);
            }
        });
        return output;
    }

    internal static float[] ResizeLinear(float[] source, int sourceWidth, int sourceHeight,
        int outputWidth, int outputHeight)
    {
        var output = new float[outputWidth * outputHeight];
        var scaleX = outputWidth <= 1 ? 0 : (sourceWidth - 1d) / (outputWidth - 1d);
        var scaleY = outputHeight <= 1 ? 0 : (sourceHeight - 1d) / (outputHeight - 1d);
        Parallel.For(0, outputHeight, y =>
        {
            for (var x = 0; x < outputWidth; x++)
                output[y * outputWidth + x] = SampleLinear(source, sourceWidth, sourceHeight,
                    x * scaleX, y * scaleY);
        });
        return output;
    }

    internal static float SampleLinear(float[] source, int width, int height, double x, double y)
    {
        x = Math.Clamp(x, 0, width - 1);
        y = Math.Clamp(y, 0, height - 1);
        var x0 = (int)Math.Floor(x); var y0 = (int)Math.Floor(y);
        var x1 = Math.Min(width - 1, x0 + 1); var y1 = Math.Min(height - 1, y0 + 1);
        var tx = x - x0; var ty = y - y0;
        var top = source[y0 * width + x0] * (1 - tx) + source[y0 * width + x1] * tx;
        var bottom = source[y1 * width + x0] * (1 - tx) + source[y1 * width + x1] * tx;
        return (float)(top * (1 - ty) + bottom * ty);
    }

    public static float[] Normalise(float[] source, double percentile)
    {
        var positiveCount = 0;
        for (var index = 0; index < source.Length; index++)
            if (source[index] > 0 && float.IsFinite(source[index])) positiveCount++;
        var output = new float[source.Length];
        if (positiveCount == 0) return output;
        var sorted = new float[positiveCount];
        var cursor = 0;
        for (var index = 0; index < source.Length; index++)
            if (source[index] > 0 && float.IsFinite(source[index])) sorted[cursor++] = source[index];
        Array.Sort(sorted);
        var rank = Math.Clamp(percentile, 0, 100) / 100d * (sorted.Length - 1);
        var lower = (int)Math.Floor(rank); var upper = (int)Math.Ceiling(rank);
        var scale = sorted[lower] + (sorted[upper] - sorted[lower]) * (float)(rank - lower);
        if (scale <= 1e-12f) scale = sorted[^1];
        if (scale <= 1e-12f) return output;
        Parallel.For(0, source.Length, index => output[index] = Math.Clamp(source[index] / scale, 0, 1));
        return output;
    }

    private static double Square(double value) => value * value;
}
