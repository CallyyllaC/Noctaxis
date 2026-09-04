using Noctaxis.Desktop.Services;

namespace Noctaxis.Desktop.Tests;

public sealed class SettlementGalaxyReferenceFidelityTests
{
    [Fact]
    public void Pass01_UsesReferencePcaScalesAxisClamps_AndRealComponentSubcores()
    {
        const int width = 100;
        const int height = 100;
        var labels = new int[width * height];
        var primary = new bool[labels.Length];
        var main = new SettlementComponent(1, 100, 40, 1, 50, 50, 100, 25, 0,
            20, 20, 80, 80, 10);
        var subordinate = new SettlementComponent(2, 20, 5, .5f, 15, 18, 16, 4, 0,
            8, 10, 22, 26, 1);
        var model = new SettlementDensityModel(width, height, new float[labels.Length],
            new float[labels.Length], primary, labels, [main, subordinate], 1, [2], [], 120, 120, 1, 100);

        var geometry = new SettlementGlowGeometryCalculator().Calculate(model, width, height,
            SettlementGalaxyStyle.DefaultV1)!;

        Assert.Equal(16.5, geometry.HaloMinor, 5);
        Assert.Equal(16.5 * 1.4, geometry.HaloMajor, 5);
        Assert.Single(geometry.SubCores);
        Assert.Equal(2, geometry.SubCores[0].Label);
        Assert.Equal(.1, geometry.SubCores[0].RelativeStrength, 6);
    }

    [Fact]
    public void GaussianBlur_DefaultReflectsReferenceBoundary_AndPass12CanRequestConstantZero()
    {
        var source = new float[25];
        source[0] = 1;
        var acceleration = new ManagedMapImageAcceleration();

        var reflected = acceleration.GaussianBlur(source, 5, 5, 1, GaussianBorderMode.Reflect);
        var constant = acceleration.GaussianBlur(source, 5, 5, 1, GaussianBorderMode.ConstantZero);

        Assert.True(reflected[0] > constant[0]);
        Assert.True(reflected.Sum() > constant.Sum());
    }

    [Fact]
    public void Pass02_UsesSelectedFourZoneWeights_Strength_AndPreservesLuminance()
    {
        var style = SettlementGalaxyStyle.DefaultV1.Galaxy.ColourZoning;
        var fields = SettlementGalaxyPassMath.BuildColourZoning([.55f], style);
        var source = new RgbFloat(.41, .41, .41);

        var unchanged = SettlementGalaxyPassMath.ReplaceChroma(source,
            fields.Target[0], fields.Alpha[0], 0);
        var coloured = SettlementGalaxyPassMath.ReplaceChroma(source,
            fields.Target[0], fields.Alpha[0], style.Strength);

        Assert.Equal(source, unchanged);
        Assert.Equal(SettlementGalaxyPassMath.Luminance(source),
            SettlementGalaxyPassMath.Luminance(coloured), 5);
        Assert.NotEqual(source, coloured);
        Assert.True(fields.Alpha[0] > 0);
    }

    [Fact]
    public void Pass03_EverySelectedParameterChangesItsEmissionField()
    {
        const int width = 65;
        const int height = 1;
        var density = Enumerable.Range(0, width).Select(index => index / (float)(width - 1)).ToArray();
        var baseline = SettlementGalaxyStyle.DefaultV1.Galaxy.Luminance;
        var acceleration = new ManagedMapImageAcceleration();
        var baselineHash = Hash(SettlementGalaxyPassMath.BuildLuminosityEmission(
            density, width, height, baseline, acceleration));
        var variants = new[]
        {
            baseline with { BroadGamma = baseline.BroadGamma + .01 },
            baseline with { BroadGain = baseline.BroadGain + .01 },
            baseline with { DenseGamma = baseline.DenseGamma + .01 },
            baseline with { DenseGain = baseline.DenseGain + .01 },
            baseline with { KnotGamma = baseline.KnotGamma + .01 },
            baseline with { KnotGain = baseline.KnotGain + .01 },
            baseline with { CoreThreshold = baseline.CoreThreshold - .01 },
            baseline with { CoreFull = baseline.CoreFull - .01 },
            baseline with { CoreGamma = baseline.CoreGamma + .01 },
            baseline with { CoreGain = baseline.CoreGain + .01 },
            baseline with { BloomGain = baseline.BloomGain + .01 },
            baseline with { BloomRadius = baseline.BloomRadius + .25 },
            baseline with { HotRadius = baseline.HotRadius + .25 },
            baseline with { SoftClip = 0 }
        };

        Assert.All(variants, variant => Assert.NotEqual(baselineHash,
            Hash(SettlementGalaxyPassMath.BuildLuminosityEmission(
                density, width, height, variant, acceleration))));
    }

    [Fact]
    public void Pass04_MergesOnlyTheTwoStrongestNearbyDensityMaxima()
    {
        const int width = 96;
        const int height = 48;
        var density = new float[width * height];
        density[20 * width + 20] = .9f;
        density[20 * width + 34] = .8f;
        density[20 * width + 86] = .7f;

        var core = SettlementGalaxyPassMath.FindHeroCore(density, width, height, mergeDistance: 56);

        Assert.NotNull(core);
        Assert.Equal(2, core.MergedPeakCount);
        Assert.Equal((20 * .9 + 34 * .8) / 1.7, core.X, 4);
        Assert.Equal(20, core.Y, 4);
    }

