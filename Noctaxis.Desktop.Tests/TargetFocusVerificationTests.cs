using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Persistence;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.ViewModels;
using NodaTime;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// End-to-end checks of the target, focus and camera model through the Planner's own state transitions: the
/// camera's frame, cone, terrain sampling and readouts are the camera's alone; what is drawn is the shown
/// targets alone; focus is never chosen for the user.
/// </summary>
public sealed partial class MainViewModelTests
{
    /// <summary>A terrain profile whose horizon rises steadily with bearing, so a bearing can be read back from it.</summary>
    private static TerrainHorizonProfile SlopingTerrain(PlanningSnapshot snapshot) => snapshot.Terrain with
    {
        HasDemCoverage = true,
        Samples = Enumerable.Range(0, 360).Select(b => new TerrainHorizonSample(b, 1 + b / 50d, 2000,
            Sightline: [new TerrainSightlineSample(2000, 1 + b / 50d, 0, 3)])).ToArray()
    };

    private static double MeanBearing(IReadOnlyList<double> bearings)
    {
        var (sin, cos) = (bearings.Sum(b => Math.Sin(b * Math.PI / 180)), bearings.Sum(b => Math.Cos(b * Math.PI / 180)));
        return Angles.NormaliseDegrees(Math.Atan2(sin, cos) * 180 / Math.PI);
    }

    /// <summary>
    /// Cases 1 and 8: with Follow off and the camera at 283°/+14.9° while the focused target is at 311.5°/+21.2°,
    /// the frame's centre, its terrain sampling, the map's framing cone and the Horizon readout are the camera's,
    /// and focusing M31, then the Moon, then nothing, changes none of them.
    /// </summary>
    [Fact]
    public async Task TheCamerasFrameConeSamplingAndReadoutsIgnoreTheFocus()
    {
        var (vm, planning) = await TargetsPlannerAsync();
        planning.RecordPriorityTerrain = true;
        planning.HorizontalFor = session => new(311.5, 21.2);
        vm.FollowFocusedTarget = false;
        vm.CameraBearingDegrees = 283;
        vm.CameraPitchDegrees = 14.9;
        Target(vm, Id("M31")).FocusCommand.Execute(null);
        await vm.WaitForPlannerRefreshAsync();
        // The camera's terrain is sampled first when the place changes: a small move asks for it now.
        var latitude = vm.Session.Observer.Latitude;
        async Task Resample()
        {
            latitude += .001;
            vm.MoveObserver(vm.Session.Observer with { Latitude = latitude });
            await vm.WaitForPlannerRefreshAsync();
            vm.Snapshot = vm.Snapshot! with { Terrain = SlopingTerrain(vm.Snapshot) };
        }
        await Resample();

        (double Bearing, double Pitch, double Cone, double Span, double FrameCentre, double FrameLow, string Horizon) Camera()
        {
            var depth = vm.TerrainFrameDepth!;
            return (vm.CameraBearingDegrees, vm.CameraPitchDegrees, vm.CameraFramingGuide!.CentreBearingDegrees,
                vm.CameraFramingGuide.HorizontalFieldOfViewDegrees, MeanBearing(depth.Bearings), depth.LowerAltitude, vm.GroundHorizonAngleText);
        }
        var before = Camera();
        Assert.Equal(283, before.Bearing, 6);
        Assert.Equal(14.9, before.Pitch, 6);
        Assert.Equal(283, before.Cone, 6);
        Assert.Equal(283, before.FrameCentre, 3);
        Assert.Equal(14.9 - vm.CameraFrameVerticalFieldOfView / 2, before.FrameLow, 6);
        // The horizon readout is the profile's at 283°, not at the target's 311.5°.
        Assert.Equal($"+{1 + 283 / 50d:0.0}°", before.Horizon);
        Assert.Contains(planning.PriorityBearings, bearings => Math.Abs(Angles.NormaliseSignedDegrees(MeanBearing(bearings) - 283)) < .5);

        foreach (var step in new Action[]
        {
            () => Target(vm, "moon").FocusCommand.Execute(null),
            () => Target(vm, "moon").IsVisible = false
        })
        {
            planning.PriorityBearings.Clear();
            step();
            await vm.WaitForPlannerRefreshAsync();
            vm.Snapshot = vm.Snapshot! with { Terrain = SlopingTerrain(vm.Snapshot) };
            Assert.Equal(before, Camera());
            await Resample();
            Assert.Equal(before, Camera());
            Assert.NotEmpty(planning.PriorityBearings);
            Assert.All(planning.PriorityBearings, bearings => Assert.True(Math.Abs(Angles.NormaliseSignedDegrees(MeanBearing(bearings) - 283)) < .5));
        }
        Assert.False(vm.HasFocusedTarget);
    }

