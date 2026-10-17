using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Persistence;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.ViewModels;
using NodaTime;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// One state model for targets and the camera. A focused target is always a shown one; hiding it ends the focus
/// and nothing takes its place; what is drawn is the shown targets alone, at once. The Camera frame is the
/// camera's: its bearing, pitch and field of view, which a target moves only through Follow with camera. The
/// frame's skyline stands exactly where the pitch scale puts the horizon's altitude.
/// </summary>
public sealed partial class MainViewModelTests
{
    private static string Id(string name) => new OpenNgcTargetCatalogue().Get(name).Id;

    private async Task<(MainViewModel Vm, FakePlanning Planning)> TargetsPlannerAsync()
    {
        var (vm, planning, _) = await FocusPlannerAsync();
        var catalogue = new OpenNgcTargetCatalogue();
        vm.AddCelestialObjectCommand.Execute(catalogue.Get("M31"));
        vm.AddCelestialObjectCommand.Execute(catalogue.Get("M42"));
        await vm.WaitForPlannerRefreshAsync();
        return (vm, planning);
    }

    private static bool Drawn(MainViewModel vm, string id) =>
        vm.Snapshot!.EffectiveObjectPlans.Any(plan => plan.Position.Target.Id == id) || vm.TimelinePaths.Any(path => path.Target.Id == id);

    // ---- Target state ------------------------------------------------------------------------------------

    /// <summary>A: hiding the focused target ends the focus, gives it to nothing, and takes it off the map and Timeline at once.</summary>
    [Fact]
    public async Task HidingTheFocusedTargetEndsTheFocusAndItsPaths()
    {
        var (vm, _) = await TargetsPlannerAsync();
        var m31 = Target(vm, Id("M31"));
        m31.FocusCommand.Execute(null);
        await Until(() => vm.TimelinePaths.Any(path => path.Target.Id == m31.TargetId), "M31's Timeline path should be calculated");
        Assert.True(m31.IsPrimary);

        m31.IsVisible = false;
        Assert.False(m31.IsVisible);
        Assert.DoesNotContain(vm.CelestialObjects, item => item.IsPrimary);
        Assert.False(vm.HasFocusedTarget);
        Assert.False(vm.HasFocusedTargetDetails);
        Assert.False(vm.Session.IsTargetFocused);
        Assert.Null(vm.Settings.EffectiveCelestialObjects.DefaultPrimaryTargetId);
        Assert.False(Drawn(vm, m31.TargetId), "M31 should leave the map and the Timeline at once");
        await vm.WaitForPlannerRefreshAsync();
        await Until(() => !Drawn(vm, m31.TargetId), "and stay off after the recalculation");
        Assert.DoesNotContain(vm.CelestialObjects, item => item.IsPrimary);
    }

    /// <summary>B: with M31 hidden, focusing and then hiding M42 leaves nothing focused; M31 stays hidden and unfocused.</summary>
    [Fact]
    public async Task HidingAFocusedTargetNeverFocusesOrShowsAnother()
    {
        var (vm, _) = await TargetsPlannerAsync();
        var m31 = Target(vm, Id("M31"));
        var m42 = Target(vm, Id("M42"));
        m31.IsVisible = false;
        m42.FocusCommand.Execute(null);
        Assert.True(m42.IsPrimary);
        m42.IsVisible = false;
        Assert.DoesNotContain(vm.CelestialObjects, item => item.IsPrimary);
        Assert.False(m31.IsVisible);
        Assert.False(m31.IsPrimary);
        // Focusing a hidden target is refused: focus never shows anything.
        m31.FocusCommand.Execute(null);
        Assert.False(m31.IsVisible);
        Assert.False(m31.IsPrimary);
    }

