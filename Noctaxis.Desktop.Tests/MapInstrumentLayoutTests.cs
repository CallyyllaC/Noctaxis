using Avalonia;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// The arithmetic that sizes the map's two instruments together: preferences as shares of the map's width,
/// each kept between its minimum and a third of the map, fitted so the PiP and the Camera frame row never
/// overlap and never leave the map, and never written back into the preferences.
/// </summary>
public sealed class MapInstrumentLayoutTests
{
    // A Camera frame card like the Planner's: 230 px of heading, direction and readings, plus the frame,
    // which keeps its 208:128 shape across the card's content (18 px of padding and border narrower).
    private static double Row(double cardWidth) => 230 + TerrainFrameView.HeightFor(cardWidth - 18);
    private static readonly PlannerOverlaySizes Defaults = new();

    private static void AssertFitsTheMap(Size map, MapInstrumentLayout.Result layout)
    {
        const double m = MapInstrumentLayout.Margin;
        if (layout.PipShown)
        {
            Assert.True(m + layout.PipSide + MapInstrumentLayout.Gap <= map.Height - m - Row(layout.CameraFrameWidth) + 1e-6,
                $"{layout} overlaps on {map}");
            Assert.True(layout.PipSide >= MapInstrumentLayout.MinimumPipSide - 1e-6);
        }
        Assert.True(layout.CameraFrameWidth >= MapInstrumentLayout.MinimumCameraFrameWidth - 1e-6);
        // The row stays on the map unless the map is shorter than the Camera frame at its smallest.
        if (Row(MapInstrumentLayout.MinimumCameraFrameWidth) <= map.Height - 2 * m)
            Assert.True(Row(layout.CameraFrameWidth) <= map.Height - 2 * m + 1e-6, $"{layout} leaves {map}");
        Assert.True(layout.CameraFrameWidth <= Math.Max(MapInstrumentLayout.MinimumCameraFrameWidth, map.Width / 3) + 1e-6);
    }

    /// <summary>
    /// The shipped sizes, as tuned on a 1920 × 1080 display: on its map, 1,578 px wide beside the Inspector, a
    /// 349 px PiP and a 350 px Camera frame, with room for both (the window itself is checked in
    /// ShippedPlannerLayoutTests).
    /// </summary>
    [Fact]
    public void TheDefaultsAreTheTunedSizesOnAFullHdMap()
    {
        var layout = MapInstrumentLayout.Resolve(new Size(1578, 1000), Defaults, true, Row);
        Assert.True(layout.PipShown);
        Assert.Equal(349, layout.PipSide, 0);
        Assert.Equal(350, layout.CameraFrameWidth, 0);
    }

    [Fact]
    public void EachIsKeptBetweenItsMinimumAndAThirdOfTheMap()
    {
        var wide = MapInstrumentLayout.Resolve(new Size(1200, 2000), new(.5, .5), true, Row);
        Assert.Equal(400, wide.PipSide, 6);
        Assert.Equal(400, wide.CameraFrameWidth, 6);
        var narrow = MapInstrumentLayout.Resolve(new Size(1200, 2000), new(.05, .05), true, Row);
        Assert.Equal(MapInstrumentLayout.MinimumPipSide, narrow.PipSide, 6);
        Assert.Equal(MapInstrumentLayout.MinimumCameraFrameWidth, narrow.CameraFrameWidth, 6);
        // A map narrower than three minimums still draws each at its minimum.
        var small = MapInstrumentLayout.Resolve(new Size(400, 2000), Defaults, true, Row);
        Assert.Equal((MapInstrumentLayout.MinimumPipSide, MapInstrumentLayout.MinimumCameraFrameWidth), (small.PipSide, small.CameraFrameWidth));
    }

    /// <summary>
    /// A shorter map shrinks both in proportion, keeping the gap; a taller one gives back what was taken. The
    /// preferences passed in are the same throughout: nothing here changes them.
    /// </summary>
    [Fact]
    public void AShortMapShrinksBothAndATallerOneRestoresThem()
    {
        var preferred = new PlannerOverlaySizes(.2, .2);
        var roomy = MapInstrumentLayout.Resolve(new Size(1500, 1000), preferred, true, Row);
        Assert.Equal((300, 300), (roomy.PipSide, roomy.CameraFrameWidth));

        var tight = MapInstrumentLayout.Resolve(new Size(1500, 640), preferred, true, Row);
        Assert.True(tight.PipShown);
        Assert.True(tight.PipSide < 300 && tight.CameraFrameWidth < 300);
        Assert.Equal(1, tight.PipSide / tight.CameraFrameWidth, 3);
        Assert.Equal(640 - 2 * MapInstrumentLayout.Margin - MapInstrumentLayout.Gap, tight.PipSide + Row(tight.CameraFrameWidth), 1);
        Assert.Equal(new PlannerOverlaySizes(.2, .2), preferred);

        Assert.Equal(roomy, MapInstrumentLayout.Resolve(new Size(1500, 1000), preferred, true, Row));
    }

    /// <summary>One at its minimum, the other keeps shrinking; below both minimums the PiP is set aside.</summary>
    [Fact]
    public void BelowBothMinimumsThePipIsSetAsideAndNothingOverlaps()
    {
        var lopsided = MapInstrumentLayout.Resolve(new Size(1500, 560), new(.06, .3), true, Row);
        Assert.True(lopsided.PipShown);
        Assert.Equal(MapInstrumentLayout.MinimumPipSide, lopsided.PipSide, 6);
        Assert.True(lopsided.CameraFrameWidth < 450);

        var cramped = MapInstrumentLayout.Resolve(new Size(1500, 480), Defaults, true, Row);
        Assert.False(cramped.PipShown);
        AssertFitsTheMap(new Size(1500, 480), cramped);
        Assert.False(MapInstrumentLayout.Resolve(new Size(1500, 900), Defaults, false, Row).PipShown);
    }

    [Fact]
    public void NoMapSizeOrPreferenceMakesThemOverlap()
    {
        foreach (var width in new[] { 500d, 800, 1100, 1600, 2400 })
        foreach (var height in new[] { 380d, 460, 540, 620, 760, 900, 1300 })
        foreach (var preferred in new PlannerOverlaySizes[] { Defaults, new(.05, .5), new(.5, .05), new(.5, .5), new(double.NaN, -3) })
            AssertFitsTheMap(new Size(width, height), MapInstrumentLayout.Resolve(new Size(width, height), preferred, true, Row));
    }

    [Fact]
    public void AWidthIsSavedAsTheShareOfTheMapItIsWithinTheBounds()
    {
        Assert.Equal(.2, MapInstrumentLayout.FractionOf(300, MapInstrumentLayout.MinimumPipSide, 1500), 6);
        Assert.Equal(1 / 3d, MapInstrumentLayout.FractionOf(900, MapInstrumentLayout.MinimumPipSide, 1500), 6);
        Assert.Equal(MapInstrumentLayout.MinimumPipSide / 1500, MapInstrumentLayout.FractionOf(20, MapInstrumentLayout.MinimumPipSide, 1500), 6);
    }
}
