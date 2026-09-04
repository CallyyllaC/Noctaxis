namespace Noctaxis.Desktop.Services;

public sealed record SettlementComponentGeometry(
    int Label, double CentreX, double CentreY, double AngleRadians,
    double MajorRadius, double MinorRadius, double PeakDensity, double RelativeStrength);

public sealed record SettlementGlowGeometry(
    double CentreX,
    double CentreY,
    double AngleRadians,
    double SigmaMajor,
    double SigmaMinor,
    double CoreMajor,
    double CoreMinor,
    double BodyMajor,
    double BodyMinor,
    double HaloMajor,
    double HaloMinor,
    SettlementComponentGeometry[] SubCores,
    int ComponentCellCount)
{
    public SettlementComponentGeometry[] Satellites { get; init; } = [];
    public SettlementComponentGeometry[] MinorComponents { get; init; } = [];
}

/// <summary>Maps the reference density-weighted PCA geometry into thumbnail coordinates.</summary>
public sealed class SettlementGlowGeometryCalculator
{
    public SettlementGlowGeometry? Calculate(SettlementDensityModel density, int outputWidth, int outputHeight) =>
        Calculate(density, outputWidth, outputHeight, SettlementGalaxyStyle.DefaultV1);

    public SettlementGlowGeometry? Calculate(SettlementDensityModel density, int outputWidth, int outputHeight,
        SettlementGalaxyStyle style)
    {
        var component = density.MainComponent;
        if (component is null) return null;

        var transform = ThumbnailTransform.Create(density.Width, density.Height, outputWidth, outputHeight);
        var mappedCentre = transform.Map(component.CentreX, component.CentreY);
        var angle = .5 * Math.Atan2(2 * component.CovarianceXy, component.CovarianceX - component.CovarianceY);
        var (sigmaMajor, sigmaMinor) = PrincipalSigmas(component);
        sigmaMajor *= transform.Scale;
        sigmaMinor *= transform.Scale;
        var hierarchy = style.Galaxy.Hierarchy;
        var halo = ScaledAxes(sigmaMajor, sigmaMinor, hierarchy.HaloRadiusScale,
            hierarchy.HaloAxisRatioClamp);
        var body = ScaledAxes(sigmaMajor, sigmaMinor, hierarchy.BodyRadiusScale,
            hierarchy.BodyAxisRatioClamp);
        var core = ScaledAxes(sigmaMajor, sigmaMinor, hierarchy.CoreRadiusScale,
            hierarchy.CoreAxisRatioClamp);

        var mainStrength = Math.Max(component.Strength, 1e-12);
        var remaining = density.Components.Where(value => value.Label != component.Label)
            .OrderByDescending(value => value.Strength).ThenBy(value => value.Label).ToArray();
        var subCores = remaining.Take(hierarchy.MaxSubcores)
            .Where(value => value.Strength >= mainStrength * hierarchy.SubcoreMinimumStrengthFraction)
            .Select(value => MapComponent(value, mainStrength, transform, hierarchy.SubcoreRadiusScale,
                hierarchy.SubcoreAxisRatioClamp)).ToArray();

        var satelliteSet = density.SatelliteComponentLabels.ToHashSet();
        var satellites = remaining.Where(value => satelliteSet.Contains(value.Label))
            .Select(value => MapComponent(value, mainStrength, transform, style.Satellites.PcaRadiusScale,
                style.Satellites.AxisRatioClamp)).ToArray();
        var minorSet = density.MinorComponentLabels.ToHashSet();
        var minors = remaining.Where(value => minorSet.Contains(value.Label))
            .Select(value => MapComponent(value, mainStrength, transform, style.Satellites.MinorRadiusScale,
                style.Satellites.MinorAxisRatioClamp)).ToArray();

        return new SettlementGlowGeometry(mappedCentre.X, mappedCentre.Y, angle, sigmaMajor, sigmaMinor,
            core.Major, core.Minor, body.Major, body.Minor, halo.Major, halo.Minor,
            subCores, density.PrimaryComponentCellCount) { Satellites = satellites, MinorComponents = minors };
    }

    private static SettlementComponentGeometry MapComponent(SettlementComponent value, double mainStrength,
        ThumbnailTransform transform, double radiusScale, double axisClamp)
    {
        var centre = transform.Map(value.CentreX, value.CentreY);
        var angle = .5 * Math.Atan2(2 * value.CovarianceXy, value.CovarianceX - value.CovarianceY);
        var (majorSigma, minorSigma) = PrincipalSigmas(value);
        var axes = ScaledAxes(majorSigma * transform.Scale, minorSigma * transform.Scale, radiusScale, axisClamp);
        return new SettlementComponentGeometry(value.Label, centre.X, centre.Y, angle, axes.Major, axes.Minor,
            value.PeakDensity, Math.Clamp(value.Strength / mainStrength, 0, 1));
    }

    private static (double Major, double Minor) PrincipalSigmas(SettlementComponent component)
    {
        var trace = component.CovarianceX + component.CovarianceY;
        var discriminant = Math.Sqrt(Math.Max(0, Square(component.CovarianceX - component.CovarianceY) +
                                                      4 * Square(component.CovarianceXy)));
        return (Math.Sqrt(Math.Max(.25, (trace + discriminant) / 2)),
            Math.Sqrt(Math.Max(.25, (trace - discriminant) / 2)));
    }

    private static (double Major, double Minor) ScaledAxes(double major, double minor, double scale,
        double maximumRatio)
    {
        major = Math.Max(1, major * scale);
        minor = Math.Max(1, minor * scale);
        if (minor > major) (major, minor) = (minor, major);
        major = Math.Min(major, minor * maximumRatio);
        return (major, minor);
    }

    public static float[] MaximumFilter(float[] source, int width, int height, int size) =>
        OpenCvMapImageAcceleration.Shared.MaximumFilter(source, width, height, size);

    private static double Square(double value) => value * value;

    internal sealed record ThumbnailTransform(double Left, double Top, double Scale)
    {
        public static ThumbnailTransform Create(int sourceWidth, int sourceHeight, int outputWidth, int outputHeight)
        {
            var targetAspect = outputWidth / (double)outputHeight;
            var sourceAspect = sourceWidth / (double)sourceHeight;
            if (sourceAspect > targetAspect)
            {
                var cropWidth = sourceHeight * targetAspect;
                return new ThumbnailTransform((sourceWidth - cropWidth) / 2, 0, outputWidth / cropWidth);
            }
            var cropHeight = sourceWidth / targetAspect;
            return new ThumbnailTransform(0, (sourceHeight - cropHeight) / 2, outputHeight / cropHeight);
        }

        public MapPixelPoint Map(double x, double y) => new((x - Left) * Scale, (y - Top) * Scale);

        // WSF source rasters are retained at the 2x environmental grid used by the saved-location source cache.
        public MapPixelPoint MapHighResolution(double x, double y) =>
            Map(x / SettlementDensityBuilder.Supersampling, y / SettlementDensityBuilder.Supersampling);
    }
}