    /// <summary>C: a saved focus on a hidden target loads as no focus; the target stays hidden.</summary>
    [Fact]
    public async Task ASavedFocusOnAHiddenTargetLoadsAsNoFocus()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var m31 = catalogue.Get("M31").Id;
        var settings = new AppSettings(CelestialObjects: new CelestialObjectSettings(
            [new("sun", true, 0), new("moon", true, 1), new(m31, false, 2)], m31));
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, new FakeStore(new PersistedState(4, settings, [],
            PlanningSession.Default(Instant.FromUtc(2026, 10, 2, 21, 0), "UTC") with { TargetId = m31 }, null)), new FakeExporter());
        await vm.InitializeAsync();
        Assert.False(Target(vm, m31).IsVisible);
        Assert.DoesNotContain(vm.CelestialObjects, item => item.IsPrimary);
        Assert.False(vm.Session.IsTargetFocused);
        Assert.Equal("sun", vm.Session.TargetId);
        Assert.Null(vm.Settings.EffectiveCelestialObjects.DefaultPrimaryTargetId);
    }

    /// <summary>D: showing a target draws it and does not take the focus.</summary>
    [Fact]
    public async Task ShowingATargetDoesNotTakeTheFocus()
    {
        var (vm, _) = await TargetsPlannerAsync();
        var m31 = Target(vm, Id("M31"));
        m31.IsVisible = false;
        Target(vm, "moon").FocusCommand.Execute(null);
        m31.IsVisible = true;
        Assert.True(Target(vm, "moon").IsPrimary);
        Assert.False(m31.IsPrimary);
        await vm.WaitForPlannerRefreshAsync();
        await Until(() => vm.Session.EffectiveVisibleObjects.Any(item => item.TargetId == m31.TargetId && item.IsVisible), "M31 should be shown");
        Assert.True(Target(vm, "moon").IsPrimary);
    }

    /// <summary>E: focusing the Sun draws only the shown targets; a hidden M31 is not among them.</summary>
    [Fact]
    public async Task FocusingTheSunDrawsOnlyShownTargets()
    {
        var (vm, _) = await TargetsPlannerAsync();
        var m31 = Target(vm, Id("M31"));
        m31.FocusCommand.Execute(null);
        m31.IsVisible = false;
        Target(vm, "sun").FocusCommand.Execute(null);
        await vm.WaitForPlannerRefreshAsync();
        Assert.True(Target(vm, "sun").IsPrimary);
        Assert.False(Drawn(vm, m31.TargetId));
        await Until(() => vm.TimelinePaths.Count > 0 && !vm.TimelinePaths.Any(path => path.Target.Id == m31.TargetId), "the Timeline should not draw M31");
    }

    // ---- Camera ownership --------------------------------------------------------------------------------

    /// <summary>
    /// F, G, I: with Follow off, focusing, hiding the focused target and clearing the focus leave the camera, and
    /// so the frame, exactly where it was, wherever the target is.
    /// </summary>
    [Fact]
    public async Task WithFollowOffTheTargetNeverMovesTheCamera()
    {
        var (vm, planning) = await TargetsPlannerAsync();
        planning.HorizontalFor = session => new(311.5, 21.2);
        vm.FollowFocusedTarget = false;
        vm.CameraBearingDegrees = 283;
        vm.CameraPitchDegrees = 14.9;
        var guide = vm.CameraFramingGuide;
        void Unchanged(string when)
        {
            Assert.Equal(283, vm.CameraBearingDegrees, 6);
            Assert.Equal(14.9, vm.CameraPitchDegrees, 6);
            Assert.Equal(283, vm.CameraFramingGuide!.CentreBearingDegrees, 6);
            Assert.Equal(guide!.HorizontalFieldOfViewDegrees, vm.CameraFramingGuide.HorizontalFieldOfViewDegrees, 6);
        }

        Target(vm, Id("M31")).FocusCommand.Execute(null);
        await vm.WaitForPlannerRefreshAsync();
        Unchanged("after focusing M31");
        Target(vm, "moon").FocusCommand.Execute(null);
        await vm.WaitForPlannerRefreshAsync();
        Unchanged("after focusing the Moon");
        // The target moving (a new time) does not move the camera either.
        planning.HorizontalFor = session => new(120, 50);
        vm.MinutesOfDay = 23 * 60;
        await vm.WaitForPlannerRefreshAsync();
        Unchanged("after the target moved");
        Target(vm, "moon").IsVisible = false;
        await vm.WaitForPlannerRefreshAsync();
        Unchanged("after hiding the focused target");
        Assert.False(vm.HasFocusedTarget);
    }

    /// <summary>
    /// H, I: Follow on, focusing M31 turns the camera to it through the camera's own bearing and pitch, and the
    /// frame follows the camera; Follow off again, a new focus leaves the camera at its last direction.
    /// </summary>
    [Fact]
    public async Task FollowTurnsTheCameraAndTheFrameFollowsTheCamera()
    {
        var (vm, planning) = await TargetsPlannerAsync();
        planning.HorizontalFor = session => session.TargetId == Id("M31") ? new(311.5, 21.2) : new(90, 5);
        vm.CameraBearingDegrees = 283;
        vm.CameraPitchDegrees = 14.9;
        vm.FollowFocusedTarget = true;
        Target(vm, Id("M31")).FocusCommand.Execute(null);
        await Until(() => Math.Abs(vm.CameraBearingDegrees - 311.5) < 1e-6, "Follow should turn the camera to M31");
        Assert.Equal(21.2, vm.CameraPitchDegrees, 6);
        Assert.Equal(311.5, vm.CameraFramingGuide!.CentreBearingDegrees, 6);

        vm.FollowFocusedTarget = false;
        Target(vm, "moon").FocusCommand.Execute(null);
        await vm.WaitForPlannerRefreshAsync();
        Assert.Equal(311.5, vm.CameraBearingDegrees, 6);
        Assert.Equal(21.2, vm.CameraPitchDegrees, 6);
        Assert.Equal(311.5, vm.CameraFramingGuide!.CentreBearingDegrees, 6);
    }

    // ---- Terrain geometry --------------------------------------------------------------------------------

    /// <summary>A frame and its scales, as the Planner stacks them, for a camera and a level horizon.</summary>
    private static (Window Window, TerrainFrameView View, CameraDirectionPad Pad) Stage(double horizontal, double vertical, double pitch, double horizon)
    {
        const int columns = 41;
        var bearings = Enumerable.Range(0, columns).Select(c => 180 - horizontal / 2 + c * horizontal / (columns - 1)).ToArray();
        var distances = new double[columns * CameraTerrainDepth.Rows];
        var depth = new CameraTerrainDepth([.. bearings], pitch - vertical / 2, pitch + vertical / 2, [.. distances],
            [.. Enumerable.Repeat(horizon, columns)]);
        for (var row = 0; row < CameraTerrainDepth.Rows; row++)
            if (depth.AltitudeAtRow(row) <= horizon)
                for (var column = 0; column < columns; column++) distances[row * columns + column] = 2000;
        depth = depth with { DistancesMetres = [.. distances] };
        var view = new TerrainFrameView { Depth = depth, Width = 300 };
        var pad = new CameraDirectionPad { Bearing = 180, Pitch = pitch, HorizontalFieldOfView = horizontal, VerticalFieldOfView = vertical };
        var stage = new Grid { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Children = { view, pad } };
        var window = new Window { Width = 400, Height = 400, Content = stage };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return (window, view, pad);
    }

    /// <summary>
    /// J–N: the skyline at the frame's centre bearing stands exactly where the pitch scale puts the horizon's
    /// altitude, which is the projection of (horizon − pitch): 2.1° below the centre for +10° over a +7.9°
    /// horizon, through the centre when they are equal, 10° above when looking 10° down at a level horizon. The
    /// picture's terrain meets its sky there too. The same for a Portrait frame and for wide and telephoto lenses.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(73.7, 53.1, 10, 7.9)]      // J: 24 mm landscape
    [InlineData(73.7, 53.1, 0, 0)]         // K
    [InlineData(73.7, 53.1, 10, 10)]       // L
    [InlineData(73.7, 53.1, -10, 0)]       // M
    [InlineData(53.1, 73.7, 10, 7.9)]      // N: Portrait
    [InlineData(104.3, 81.2, 10, 7.9)]     // N: 14 mm
    [InlineData(10.3, 6.9, 2, 0.8)]        // N: 200 mm
    [InlineData(39.6, 27.0, 10, 7.9)]      // 50 mm landscape
    [InlineData(27.0, 39.6, 10, 7.9)]      // 50 mm portrait
    [InlineData(81.2, 104.3, 10, 7.9)]     // 14 mm portrait
    [InlineData(6.9, 10.3, 2, 0.8)]        // 200 mm portrait
    public void TheSkylineStandsWhereThePitchScalePutsTheHorizon(double horizontal, double vertical, double pitch, double horizon)
    {
        var (window, view, pad) = Stage(horizontal, vertical, pitch, horizon);
        try
        {
            var frame = view.FrameRectangle;
            var centreColumn = view.SkylinePoints.Count / 2;
            var skyline = view.SkylinePoints[centreColumn]!.Value;
            Assert.Equal(frame.Center.X, skyline.X, 3);
            // The skyline and the pitch scale: the one projection, the same height.
            // Within half a pixel: the scale's bounds are layout-rounded, the picture's rectangle exact.
            Assert.InRange(pad.AltitudeY(horizon) - skyline.Y, -.5, .5);
            Assert.Equal(view.AltitudeY(horizon)!.Value, skyline.Y, 3);
            if (horizon == pitch) Assert.Equal(frame.Center.Y, skyline.Y, 3);
            if (horizon < pitch) Assert.True(skyline.Y > frame.Center.Y, "a horizon below the camera's pitch is below the centre");
            if (horizon > pitch) Assert.True(skyline.Y < frame.Center.Y, "a horizon above the camera's pitch is above the centre");

            // The picture's terrain meets its sky at the skyline: sky just above it, terrain just below.
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(frame.Right), (int)Math.Ceiling(frame.Bottom)));
            bitmap.Render(view);
            var pixels = RenderedPixels.Bgra(bitmap);
            Avalonia.Media.Color At(double y)
            {
                var c = RenderedPixels.At(pixels, bitmap.PixelSize.Width, (int)frame.Center.X + 3, (int)Math.Round(y));
                return Avalonia.Media.Color.FromRgb(c.R, c.G, c.B);
            }
            if (skyline.Y > frame.Top + 4 && skyline.Y < frame.Bottom - 4)
            {
                var above = At(skyline.Y - 3);
                var below = At(skyline.Y + 3);
                Assert.True(above.B > above.R, $"above the skyline should be sky, not {above}");
                Assert.Equal(below.R, below.B);
            }
        }
        finally { window.Close(); }
    }
}
