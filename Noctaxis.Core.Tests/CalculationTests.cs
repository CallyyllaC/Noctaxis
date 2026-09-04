using Noctaxis.Core.Calculations;
using Noctaxis.Core.Domain;

namespace Noctaxis.Core.Tests;

public sealed class CalculationTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(360, 0)]
    [InlineData(-10, 350)]
    [InlineData(725, 5)]
    public void NormaliseDegrees_UsesZeroTo360Convention(double input, double expected) =>
        Assert.Equal(expected, Angles.NormaliseDegrees(input), 10);

    [Fact]
    public void InitialBearing_IsClockwiseFromTrueNorth()
    {
        var origin = new GeoCoordinate(0, 0);
        Assert.Equal(0, Angles.InitialBearing(origin, new GeoCoordinate(1, 0)), 6);
        Assert.Equal(90, Angles.InitialBearing(origin, new GeoCoordinate(0, 1)), 6);
        Assert.Equal(180, Angles.InitialBearing(origin, new GeoCoordinate(-1, 0)), 6);
    }

    [Fact]
    public void FullFrame50mm_FieldOfViewMatchesOpticalFormula()
    {
        var result = new LensCalculator().Calculate(new LensConfiguration(FocalLengthMillimetres: 50));
        Assert.Equal(39.598, result.HorizontalDegrees, 3);
        Assert.Equal(26.991, result.VerticalDegrees, 3);
        Assert.Equal(46.793, result.DiagonalDegrees, 3);
    }

    [Fact]
    public void PortraitOrientation_SwapsHorizontalAndVerticalField()
    {
        var calculator = new LensCalculator();
        var landscape = calculator.Calculate(new LensConfiguration(FocalLengthMillimetres: 35));
        var portrait = calculator.Calculate(new LensConfiguration(FocalLengthMillimetres: 35, Orientation: CameraOrientation.Portrait));
        Assert.Equal(landscape.HorizontalDegrees, portrait.VerticalDegrees, 10);
        Assert.Equal(landscape.VerticalDegrees, portrait.HorizontalDegrees, 10);
    }

    [Fact]
    public void CameraFramingGuide_UsesPrimaryTargetAndOpticalHorizontalField()
    {
        var fieldOfView = new LensCalculator().Calculate(new LensConfiguration(FocalLengthMillimetres: 50));
        var guide = new CameraFramingGuideCalculator().Calculate(fieldOfView, 90, new CameraFramingSettings());

        Assert.Equal(CameraFramingDirectionSource.PrimaryTarget, guide.DirectionSource);
        Assert.Equal(90, guide.CentreBearingDegrees, 10);
        Assert.Equal(fieldOfView.HorizontalDegrees, guide.HorizontalFieldOfViewDegrees, 10);
        Assert.Equal(90 - fieldOfView.HorizontalDegrees / 2, guide.LeftEdgeBearingDegrees, 10);
        Assert.Equal(90 + fieldOfView.HorizontalDegrees / 2, guide.RightEdgeBearingDegrees, 10);
    }

    [Fact]
    public void CameraFramingGuide_FallsBackToManualBearingAndSupportsCompositionOffset()
    {
        var guide = new CameraFramingGuideCalculator().Calculate(
            new FieldOfView(60, 40, 70),
            null,
            new CameraFramingSettings(ManualBearingDegrees: 350, CompositionOffsetDegrees: 20));

        Assert.Equal(CameraFramingDirectionSource.ManualBearing, guide.DirectionSource);
        Assert.Equal(10, guide.CentreBearingDegrees, 10);
        Assert.Equal(340, guide.LeftEdgeBearingDegrees, 10);
        Assert.Equal(40, guide.RightEdgeBearingDegrees, 10);
    }

    [Fact]
    public void CameraFramingAppearanceSettings_AreClampedForSafeRendering()
    {
        var settings = new CameraFramingSettings(ShadingOpacityPercent: 120, LineThickness: .1,
            TerrainCastAngularDetailDegrees: 100).Normalised();
        Assert.Equal(50, settings.ShadingOpacityPercent);
        Assert.Equal(.5, settings.LineThickness);
        Assert.Equal(45, settings.TerrainCastAngularDetailDegrees);
        Assert.Equal(10, new CameraFramingSettings().TerrainCastAngularDetailDegrees);
    }
}
