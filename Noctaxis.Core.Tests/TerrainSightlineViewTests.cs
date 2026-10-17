using Noctaxis.Core.Calculations;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Environment;

namespace Noctaxis.Core.Tests;

// PF-03: camera framing reads interpolated sightlines in place. The view must be exactly SightlineAt.
public sealed class TerrainSightlineViewTests
{
    [Theory]
    [InlineData(0d)]
    [InlineData(17d)]
    [InlineData(90.35)]
    [InlineData(123.456789)]
    [InlineData(359.7)]
    [InlineData(-0.25)]
    [InlineData(720.5)]
    public void TheViewIsExactlySightlineAt(double bearing)
    {
        var terrain = PrealphaPerformanceBenchmarks.ProductionShapedProfile();
        var copied = terrain.SightlineAt(bearing);
        var view = terrain.SightlineViewAt(bearing);
        Assert.Equal(copied.Count, view.Count);
        for (var index = 0; index < copied.Count; index++)
        {
            Assert.Equal(copied[index], view[index]);
            Assert.Equal(copied[index].TerrainElevationAngleDegrees, view[index].TerrainElevationAngleDegrees);
        }
        var enumerated = new List<TerrainSightlineSample>();
        foreach (var sample in view) enumerated.Add(sample);
        Assert.Equal(copied, enumerated);
    }

    [Fact]
    public void AProfileBearingIsReadFromTheStoredLine()
    {
        var terrain = PrealphaPerformanceBenchmarks.ProductionShapedProfile();
        Assert.Same(terrain.Samples[42].Sightline, terrain.SightlineViewAt(42).InPlace);
        Assert.Same(terrain.Samples[42].Sightline, terrain.SightlineAt(42));
        Assert.Null(terrain.SightlineViewAt(42.5).InPlace);
    }

    [Fact]
    public void MissingOrMismatchedSightlinesAreEmpty()
    {
        var terrain = PrealphaPerformanceBenchmarks.ProductionShapedProfile();
        var mismatched = terrain with
        {
            Samples = terrain.Samples.Select((sample, index) => index == 11 ? sample with { Sightline = sample.Sightline!.Take(3).ToArray() } : sample).ToArray()
        };
        Assert.Equal(0, mismatched.SightlineViewAt(10.5).Count);
        Assert.Empty(mismatched.SightlineAt(10.5));
        var none = terrain with { Samples = [] };
        Assert.Equal(0, none.SightlineViewAt(10).Count);
        Assert.Empty(none.SightlineAt(10));
        var foreachCount = 0;
        foreach (var _ in default(TerrainSightlineView)) foreachCount++;
        Assert.Equal(0, foreachCount);
    }

    [Fact]
    public void ACameraScanAtAnInterpolatedBearingNoLongerCopiesTheSightline()
    {
        var terrain = PrealphaPerformanceBenchmarks.ProductionShapedProfile();
        var frame = new CameraTerrainFrame(5, 53.1, 5);
        var depth = new double[CameraTerrainDepth.Rows];
        for (var warm = 0; warm < 20; warm++) frame.Scan(terrain, 90.35 + warm, depth);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 50; index++) frame.Scan(terrain, 100.37 + index * 0.41, depth);
        var perScan = (GC.GetAllocatedBytesForCurrentThread() - before) / 50;
        // Copying one 437-sample line was about 40 KB; the scan itself allocates nothing per sample.
        Assert.True(perScan < 1_024, $"{perScan} bytes per scan");
    }

    [Fact]
    public void FramingIsUnchangedByReadingInPlace()
    {
        var terrain = PrealphaPerformanceBenchmarks.ProductionShapedProfile();
        // The same profile with every sightline pre-copied by SightlineAt at the probe bearings would give the
        // same scans: compare a scan against one computed from SightlineAt's copied line.
        var frame = new CameraTerrainFrame(3, 40, 5);
        foreach (var bearing in new[] { 12.3, 90.35, 271.77, 359.9 })
        {
            var depth = new double[CameraTerrainDepth.Rows];
            var scanned = frame.Scan(terrain, bearing, depth);
            var line = terrain.SightlineAt(bearing);
            double? horizon = null, first = null;
            foreach (var point in line)
            {
                if (point.DistanceMetres <= 0 || point.DistanceMetres > LocalHorizonCalculator.MaximumTerrainCastDistanceMetres ||
                    point.TerrainElevationAngleDegrees is not double angle || !double.IsFinite(angle)) continue;
                horizon = horizon.HasValue ? Math.Max(horizon.Value, angle) : angle;
                if (first is null && angle >= frame.ThresholdAltitude) first = point.DistanceMetres;
            }
            Assert.Equal(horizon, scanned.FrameTerrainHorizonDegrees);
            Assert.Equal(scanned.IsObstructed ? first : null, scanned.FirstObstructionDistanceMetres);
        }
    }
}