    /// <summary>
    /// Cases 5 and 7: with targets shown, hidden and one focused, the map's and the Timeline's targets are exactly
    /// the shown ones; showing another, with nothing focused, keeps nothing focused.
    /// </summary>
    [Fact]
    public async Task OnlyShownTargetsAreDrawnAndShowingOneFocusesNothing()
    {
        var (vm, planning) = await TargetsPlannerAsync();
        Target(vm, Id("M31")).IsVisible = false;
        Target(vm, Id("M42")).FocusCommand.Execute(null);
        await vm.WaitForPlannerRefreshAsync();
        string[] Shown() => vm.CelestialObjects.Where(item => item.IsVisible).Select(item => item.TargetId).Order().ToArray();
        await Until(() => vm.TimelinePaths.Select(path => path.Target.Id).Order().SequenceEqual(Shown()), "the Timeline should draw the shown targets");
        // The map draws what the plan is calculated for: exactly the shown targets (PlanningService draws only
        // those; TargetMembershipTests), and nothing hidden is left in the plan on screen.
        Assert.Equal(Shown(), planning.LastCalculatedSession!.EffectiveVisibleObjects.Where(item => item.IsVisible).Select(item => item.TargetId).Order());
        Assert.All(vm.Snapshot!.EffectiveObjectPlans, plan => Assert.Contains(plan.Position.Target.Id, Shown()));
        Assert.Equal(Id("M42"), vm.Snapshot.Position.Target.Id);

        // Nothing focused, then a target shown: still nothing focused.
        Target(vm, Id("M42")).IsVisible = false;
        Assert.False(vm.HasFocusedTarget);
        Target(vm, Id("M31")).IsVisible = true;
        Assert.False(vm.HasFocusedTarget);
        await vm.WaitForPlannerRefreshAsync();
        Assert.False(vm.HasFocusedTarget);
        await Until(() => vm.TimelinePaths.Select(path => path.Target.Id).Order().SequenceEqual(Shown()), "the Timeline should follow");
        Assert.Equal(Shown(), planning.LastCalculatedSession!.EffectiveVisibleObjects.Where(item => item.IsVisible).Select(item => item.TargetId).Order());
        Assert.All(vm.Snapshot!.EffectiveObjectPlans, plan => Assert.Contains(plan.Position.Target.Id, Shown()));
        vm.AddCelestialObjectCommand.Execute(new OpenNgcTargetCatalogue().Get("M13"));
        Assert.True(Target(vm, Id("M13")).IsVisible);
        Assert.False(vm.HasFocusedTarget);
    }