    [Fact]
    public void Pass07_UsesOneSharedPercentileNormalisedImpulseField()
    {
        var stars = new[]
        {
            Star(20, 20, .4f, .34f, SettlementStarClass.Faint, 1),
            Star(20, 20, .8f, .58f, SettlementStarClass.Common, 2),
            Star(21, 20, .7f, 1f, SettlementStarClass.Bright, 3)
        };
        var fields = SettlementGalaxyPassMath.BuildStarHierarchyFields(stars, 48, 40,
            SettlementGalaxyStyle.DefaultV1.Stars, new ManagedMapImageAcceleration());

        Assert.Equal(.34f * .4f + .58f * .8f, fields.CoreImpulses[20 * 48 + 20], 5);
        Assert.Equal(1, fields.StarCore.Max(), 5);
        Assert.True(fields.StarCore.Count(value => value >= .999f) < 8);
        Assert.True(fields.Bloom.Max() <= 1);
    }

    [Fact]
    public void Pass10_GalaxySuppressionMatchesReferencePowerLaw()
    {
        var suppression = SettlementGalaxyPassMath.BuildGalaxySuppression([0f, .25f, 1f], .55);

        Assert.Equal(1, suppression[0], 6);
        Assert.Equal(1 - Math.Pow(.25, .55), suppression[1], 6);
        Assert.Equal(0, suppression[2], 6);
    }

    [Fact]
    public void Pass09_SelectedSatelliteShapeExtendsBeyondLiteralDensity()
    {
        var style = SettlementGalaxyStyle.DefaultV1.Satellites;
        const double ellipse = .6;
        const double zeroDensity = 0;
        var shaped = ellipse * (style.ShapedDensityFloor +
            style.ShapedDensityWeight * Math.Sqrt(zeroDensity));

        Assert.Equal(.27, shaped, 8);
        Assert.True(shaped > 0);
    }

    [Fact]
    public void Pass12_OuterFalloffRetainsPoweredHaloFactor()
    {
        const int width = 31;
        const int height = 1;
        var density = new float[width * height];
        density[width / 2] = 1;
        var style = SettlementGalaxyStyle.DefaultV1;
        var acceleration = new ManagedMapImageAcceleration();

        var envelope = SettlementGalaxyPassMath.NormaliseMaximum(acceleration.GaussianBlur(
            density, width, height, style.OuterFalloff.EnvelopeSigma, GaussianBorderMode.Reflect));
        var halo = SettlementDensityBuilder.Normalise(acceleration.GaussianBlur(envelope, width, height,
            style.OuterFalloff.OuterHaloRadius, GaussianBorderMode.ConstantZero),
            style.OuterFalloff.NormalisePercentile);
        var actual = new SettlementGlowCompositor(acceleration)
            .BuildOuterFalloffFields(density, width, height, style);
        var sample = 0;
        var powered = Math.Pow(halo[sample], style.OuterFalloff.FalloffGamma);
        var inner = SettlementGalaxyPassMath.SmoothStep(style.OuterFalloff.InnerPresenceStart,
            style.OuterFalloff.InnerPresenceFull, envelope[sample]);
        var outerWeight = style.OuterFalloff.OuterBaseWeight +
                          style.OuterFalloff.OuterAbsenceWeight * (1 - inner);
        var expectedFalloff = powered * (style.OuterFalloff.MinimumOpacity +
            (1 - style.OuterFalloff.MinimumOpacity) * powered) * outerWeight;
        var expectedMid = expectedFalloff *
            Math.Pow(envelope[sample], style.OuterFalloff.MidDensityExponent);

        Assert.InRange(halo[sample], .0001f, .9999f);
        Assert.Equal(expectedFalloff, actual.Falloff[sample], 6);
        Assert.Equal(expectedMid, actual.Mid[sample], 6);
    }

    [Fact]
    public void Pass13_LuminanceEquationMatchesPythonReferenceLiterally()
    {
        var style = SettlementGalaxyStyle.DefaultV1.Tonemapping;
        const double input = .84;
        const double local = .73;

        var actual = SettlementGalaxyPassMath.ToneMapLuminance(input, local, style);
        var shoulder = SmoothStep(style.HighlightThreshold, 1, input);
        var y1 = input - shoulder * style.HighlightCompression *
            (input - style.HighlightThreshold) * (1 - input);
        var detail = Math.Max(y1 - local, 0);
        var detailWeight = 1 - .75 * SmoothStep(.76, 1, y1);
        var y2 = Math.Clamp(y1 + detail * style.LocalPositiveLightContrast * detailWeight, 0, 1);
        const double pivot = .42;
        var centred = y2 - pivot;
        var curved = Math.Clamp(y2 + centred * (1 - Math.Abs(centred) / .58) *
            style.GlobalCurveStrength, 0, 1);
        var toeProtect = 1 - SmoothStep(.015, .10, y2);
        var expected = curved * (1 - toeProtect) + y2 * toeProtect;

        Assert.Equal(expected, actual, 8);
    }

    private static SettlementStar Star(float x, float y, float radius, float brightness,
        SettlementStarClass starClass, ulong seed) =>
        new(x, y, radius, brightness, 240, 242, 255, starClass, seed);

    private static string Hash(SettlementEmissionFields fields) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(
            fields.Red.SelectMany(BitConverter.GetBytes)
                .Concat(fields.Green.SelectMany(BitConverter.GetBytes))
                .Concat(fields.Blue.SelectMany(BitConverter.GetBytes)).ToArray()));

    private static double SmoothStep(double a, double b, double value)
    {
        var t = Math.Clamp((value - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
