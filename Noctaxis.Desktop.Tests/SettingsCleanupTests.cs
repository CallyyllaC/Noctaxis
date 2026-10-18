using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.ViewModels;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// The Settings cleanup: each setting named plainly with its control beneath, explanations behind an explicit ⓘ,
/// and the specific changes page by page.
/// </summary>
public sealed partial class MainViewModelTests
{
    private static System.Xml.Linq.XElement SettingsPage(string header) =>
        TestMarkup.LoadMainWindow().Descendants().Single(element => element.Name.LocalName == "TabItem" &&
            element.Attribute("Header")?.Value == "Settings").Descendants()
            .Single(element => element.Name.LocalName == "TabItem" && element.Attribute("Header")?.Value == header);

    // ---- Global ------------------------------------------------------------------------------------------

    /// <summary>
    /// An explanation is behind its ⓘ, opened by a click or tap and by the keyboard, and says what the old
    /// paragraph said; the page itself no longer carries it. Every ⓘ is named for what it explains.
    /// </summary>
    [AvaloniaFact]
    public async Task ExplanationsAreBehindExplicitInfoButtons()
    {
        var (_, window, _) = await OpenSettingsAsync("builtin.noctaxis", 1, 1440);
        try
        {
            var units = window.FindControl<InfoButton>("UnitsInfo")!;
            Assert.Equal("About units", Avalonia.Automation.AutomationProperties.GetName(units));
            Assert.True(units.Focusable && units.Bounds.Width >= 32 && units.Bounds.Height >= 28);
            var at = units.TranslatePoint(new Point(units.Bounds.Width / 2, units.Bounds.Height / 2), window)!.Value;
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.True(units.Flyout!.IsOpen);
            Assert.Contains("• km/h", units.Explanation.Text);
            Assert.Contains("UK Mixed", units.Explanation.Text);
            units.Flyout.Hide();

            var zone = window.FindControl<InfoButton>("TimeZoneInfo")!;
            Assert.True(zone.Focus(NavigationMethod.Tab));
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.True(zone.Flyout!.IsOpen);
            zone.Flyout.Hide();

            // No permanent paragraphs are left on the pages they explained; each ⓘ is named.
            var settings = TestMarkup.LoadMainWindow().Descendants().Single(element => element.Name.LocalName == "TabItem" &&
                element.Attribute("Header")?.Value == "Settings");
            foreach (var header in new[] { "General", "Appearance", "Equipment", "Planner map", "Weather", "Celestial objects", "Data" })
                Assert.DoesNotContain(SettingsPage(header).Descendants(), element => element.Attribute("Classes")?.Value == "settingDescription" &&
                    element.Attribute("Text")?.Value?.StartsWith("{Binding") == false);
            Assert.All(settings.Descendants().Where(element => element.Name.LocalName == "InfoButton"),
                info => Assert.StartsWith("About ", info.Attribute("AutomationProperties.Name")?.Value));
        }
        finally { window.Close(); }
    }

    /// <summary>A setting's name is set larger and firmer than any secondary text beneath it.</summary>
    [AvaloniaFact]
    public async Task SettingNamesLeadTheirSecondaryText()
    {
        var (_, window, categories) = await OpenSettingsAsync("builtin.noctaxis", 1, 1440);
        try
        {
            window.SelectSettingsCategory("General");
            var name = categories.GetVisualDescendants().OfType<TextBlock>().First(text => text.Classes.Contains("fieldLabel") && text.IsEffectivelyVisible);
            Assert.False(string.IsNullOrEmpty(name.Text));
            var body = (double)Application.Current!.FindResource("TextBody")!;
            var small = (double)Application.Current!.FindResource("TextSmall")!;
            Assert.Equal(body, name.FontSize);
            Assert.True(name.FontWeight >= Avalonia.Media.FontWeight.Medium);
            Assert.True(name.FontSize > small, "a setting's name is larger than the secondary text beneath it");
        }
        finally { window.Close(); }
    }

    // ---- General ------------------------------------------------------------------------------------------