    /// <summary>
    /// Case 10, with fixtures instead of the developer's own state: saved state that focuses a shown target loads with
    /// that focus, and the camera comes back as its Follow setting says. With Follow off the camera keeps the bearing
    /// and pitch it stored; with Follow on it aims at the focused target (aiming writes the camera's own direction),
    /// and turning Follow off afterwards leaves it exactly there. The state goes through the real store and file.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavedFocusAndCameraLoadUnderTheNewRules(bool follow)
    {
        var directory = Path.Combine(Path.GetTempPath(), "noctaxis-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var saved = new PersistedState(4, new AppSettings(
                    CelestialObjects: new CelestialObjectSettings([new("sun", true, 0), new("moon", true, 1), new("m31", false, 2)], "moon"),
                    CameraFraming: new CameraFramingSettings(ManualBearingDegrees: 36.84258575888677, CompositionOffsetDegrees: 12,
                        CameraPitchDegrees: 2.804491582275034, FollowFocusedTarget: follow)),
                [], PlanningSession.Default(Instant.FromUtc(2026, 10, 15, 21, 0), "UTC") with { TargetId = "moon" }, null);
            var store = new JsonUserDataStore(new VerifyPaths(directory), NullLogger<JsonUserDataStore>.Instance, () => Instant.FromUtc(2026, 10, 16, 12, 0));
            await store.SaveAsync(saved, CancellationToken.None);
            var state = await new JsonUserDataStore(new VerifyPaths(directory), NullLogger<JsonUserDataStore>.Instance,
                () => Instant.FromUtc(2026, 10, 16, 12, 0)).LoadAsync(CancellationToken.None);
            await VerifyLoadedStateAsync(state);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// The same checks against a read-only copy of this machine's own saved state. Opt-in diagnostic only
    /// (NOCTAXIS_RUN_LOCAL_STATE_CHECK=1): its input is whatever the developer last saved, so it never runs in ordinary
    /// test runs or CI. The real file is copied, never opened for writing.
    /// </summary>
    [Fact]
    [Trait("Category", "LocalStateDiagnostic")]
    public async Task TheSavedStateOnThisMachineLoadsUnderTheNewRules()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("NOCTAXIS_RUN_LOCAL_STATE_CHECK") == "1",
            "Opt-in diagnostic: set NOCTAXIS_RUN_LOCAL_STATE_CHECK=1 to check this machine's saved state.");
        var saved = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Noctaxis", "state.json");
        Assert.SkipUnless(File.Exists(saved), "This machine has no saved Noctaxis state.");
        var directory = Path.Combine(Path.GetTempPath(), "noctaxis-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.Copy(saved, Path.Combine(directory, "state.json"));
            var state = await new JsonUserDataStore(new VerifyPaths(directory), NullLogger<JsonUserDataStore>.Instance,
                () => SystemClock.Instance.GetCurrentInstant()).LoadAsync(CancellationToken.None);
            await VerifyLoadedStateAsync(state);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task VerifyLoadedStateAsync(PersistedState state)
    {
        var objects = state.Settings.EffectiveCelestialObjects;
        var focus = objects.DefaultPrimaryTargetId;
        if (focus is not null)
            Assert.Contains(objects.EffectiveConfiguredObjects, item => item.IsVisible && item.TargetId.Equals(focus, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(focus is not null, state.Session.IsTargetFocused);

        var catalogue = new OpenNgcTargetCatalogue();
        var framing = state.Settings.EffectiveCameraFraming;
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, new FakeStore(state), new FakeExporter());
        await vm.InitializeAsync(); vm.ShowPlannerCommand.Execute(null); await vm.WaitForPlannerRefreshAsync();
        Assert.All(vm.CelestialObjects.Where(item => item.IsPrimary), item => Assert.True(item.IsVisible));
        Assert.True(vm.CelestialObjects.Count(item => item.IsPrimary) <= 1);
        Assert.Equal(framing.FollowFocusedTarget, vm.FollowFocusedTarget);
        if (vm.IsFollowingFocusedTarget)
        {
            // Following: the camera points at the focused target, and an old offset from it plays no part.
            var target = vm.Snapshot!.Position.Horizontal;
            Assert.Equal(Angles.NormaliseDegrees(target.AzimuthDegrees), vm.CameraBearingDegrees, 6);
            Assert.Equal(target.AltitudeDegrees, vm.CameraPitchDegrees, 6);
            Assert.Equal(0, vm.Settings.EffectiveCameraFraming.CompositionOffsetDegrees);
            var (bearing, pitch) = (vm.CameraBearingDegrees, vm.CameraPitchDegrees);
            vm.FollowFocusedTarget = false;
            Assert.Equal(bearing, vm.CameraBearingDegrees, 6);
            Assert.Equal(pitch, vm.CameraPitchDegrees, 6);
        }
        else
        {
            // Not following: the camera's stored direction is kept exactly.
            Assert.Equal(Angles.NormaliseDegrees(framing.ManualBearingDegrees), vm.CameraBearingDegrees, 6);
            Assert.Equal(framing.CameraPitchDegrees, vm.CameraPitchDegrees, 6);
        }
        if (vm.CameraFramingGuide is { } guide) Assert.Equal(vm.CameraBearingDegrees, guide.CentreBearingDegrees, 6);
    }

    private sealed record VerifyPaths(string Path) : IUserDataPathProvider { public string GetApplicationDataDirectory() => Path; }

    /// <summary>
    /// Case 11: at the normal window width the four weather groups share one row; narrower they wrap, whole
    /// groups at a time, inside the page.
    /// </summary>
    [AvaloniaFact]
    public async Task WeatherGroupsShareARowWhenThereIsRoom()
    {
        foreach (var (width, oneRow) in new[] { (1440d, true), (900d, false) })
        {
            var (_, window, categories) = await OpenSettingsAsync("builtin.noctaxis", 1, width);
            try
            {
                window.SelectSettingsCategory("Weather");
                var list = window.FindControl<ItemsControl>("WeatherTopicGroupList")!;
                var groups = list.GetVisualDescendants().OfType<TextBlock>().Where(text => text.Classes.Contains("weatherTopicGroup"))
                    .Select(text => (Control)text.Parent!).ToArray();
                Assert.Equal(4, groups.Length);
                var rows = groups.Select(group => Math.Round(group.TranslatePoint(default, list)!.Value.Y)).Distinct().Count();
                Assert.True(oneRow ? rows == 1 : rows >= 1, $"{width}: {rows} rows");
                Assert.All(groups, group => Assert.True(group.TranslatePoint(new Point(group.Bounds.Width, 0), list)!.Value.X <= list.Bounds.Width + .5));
            }
            finally { window.Close(); }
        }
    }

    /// <summary>Case 12: the support card stands apart from the licence's own actions, and still opens Ko-fi.</summary>
    [AvaloniaFact]
    public async Task TheSupportCardIsSeparateFromTheLicence()
    {
        var (_, window, _) = await OpenSettingsAsync("builtin.noctaxis", 1, 1440);
        try
        {
            window.SelectSettingsCategory("Supporter");
            var card = window.FindControl<Border>("SupportNoctaxisCard")!;
            Assert.True(card.IsEffectivelyVisible);
            Assert.NotNull(card.Background);
            var buttons = card.GetVisualDescendants().OfType<Button>().ToArray();
            var support = Assert.Single(buttons);
            Assert.Equal("Support on Ko-fi", support.Content);
            Assert.Contains("accent", support.Classes);
            Assert.Equal("https://ko-fi.com/callyyllac", MainViewModel.KoFiUri.AbsoluteUri);
            Assert.DoesNotContain(buttons, button => button.Command is { } command && command == (window.DataContext as MainViewModel)!.ReplaceSupporterLicenceCommand);
        }
        finally { window.Close(); }
    }
}
