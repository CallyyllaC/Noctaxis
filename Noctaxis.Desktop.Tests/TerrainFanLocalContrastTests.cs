using Noctaxis.Desktop.Controls;

namespace Noctaxis.Desktop.Tests;

public sealed class TerrainFanLocalContrastTests
{
    private static PlanTerrainFan Fan(double[] angles)
    {
        var profile = TerrainFanContrastTests.Profile(angles);
        return TerrainFanContrastTests.Fan(profile, 0, 24, 20);
    }

    [Theory]
    [InlineData(TerrainFanLocalContrastMode.None)]
    [InlineData(TerrainFanLocalContrastMode.Bearing)]
    [InlineData(TerrainFanLocalContrastMode.Radial)]
    [InlineData(TerrainFanLocalContrastMode.Combined)]
    public void OutputIsBoundedAndDeterministic(TerrainFanLocalContrastMode mode)
    {
        var fan = Fan([1, 3, 6]);
        var first = fan.Patches.Select((_, i) => TerrainFanLocalContrast.Enhance(fan, i, mode)).ToArray();
        var second = fan.Patches.Select((_, i) => TerrainFanLocalContrast.Enhance(fan, i, mode)).ToArray();
        Assert.Equal(first, second);
        Assert.All(first, value => Assert.InRange(value, 0, 1));
    }

    [Fact]
    public void LocalRidgeIsRaisedRelativeToItsLowerBearingNeighbours()
    {
        var fan = TerrainFanContrastTests.Fan(TerrainFanContrastTests.ProfileByBearing(b =>
            b is >= 89 and <= 91 ? [3d] : [1d]), 0, 24, 20);
        var values = fan.Patches.Select((_, i) => TerrainFanLocalContrast.Enhance(fan, i,
            TerrainFanLocalContrastMode.Bearing)).ToArray();
        if (!(values.Max() > values.Min())) throw new InvalidOperationException(string.Join(",", values.Take(30)));
        Assert.True(values.Max() <= TerrainFanTone.Strength(3) + .1);
        Assert.InRange(values.Min(), TerrainFanTone.Strength(1) - .1, 1);
    }

    [Fact]
    public void ValleyRemainsLowerThanBroadFiveDegreeRidge()
    {
        var broad = TerrainFanContrastTests.Fan(TerrainFanContrastTests.ProfileByBearing(_ => [5d]), 0, 24, 20);
        var valley = TerrainFanContrastTests.Fan(TerrainFanContrastTests.ProfileByBearing(b =>
            b is >= 89 and <= 91 ? [2d] : [5d]), 0, 24, 20);
        var broadValue = broad.Patches.Select((p, i) => TerrainFanLocalContrast.Enhance(broad, i)).Average();
        var valleyValue = valley.Patches.OrderBy(p => p.ApparentAltitudeDegrees).First();
        var valleyStrength = TerrainFanLocalContrast.Enhance(valley, valley.Patches.IndexOf(valleyValue));
        Assert.True(valleyStrength < broadValue);
    }

    [Fact]
    public void NearlyEqualLevelsDoNotBecomeDramaticStripes()
    {
        var fan = Fan([2, 2.1, 2, 2.1]);
        var values = fan.Patches.Select((_, i) => TerrainFanLocalContrast.Enhance(fan, i)).ToArray();
        Assert.InRange(values.Max() - values.Min(), 0, .03);
    }

    [Fact]
    public void StrongMountainRemainsStrongerThanSmallLocalBump()
    {
        var mountain = Fan([8, 9, 10]);
        var bump = Fan([1, 3, 1]);
        var mountainValue = mountain.Patches.Select((p, i) => TerrainFanLocalContrast.Enhance(mountain, i)).Max();
        var bumpValue = bump.Patches.Select((p, i) => TerrainFanLocalContrast.Enhance(bump, i)).Max();
        Assert.True(mountainValue > bumpValue);
        Assert.InRange(mountainValue, 0, 1);
    }

    [Fact]
    public void FlatFanStaysClearAndGeometryMetadataIsUntouched()
    {
        var fan = Fan([0, -.1, 0]);
        Assert.Empty(fan.Patches);
        Assert.All(fan.Rays, ray => Assert.Empty(ray.Bands));
    }

    [Theory]
    [InlineData(72, 50)] [InlineData(24, 20)] [InlineData(4.5, 3)]
    public void WideModerateAndTeleFansRemainValid(double horizontal, double vertical)
    {
        var fan = TerrainFanContrastTests.Fan(TerrainFanContrastTests.Profile([1, 3, 6]), 0, horizontal, vertical);
        Assert.NotEmpty(fan.Rays);
        Assert.All(fan.Patches, patch =>
        {
            var enhanced = TerrainFanLocalContrast.Enhance(fan, fan.Patches.IndexOf(patch));
            Assert.InRange(enhanced, 0, 1);
        });
    }

    [Fact]
    public void DistanceDoesNotDirectlyChangeLocalTone()
    {
        var near = Fan([1, 3, 6]);
        var farProfile = TerrainFanContrastTests.Profile([1, 3, 6]) with
        {
            Samples = TerrainFanContrastTests.Profile([1, 3, 6]).Samples.Select(s => s with
            {
                Sightline = s.Sightline!.Select(p => new Noctaxis.Core.Domain.TerrainSightlineSample(
                    p.DistanceMetres * 3, p.TerrainElevationMetres, p.CurvatureDropMetres,
                    p.TerrainElevationAngleDegrees)).ToArray()
            }).ToArray()
        };
        var far = TerrainFanContrastTests.Fan(farProfile, 0, 24, 20);
        var nearValues = near.Patches.Select((_, i) => TerrainFanLocalContrast.Enhance(near, i)).ToArray();
        var farValues = far.Patches.Select((_, i) => TerrainFanLocalContrast.Enhance(far, i)).ToArray();
        Assert.Equal(nearValues.Length, farValues.Length);
        Assert.All(nearValues.Zip(farValues), pair => Assert.InRange(Math.Abs(pair.First - pair.Second), 0, .05));
    }
}