    /// <summary>
    /// The system time zone is named "System", while "system" is still what is saved; the snap is the Timeline's,
    /// in minutes shown after it, with the same behaviour.
    /// </summary>
    [AvaloniaFact]
    public async Task TimeZoneSystemIsCapitalisedAndTheTimelineSnapNamedPlainly()
    {
        var (vm, window, _) = await OpenSettingsAsync("builtin.noctaxis", 1, 1440);
        try
        {
            Assert.Equal("System", TimeZoneLabelConverter.Label(AppSettings.UseSystemTimeZoneId));
            Assert.Equal("Europe/London", TimeZoneLabelConverter.Label("Europe/London"));
            Assert.Equal(AppSettings.UseSystemTimeZoneId, vm.SettingsTimeZoneOptions[0]);
            var general = SettingsPage("General");
            Assert.Contains(general.Descendants(), element => element.Attribute("Text")?.Value == "Timeline snap interval");
            Assert.DoesNotContain(general.Descendants(), element => element.Attribute("Text")?.Value?.Contains("Time slider") == true);
            var snap = window.FindControl<NumericUpDown>("TimelineSnapInput")!;
            Assert.Equal("min", ((StackPanel)snap.Parent!).Children.OfType<TextBlock>().Single().Text);
            vm.SettingsTimeSnapMinutes = 10;
            Assert.Equal(10, vm.TimelineStepMinutes);
            Assert.DoesNotContain("only language", Noctaxis.Core.Localisation.Strings.Get("Settings_General_Language_Description"));
        }
        finally { window.Close(); }
    }

    // ---- Equipment ----------------------------------------------------------------------------------------

