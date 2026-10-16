using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Persistence;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.ViewModels;
using NodaTime;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// The focused target: one at a time, called Focus and Focused, aiming the camera when focused and,
/// with Follow with camera, keeping it aimed. Its details lead the Targets tab, its rise and set say
/// why when there is none, and the list beneath shows, hides and focuses targets without removing them.
/// </summary>
public sealed partial class MainViewModelTests
{
    private static async Task Until(Func<bool> condition, string what)
    {
        for (var waited = 0; waited < 4000 && !condition(); waited += 20) await Task.Delay(20);
        Assert.True(condition(), what);
    }

    private async Task<(MainViewModel ViewModel, FakePlanning Planning, FakeStore Store)> FocusPlannerAsync()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new FakePlanning(catalogue);
        var store = new FakeStore(new PersistedState(4, new AppSettings(), [],
            PlanningSession.Default(Instant.FromUtc(2026, 10, 2, 21, 0), "UTC"), null));
        var vm = CreateViewModel(planning, catalogue, store, new FakeExporter());
        await vm.InitializeAsync(); vm.ShowPlannerCommand.Execute(null); await vm.WaitForPlannerRefreshAsync();
        return (vm, planning, store);
    }

    private static CelestialObjectItemViewModel Target(MainViewModel vm, string id) =>
        vm.CelestialObjects.Single(item => item.TargetId.Equals(id, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task OneTargetIsFocusedAndTheButtonSaysFocusOrFocused()
    {
        var (vm, _, _) = await FocusPlannerAsync();
        var moon = Target(vm, "moon");
        var sun = Target(vm, "sun");
        moon.FocusCommand.Execute(null);
        Assert.Single(vm.CelestialObjects, item => item.IsPrimary);
        Assert.True(moon.IsPrimary);
        Assert.False(sun.IsPrimary);
        Assert.Equal(moon.DisplayName, vm.FocusedTargetName);
        sun.FocusCommand.Execute(null);
        Assert.Single(vm.CelestialObjects, item => item.IsPrimary);
        Assert.True(sun.IsPrimary);
        Assert.False(moon.IsPrimary);
        // No "primary" anywhere the user reads.
        foreach (var key in new[] { "Planner_Targets_Focus_Button", "Planner_Targets_Focused_Label", "Planner_Targets_Help" })
            Assert.DoesNotContain("rimary", Noctaxis.Core.Localisation.Strings.Get(key));
    }

    /// <summary>
    /// Focus aims the camera only through Follow with camera: with Follow on, focusing turns the camera to the
    /// target, from the position the Planner shows for it; with Follow off the camera stays where it is.
    /// </summary>
    [Fact]
    public async Task FocusAimsTheCameraOnlyWhileFollowing()
    {
        var (vm, planning, store) = await FocusPlannerAsync();
        planning.HorizontalFor = session => session.TargetId == "moon" ? new(359.5, 12.5) : new(200, 40);
        vm.CameraBearingDegrees = 90; vm.CameraPitchDegrees = -10;
        Target(vm, "moon").FocusCommand.Execute(null);
        await vm.WaitForPlannerRefreshAsync();
        Assert.Equal((90, -10), (vm.CameraBearingDegrees, vm.CameraPitchDegrees));

        vm.FollowFocusedTarget = true;
        await Until(() => Math.Abs(vm.CameraPitchDegrees - 12.5) < 1e-6, "following should tilt the camera to the moon's altitude");
        Assert.Equal(359.5, vm.CameraBearingDegrees, 6);
        Assert.Equal("359.5°", vm.AzimuthText);
        Assert.Equal("+12.5°", vm.AltitudeText);
        await vm.WaitForCameraFramingPersistenceAsync();
        Assert.Equal(12.5, store.State.Settings.EffectiveCameraFraming.CameraPitchDegrees, 6);

        // Below the camera's lower limit, it points as low as the camera goes.
        planning.HorizontalFor = session => session.TargetId == "sun" ? new(10, -70) : new(359.5, 12.5);
        Target(vm, "sun").FocusCommand.Execute(null);
        await Until(() => vm.CameraPitchDegrees == CameraFramingSettings.MinimumCameraPitchDegrees, "pitch should stop at -45°");
        Assert.Equal(10, vm.CameraBearingDegrees, 6);
    }

    /// <summary>
    /// Following keeps the camera on the focused target as the time, the place and the focused target
    /// change. Turning it off leaves the camera where it was and stops the pitch following.
    /// </summary>
    [Fact]
    public async Task FollowKeepsTheCameraOnTheFocusedTargetUntilItIsTurnedOff()
    {
        var (vm, planning, store) = await FocusPlannerAsync();
        planning.HorizontalFor = session => new(
            (session.Instant.InUtc().Hour * 15 + (session.TargetId == "moon" ? 100 : 0)) % 360,
            Math.Round(session.Observer.Latitude / 2 + (session.TargetId == "moon" ? 5 : 0), 3));
        Target(vm, "sun").FocusCommand.Execute(null);
        await vm.WaitForPlannerRefreshAsync();
        vm.FollowFocusedTarget = true;
        Assert.True(vm.IsFollowingFocusedTarget);
        Assert.False(vm.IsCameraDirectionManual);
        await vm.WaitForCameraFramingPersistenceAsync();
        Assert.True(store.State.Settings.EffectiveCameraFraming.FollowFocusedTarget);

        // A new time.
        ScrubTo(vm, new LocalTime(23, 0));
        vm.CommitTemporalPreview();
        double Azimuth(int offset) => (vm.Session.Instant.InUtc().Hour * 15 + offset) % 360;
        await Until(() => vm.Session.Instant.InUtc().Hour != 21 && Math.Abs(vm.CameraBearingDegrees - Azimuth(0)) < 1e-6,
            "following should turn with the new time");

        // A new place.
        vm.Latitude = 40;
        await Until(() => Math.Abs(vm.CameraPitchDegrees - 20) < 1e-6, "following should tilt for the new place");

        // A new focused target.
        Target(vm, "moon").FocusCommand.Execute(null);
        await Until(() => Math.Abs(vm.CameraPitchDegrees - 25) < 1e-6, "following should move to the new target");
        Assert.Equal(Azimuth(100), vm.CameraBearingDegrees, 6);

        // Off: nothing moves now, and pitch stays put when the time changes.
        var (bearing, pitch) = (vm.CameraBearingDegrees, vm.CameraPitchDegrees);
        vm.FollowFocusedTarget = false;
        Assert.Equal(bearing, vm.CameraBearingDegrees, 6);
        Assert.Equal(pitch, vm.CameraPitchDegrees, 6);
        Assert.True(vm.IsCameraDirectionManual);
        vm.Latitude = 60;
        await Task.Delay(400); await vm.WaitForPlannerRefreshAsync();
        Assert.Equal(pitch, vm.CameraPitchDegrees, 6);
    }

    /// <summary>Showing and hiding targets is separate from focusing them.</summary>
    [Fact]
    public async Task VisibilityStaysIndependentOfFocus()
    {
        var (vm, _, _) = await FocusPlannerAsync();
        var sun = Target(vm, "sun"); var moon = Target(vm, "moon");
        sun.FocusCommand.Execute(null);
        var visible = vm.CelestialObjects.ToDictionary(item => item.TargetId, item => item.IsVisible);
        moon.IsVisible = !moon.IsVisible;
        Assert.True(sun.IsPrimary);
        moon.IsVisible = true;
        moon.FocusCommand.Execute(null);
        Assert.True(sun.IsVisible); // Focusing another leaves this one shown.
        Assert.All(vm.CelestialObjects.Where(item => item.TargetId is not ("sun" or "moon")),
            item => Assert.Equal(visible[item.TargetId], item.IsVisible));
    }

    /// <summary>A missing rise or set says why, when the model knows; "—" only when there is no position at all.</summary>
    [Theory]
    [InlineData(DayHorizon.AboveAllDay, "Up all day", "Does not set")]
    [InlineData(DayHorizon.BelowAllDay, "Does not rise", "Down all day")]
    [InlineData(DayHorizon.Crosses, "Not on this date", "Not on this date")]
    public async Task RiseAndSetSayWhyThereIsNone(DayHorizon horizon, string rise, string set)
    {
        var (vm, planning, _) = await FocusPlannerAsync();
        var transit = Instant.FromUtc(2026, 10, 3, 0, 4);
        planning.Events = new TargetEvents(null, transit, null, horizon);
        ScrubTo(vm, new LocalTime(22, 0));
        vm.CommitTemporalPreview();
        await Until(() => vm.RiseText == rise && System.Text.RegularExpressions.Regex.IsMatch(vm.TransitText, @"^[0-9]{2}:[0-9]{2}$"), $"rise should read {rise}, not {vm.RiseText}");
        Assert.Equal(set, vm.SetText);
        Assert.Matches(@"^[0-9]{2}:[0-9]{2}$", vm.TransitText);
        planning.Events = new TargetEvents(Instant.FromUtc(2026, 10, 2, 18, 30), transit, null);
        ScrubTo(vm, new LocalTime(22, 5));
        vm.CommitTemporalPreview();
        await Until(() => System.Text.RegularExpressions.Regex.IsMatch(vm.RiseText, @"^[0-9]{2}:[0-9]{2}$"), "a real rise time is shown as a time");
        vm.Snapshot = null;
        Assert.Equal("—", vm.RiseText);
    }

    // ---- The window ----------------------------------------------------------------------------------

    /// <summary>
    /// The Targets tab leads with the focused target's details, always open, then the list. The rows have
    /// no remove button (Settings → Targets owns that) and the explanation is behind an info button.
    /// </summary>
    [Fact]
    public void TheTargetsTabLeadsWithTheFocusedTargetAndKeepsHelpBehindInfo()
    {
        var targets = TestMarkup.LoadMainWindow().Descendants()
            .Single(element => NameOf(element) == "PlannerInspectorTabs").Elements()
            .Single(element => element.Name.LocalName == "TabItem" && element.Attribute("Header")?.Value == "Targets");
        var sections = targets.Descendants().Single(element => NameOf(element) == "PlannerTargets").Elements()
            .Select(NameOf).Where(name => name != "").ToArray();
        Assert.Equal(["FocusedTargetDetails", "TargetList"], sections);
        Assert.DoesNotContain(targets.Descendants(), element => element.Name.LocalName == "Expander");
        Assert.Equal("Focused target details", targets.Descendants().First(element => (string?)element.Attribute("Classes") == "section").Attribute("Text")?.Value);

        var rows = targets.Descendants().Single(element => NameOf(element) == "PlannerTargetRows");
        Assert.DoesNotContain(rows.Descendants(), element => element.Attribute("Command")?.Value == "{Binding RemoveCommand}");
        Assert.DoesNotContain(rows.Descendants(), element => (string?)element.Attribute("Classes") == "compactDelete");
        Assert.Contains(rows.Descendants(), element => element.Attribute("Command")?.Value == "{Binding FocusCommand}");
        Assert.Contains(rows.Descendants(), element => element.Attribute("IsChecked")?.Value == "{Binding IsVisible}");
        // Removing a target is still where targets are managed.
        var settings = TestMarkup.LoadMainWindow().Descendants().Single(element =>
            element.Name.LocalName == "TabItem" && element.Attribute("Header")?.Value == "Settings");
        Assert.Contains(settings.Descendants(), element => element.Attribute("Command")?.Value == "{Binding RemoveCommand}");

        var help = targets.Descendants().Single(element => NameOf(element) == "TargetsHelp");
        Assert.Contains(help.Ancestors(), element => element.Name.LocalName == "Flyout");
        var text = Noctaxis.Core.Localisation.Strings.Get("Planner_Targets_Help");
        Assert.Contains("focused target", text);
        Assert.Contains("colour and dash pattern", text);
        Assert.DoesNotContain("marker", text);
        Assert.Single(TestMarkup.ReadMainWindow().Split(text).Skip(1));
    }

    [AvaloniaFact]
    public async Task TheTargetsHelpOpensFromATapOrTheKeyboard()
    {
        var (_, window, _) = await CreatePlannerAsync();
        try
        {
            var button = window.FindControl<Button>("TargetsHelpButton")!;
            Assert.Equal("About targets", Avalonia.Automation.AutomationProperties.GetName(button));
            Assert.True(button.Bounds.Width >= 32 && button.Bounds.Height >= 28);
            var flyout = Assert.IsType<Flyout>(button.Flyout);
            // Below the fold of the Inspector in this window: scrolled to, as it would be.
            button.BringIntoView();
            Settle(window);
            var at = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Settle(window);
            Assert.True(flyout.IsOpen, "a click should open the targets explanation");
            Assert.Equal(280, Assert.IsType<TextBlock>(flyout.Content).MaxWidth);
            flyout.Hide();
            Settle(window);
            Assert.True(button.Focus(NavigationMethod.Tab));
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Settle(window);
            Assert.True(flyout.IsOpen, "Space should open the targets explanation");
            flyout.Hide();
        }
        finally { Close(window); }
    }

    /// <summary>
    /// Following owns the camera's direction: the instrument, its arrows and its values stay in view
    /// but cannot be used, while the rest of Equipment can. Off again, they all work.
    /// </summary>
    [AvaloniaFact]
    public async Task FollowingDisablesOnlyTheManualDirectionControls()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            var toggle = window.FindControl<CheckBox>("FollowFocusedTargetToggle")!;
            window.FindControl<TabControl>("PlannerInspectorTabs")!.SelectedIndex = 2;
            Settle(window);
            Assert.True(toggle.IsEffectivelyEnabled);
            Assert.Equal("Follow with camera", toggle.Content);
            toggle.IsChecked = true;
            Settle(window);
            Assert.True(vm.FollowFocusedTarget);
            var manual = new Control[]
            {
                window.FindControl<CameraDirectionPad>("CameraDirection")!,
                window.FindControl<RepeatButton>("CameraTurnLeftButton")!, window.FindControl<RepeatButton>("CameraTurnRightButton")!,
                window.FindControl<RepeatButton>("CameraTiltUpButton")!, window.FindControl<RepeatButton>("CameraTiltDownButton")!
            };
            Assert.All(manual, control => { Assert.True(control.IsVisible); Assert.False(control.IsEffectivelyEnabled); });
            var values = new[] { window.FindControl<NumericUpDown>("CameraBearingInput")!, window.FindControl<NumericUpDown>("CameraPitchInput")! };
            Assert.All(values, value => { Assert.True(value.IsVisible); Assert.True(value.IsReadOnly); });
            Assert.True(window.FindControl<Slider>("FocalLengthSlider")!.IsEffectivelyEnabled || !vm.IsFocalLengthEditable);
            Assert.True(window.FindControl<RadioButton>("LandscapeOrientationRadioButton")!.IsEffectivelyEnabled);

            toggle.IsChecked = false;
            Settle(window);
            Assert.All(manual, control => Assert.True(control.IsEffectivelyEnabled));
            Assert.All(values, value => Assert.False(value.IsReadOnly));
        }
        finally { Close(window); }
    }

    private static System.Xml.Linq.XElement TargetsMarkup() => TestMarkup.LoadMainWindow().Descendants()
        .Single(element => NameOf(element) == "PlannerInspectorTabs").Elements()
        .Single(element => element.Name.LocalName == "TabItem" && element.Attribute("Header")?.Value == "Targets");

    /// <summary>
    /// The count of shown targets heads the list it counts, short; Follow with camera sits with the
    /// focused target it follows, and nowhere else.
    /// </summary>
    [Fact]
    public void TheCountHeadsTheListAndFollowSitsWithTheFocusedTarget()
    {
        var targets = TargetsMarkup();
        var order = targets.Descendants().Select(NameOf).Where(name => name is "TargetsVisibleCount" or "PlannerTargetRows").ToArray();
        Assert.Equal(["TargetsVisibleCount", "PlannerTargetRows"], order);
        Assert.Equal("{Binding VisibleCelestialCountShortText}", targets.Descendants().Single(element => NameOf(element) == "TargetsVisibleCount").Attribute("Text")?.Value);
        Assert.DoesNotContain(targets.Descendants(), element => element.Attribute("Text")?.Value == "{Binding VisibleCelestialCountText}");
        Assert.Equal("Visible 2 / 8", Noctaxis.Core.Localisation.Strings.Format("Planner_Targets_VisibleCountShort_Format", 2, 8));

        var follow = TestMarkup.LoadMainWindow().Descendants().Where(element => NameOf(element) == "FollowFocusedTargetToggle").ToArray();
        var toggle = Assert.Single(follow);
        Assert.Contains(toggle.Ancestors(), element => NameOf(element) == "FocusedTargetDetails");
        Assert.Equal("Follow with camera", toggle.Attribute("Content")?.Value);
    }

    /// <summary>
    /// Focus is a button on every other target; on the focused one, Focused is a label — text, not a
    /// control, so nothing about it invites a press.
    /// </summary>
    [AvaloniaFact]
    public async Task FocusedIsALabelAndFocusIsAButton()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            window.FindControl<TabControl>("PlannerInspectorTabs")!.SelectedIndex = 0;
            Settle(window);
            var rows = window.FindControl<ItemsControl>("PlannerTargetRows")!;
            Control Row(CelestialObjectItemViewModel item) => rows.ContainerFromItem(item)!;
            Button? FocusButton(CelestialObjectItemViewModel item) => Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(Row(item))
                .OfType<Button>().SingleOrDefault(button => button.GetType() == typeof(Button) && button.IsEffectivelyVisible);
            TextBlock? FocusedLabel(CelestialObjectItemViewModel item) => Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(Row(item))
                .OfType<TextBlock>().SingleOrDefault(text => text.Classes.Contains("focusedState") && text.IsEffectivelyVisible);

            var focused = vm.CelestialObjects.Single(item => item.IsPrimary);
            var other = vm.CelestialObjects.First(item => !item.IsPrimary && item.IsVisible);
            Assert.Null(FocusButton(focused));
            Assert.Equal("Focused", FocusedLabel(focused)!.Text);
            Assert.Equal(Avalonia.Media.FontStyle.Italic, FocusedLabel(focused)!.FontStyle);
            Assert.Null(FocusedLabel(other));
            var button = FocusButton(other)!;
            Assert.Equal("Focus", button.Content);

            button.BringIntoView();
            Settle(window);
            var at = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Settle(window);
            Assert.True(other.IsPrimary);
            Assert.False(focused.IsPrimary);
            Assert.Null(FocusButton(other));
            Assert.NotNull(FocusButton(focused));
        }
        finally { Close(window); }
    }
}