    /// <summary>
    /// A camera's resolution is optional: both dimensions are saved together, one alone is kept in the editor
    /// with a hint and saved as none, and the pixel pitch is derived and shown, read-only.
    /// </summary>
    [Fact]
    [CoversSettingsInput("ResolutionWidthPixels")]
    [CoversSettingsInput("ResolutionHeightPixels")]
    public async Task ACamerasOptionalResolutionIsSavedWholeOrNotAtAll()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var store = DefaultStore();
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter());
        await vm.InitializeAsync();
        var camera = vm.EquipmentCameraEditors[0];
        Assert.Null(camera.ResolutionHint);
        Assert.Equal(string.Empty, camera.PixelPitchText);

        camera.ResolutionWidthPixels = 6000;
        Assert.Equal("Enter both width and height to use the resolution.", camera.ResolutionHint);
        Assert.True(camera.IsValid);
        await vm.SettleSettingsAsync();
        Assert.Null(store.State.Settings.Equipment!.Cameras!.Single(profile => profile.Id == camera.Id).ResolutionWidthPixels);

        camera.SensorWidthMillimetres = 36;
        camera.SensorHeightMillimetres = 24;
        camera.ResolutionHeightPixels = 4000;
        Assert.Null(camera.ResolutionHint);
        Assert.Equal("Pixel pitch 6.00 µm", camera.PixelPitchText);
        await vm.SettleSettingsAsync();
        var saved = store.State.Settings.Equipment!.Cameras!.Single(profile => profile.Id == camera.Id);
        Assert.Equal((6000, 4000), (saved.ResolutionWidthPixels, saved.ResolutionHeightPixels));

        // Not a whole number of pixels: no resolution, rather than a wrong one.
        camera.ResolutionHeightPixels = 4000.5m;
        Assert.Null(camera.Profile.ResolutionHeightPixels);
    }

    /// <summary>
    /// A lens's type is derived from its focal lengths and shown quietly inside its name field, never edited;
    /// the delete buttons are the bin glyph, centred in a full touch target, named for what they remove.
    /// </summary>
    [AvaloniaFact]
    public async Task LensTypeIsDerivedAndDeleteButtonsAreClear()
    {
        var (vm, window, categories) = await OpenSettingsAsync("builtin.noctaxis", 1, 1440);
        try
        {
            var lens = vm.EquipmentLensEditors[0];
            lens.MinimumFocalLengthMillimetres = 50;
            lens.MaximumFocalLengthMillimetres = 50;
            Assert.Equal("Prime", lens.TypeText);
            lens.MaximumFocalLengthMillimetres = 70;
            Assert.Equal("Zoom", lens.TypeText);
            var equipment = SettingsPage("Equipment");
            Assert.DoesNotContain(equipment.Descendants(), element => element.Attribute("Text")?.Value == "{Binding TypeText}" &&
                element.Parent?.Name.LocalName != "TextBox.InnerRightContent");

            window.SelectSettingsCategory("Equipment");
            foreach (var delete in categories.GetVisualDescendants().OfType<Button>().Where(button => button.Classes.Contains("compactDelete") && button.IsEffectivelyVisible))
            {
                Assert.True(delete.Bounds.Width >= 34 && delete.Bounds.Height >= 34, $"{delete.Bounds.Size}");
                var glyph = Assert.IsType<Avalonia.Controls.Shapes.Path>(delete.Content);
                var centre = glyph.TranslatePoint(new Point(glyph.Bounds.Width / 2, glyph.Bounds.Height / 2), delete)!.Value;
                Assert.Equal(delete.Bounds.Width / 2, centre.X, 0);
                Assert.Equal(delete.Bounds.Height / 2, centre.Y, 0);
                Assert.True(glyph.Bounds.Width >= 14);
                Assert.StartsWith("Remove", Avalonia.Automation.AutomationProperties.GetName(delete));
            }
        }
        finally { window.Close(); }
    }

    // ---- Planner map --------------------------------------------------------------------------------------

    /// <summary>
    /// The PiP's own drawing settings give way while it is off; the framing appearance is on the page itself, not
    /// behind a nested expander, and gives way while the framing is off; Reset Planner layout is unchanged.
    /// </summary>
    [AvaloniaFact]
    public async Task PlannerMapDependentSettingsAndInlineFramingAppearance()
    {
        var (vm, window, _) = await OpenSettingsAsync("builtin.noctaxis", 1, 1440);
        try
        {
            window.SelectSettingsCategory("Planner map");
            vm.SettingsPlannerPipEnabled = false;
            Dispatcher.UIThread.RunJobs();
            foreach (var name in new[] { "PlannerPipRoadsAndLabelsToggle", "PlannerPipEnhanceContrastToggle" })
            {
                Assert.False(window.FindControl<CheckBox>(name)!.IsEffectivelyEnabled);
                Assert.True(window.FindControl<CheckBox>(name)!.IsEffectivelyVisible);
            }
            vm.SettingsPlannerPipEnabled = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.FindControl<CheckBox>("PlannerPipRoadsAndLabelsToggle")!.IsEffectivelyEnabled);

            var page = SettingsPage("Planner map");
            Assert.DoesNotContain(page.Descendants(), element => element.Name.LocalName == "Expander");
            var appearance = window.FindControl<StackPanel>("FramingAppearanceSettings")!;
            Assert.True(appearance.IsEffectivelyVisible);
            Assert.Contains(appearance.GetVisualDescendants(), control => control.Name == "TerrainTintStrengthSlider");
            vm.SettingsCameraFramingOverlayVisible = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(appearance.IsEffectivelyEnabled);

            var reset = window.FindControl<Button>("ResetPlannerLayoutButton")!;
            Assert.Same(vm.PlannerDock.ResetLayoutCommand, reset.Command);
            // The foreground setting stays, named for what it does, with its explanation behind its ⓘ.
            Assert.Contains(page.Descendants(), element => element.Attribute("Text")?.Value == "Ignore terrain in the bottom of the frame");
            Assert.Contains("threshold line", window.FindControl<InfoButton>("TerrainCoverageInfo")!.Text);
        }
        finally { window.Close(); }
    }

    // ---- Weather ------------------------------------------------------------------------------------------

    /// <summary>
    /// A weather topic stands for the values behind it: on while any is, and turning it off turns all of them off,
    /// which takes them out of the forecast request and the Weather panel together. The saved setting is still the
    /// list of values, so older settings load as they were.
    /// </summary>
    [Fact]
    public async Task WeatherTopicsTurnTheirValuesOnAndOffTogether()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var store = DefaultStore();
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter());
        await vm.InitializeAsync();
        var clouds = vm.WeatherTopicGroups.Single(group => group.Label == "Sky").Topics.Single(topic => topic.Label == "Clouds");
        Assert.Equal([WeatherField.TotalCloudCover, WeatherField.LowCloudCover, WeatherField.MediumCloudCover, WeatherField.HighCloudCover],
            clouds.Fields.Select(option => option.Field));
        Assert.True(clouds.IsEnabled);
        clouds.IsEnabled = false;
        await vm.SettleSettingsAsync();
        Assert.DoesNotContain(store.State.Settings.EffectiveWeather.EffectiveFields, field => clouds.Fields.Any(option => option.Field == field));
        Assert.Contains(WeatherField.Visibility, store.State.Settings.EffectiveWeather.EffectiveFields);

        // Any one value on shows the topic on; turning it on turns all of them on.
        clouds.Fields[1].IsEnabled = true;
        Assert.True(clouds.IsEnabled);
        clouds.IsEnabled = true;
        Assert.All(clouds.Fields, option => Assert.True(option.IsEnabled));
        Assert.Equal(["Sky", "Conditions", "Light", "Moon"], vm.WeatherTopicGroups.Select(group => group.Label));
    }

    // ---- Celestial objects --------------------------------------------------------------------------------

    /// <summary>
    /// Settings orders, shows and removes objects with arrow and bin buttons named for their object; focusing is
    /// the Planner's alone, so Settings has no Focus. Clear objects removes what was added and keeps the Sun and
    /// the Moon, and the Planner's focus with them.
    /// </summary>
    [AvaloniaFact]
    public async Task CelestialObjectsAreOrderedWithArrowsAndClearedWithoutLosingFocus()
    {
        var page = SettingsPage("Celestial objects");
        Assert.DoesNotContain(page.Descendants(), element => element.Attribute("Command")?.Value == "{Binding FocusCommand}");
        Assert.DoesNotContain(page.Descendants(), element => element.Attribute("Classes")?.Value == "focusedState");
        Assert.All(page.Descendants().Where(element => element.Attribute("Classes")?.Value == "reorder"),
            button => Assert.Equal("Path", button.Elements().Single().Name.LocalName));
        Assert.Contains(page.Descendants(), element => element.Attribute("Content")?.Value == "Clear objects");
        Assert.Equal("Clear objects", Noctaxis.Core.Localisation.Strings.Get("Settings_Celestial_RestoreDefaultObjects_Button"));

        var catalogue = new OpenNgcTargetCatalogue();
        var dialogs = new FakeDialogs { ConfirmDestructiveResult = true };
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, DefaultStore(), new FakeExporter(), dialogs: dialogs);
        await vm.InitializeAsync();
        vm.AddSettingsCelestialObjectCommand.Execute(catalogue.Get("M31"));
        var m31 = vm.CelestialObjects.Single(item => item.TargetId == "openngc:NGC0224");
        m31.MoveUpCommand.Execute(null);
        Assert.Equal(1, vm.CelestialObjects.IndexOf(m31));
        Target(vm, "moon").FocusCommand.Execute(null);
        Assert.Equal("moon", vm.Session.TargetId);

        await vm.RestoreCelestialDefaultsCommand.ExecuteAsync(null);
        Assert.Equal("Clear objects?", Assert.Single(dialogs.DestructiveRequests).Title);
        Assert.Equal(["sun", "moon"], vm.CelestialObjects.Select(item => item.TargetId));
        Assert.Equal("moon", vm.Session.TargetId);

        // A focused object that is cleared hands the focus to the Sun.
        vm.AddSettingsCelestialObjectCommand.Execute(catalogue.Get("M31"));
        vm.CelestialObjects.Single(item => item.TargetId == "openngc:NGC0224").FocusCommand.Execute(null);
        await vm.RestoreCelestialDefaultsCommand.ExecuteAsync(null);
        Assert.Equal("sun", vm.Session.TargetId);
    }

    // ---- Data ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The cache bar is its real use against its real limit, full and no further when a download takes it briefly
    /// past the limit, and says the sizes and file count in words beside it.
    /// </summary>
    [Fact]
    public void TheCacheBarIsItsUseAgainstItsLimit()
    {
        const long GiB = 1024L * 1024 * 1024;
        Assert.Equal(50, MainViewModel.CachePercent(GiB, 2 * GiB), 9);
        Assert.Equal(100, MainViewModel.CachePercent(3 * GiB, 2 * GiB));
        Assert.Equal(0, MainViewModel.CachePercent(0, 2 * GiB));
        Assert.Equal(0, MainViewModel.CachePercent(GiB, 0));
        Assert.Equal("1.00 GiB", MainViewModel.CacheSize(GiB));
        Assert.Equal("512 MiB", MainViewModel.CacheSize(GiB / 2));
    }

    /// <summary>
    /// The atlas's one install action says what it will do, never "Install / Repair / Reinstall"; the saved map
    /// images' three repairs are one ordinary button opening a menu of them.
    /// </summary>
    [AvaloniaFact]
    public async Task DataActionsAreNamedForWhatTheyDo()
    {
        var (vm, window, _) = await OpenSettingsAsync("builtin.noctaxis", 1, 1440);
        try
        {
            window.SelectSettingsCategory("Data");
            Assert.Equal("Install", vm.LightPollutionInstallLabel);
            Assert.Equal(vm.LightPollutionInstallLabel, window.FindControl<Button>("LightPollutionInstallButton")!.Content);
            var page = SettingsPage("Data");
            Assert.DoesNotContain(page.Descendants(), element => element.Name.LocalName == "Expander");
            var repair = page.Descendants().Single(element => element.Attribute(System.Xml.Linq.XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value == "RepairSavedMapImagesButton");
            Assert.Equal(3, repair.Descendants().Count(element => element.Name.LocalName == "MenuItem"));
            Assert.DoesNotContain("remains usable", Noctaxis.Core.Localisation.Strings.Get("Settings_Data_TerrainMapzenTilezen_Description"));
            var bar = window.FindControl<ProgressBar>("TerrainCacheCapacityBar")!;
            Assert.Equal((0, 100), (bar.Minimum, bar.Maximum));
            Assert.False(bar.IsIndeterminate);
        }
        finally { window.Close(); }
    }

    // ---- Diagnostics --------------------------------------------------------------------------------------

    /// <summary>
    /// The terrain diagnostics are a developer tool: absent from Settings → Diagnostics (which everyone sees, for
    /// Export Logs) and doing nothing in a normal run, even with the old preference saved on, and back, with
    /// everything beneath them, in a run started for them.
    /// </summary>
    [AvaloniaFact]
    public async Task TerrainDiagnosticsAreForDevelopersOnly()
    {
        var (vm, window, categories) = await OpenSettingsAsync("builtin.noctaxis", 1, 1440);
        try
        {
            var tab = window.FindControl<TabItem>("SettingsDiagnosticsTab")!;
            var developer = window.FindControl<StackPanel>("DeveloperDiagnostics")!;
            Assert.True(tab.IsVisible);
            Assert.False(developer.IsVisible);
            vm.SettingsTerrainDebugOverlay = true;
            Assert.False(vm.ShowTerrainDebugOverlay);
            vm.IsDeveloperDiagnosticsEnabled = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(developer.IsVisible);
            Assert.True(vm.ShowTerrainDebugOverlay);
            Assert.DoesNotContain("minimap", Noctaxis.Core.Localisation.Strings.Get("Settings_Diagnostics_RequiresTerrainCalculations_Description"));
            Assert.DoesNotContain("minimap", Noctaxis.Core.Localisation.Strings.Get("Settings_Diagnostics_BearingDistanceSampled_Description"));
        }
        finally { window.Close(); }
    }
}
