using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Persistence;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Services;
using Noctaxis.Desktop.ViewModels;
using Noctaxis.Desktop.Views;
using NodaTime;

namespace Noctaxis.Desktop.Tests;

/// <summary>Automatic persistence, commit timing, restart-required settings and Settings accessibility.</summary>
public sealed partial class MainViewModelTests
{
    private static FakeStore DefaultStore(AppSettings? settings = null) => new(new PersistedState(4, settings ?? new AppSettings(), [],
        PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null));

    [Fact]
    public async Task ImmediateSetting_AppliesAtOnceAndSavesWithoutAnExplicitSave()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var store = DefaultStore();
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter());
        await vm.InitializeAsync();

        vm.SettingsUnits = "Imperial";
        Assert.Equal("Imperial", vm.Settings.Units);          // Applied before any save completes.
        await vm.WaitForPendingSavesAsync();
        Assert.Equal("Imperial", store.State.Settings.Units); // Saved with no Save command.
        Assert.Equal("Imperial", vm.SettingsUnits);           // The control shows the saved value.
    }

    [Fact]
    public async Task DeferredSetting_ShowsTheRequestedValueAndIsAppliedOnceAfterEditingPauses()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new FakePlanning(catalogue);
        var store = DefaultStore();
        var vm = CreateViewModel(planning, catalogue, store, new FakeExporter());
        await vm.InitializeAsync();
        vm.ShowPlannerCommand.Execute(null);
        await vm.WaitForPlannerRefreshAsync();
        vm.DeferredSettingsDelay = TimeSpan.FromMilliseconds(150);
        var calculations = planning.SnapshotCalculations;

        // Spinning a numeric input produces several values; terrain is recalculated once, for the last.
        foreach (var height in new[] { 1.8, 1.9, 2.0, 2.1 }) vm.SettingsCameraHeightAboveGroundMetres = height;
        Assert.Equal(2.1, vm.SettingsCameraHeightAboveGroundMetres);
        Assert.Equal(AppSettings.DefaultCameraHeightAboveGroundMetres, vm.Settings.EffectiveCameraHeightAboveGroundMetres);

        var deadline = Stopwatch.StartNew();
        while (vm.Settings.EffectiveCameraHeightAboveGroundMetres != 2.1 && deadline.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(20);
        await vm.SettleSettingsAsync();
        await vm.WaitForPlannerRefreshAsync();
        Assert.Equal(2.1, vm.Settings.EffectiveCameraHeightAboveGroundMetres);
        Assert.Equal(2.1, store.State.Settings.EffectiveCameraHeightAboveGroundMetres);
        Assert.Equal(calculations + 1, planning.SnapshotCalculations);
    }

    [Fact]
    public async Task ClosingDuringADeferredSettingsPause_SavesTheLatestRequestedValue()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var store = DefaultStore();
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter());
        await vm.InitializeAsync();
        vm.DeferredSettingsDelay = TimeSpan.FromHours(1);

        vm.SettingsTerrainCacheLimitGiB = .5;
        vm.SettingsWeatherCacheDistance = 12;
        Assert.Equal(2L * 1024 * 1024 * 1024, store.State.Settings.TerrainCacheLimitBytes);

        // MainWindow.OnClosing flushes; the pending values must not be lost.
        Assert.True(await vm.FlushAsync());
        Assert.Equal(536870912, store.State.Settings.TerrainCacheLimitBytes);
        Assert.Equal(12, store.State.Settings.EffectiveWeather.CacheDistanceKilometres);
    }

    [Fact]
    public async Task EnablingAWeatherValue_RefreshesTheForecastSoTheNewValueIsRequested()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new FakePlanning(catalogue);
        var vm = CreateViewModel(planning, catalogue, DefaultStore(new AppSettings(
            Weather: new WeatherSettings([WeatherField.TotalCloudCover]))), new FakeExporter());
        await vm.InitializeAsync();
        vm.ShowPlannerCommand.Execute(null);
        await vm.WaitForPlannerRefreshAsync();
        var refreshes = planning.ForcedRefreshes;

        foreach (var option in vm.WeatherFieldOptions.Where(option => option.Field is WeatherField.Moonrise or WeatherField.Moonset))
            option.IsEnabled = true;
        await vm.SettleSettingsAsync();

        Assert.Equal(refreshes + 1, planning.ForcedRefreshes);
        Assert.Contains(WeatherField.Moonrise, vm.Settings.EffectiveWeather.EffectiveFields);
    }

    [Fact]
    public async Task InvalidEquipmentDraft_IsShownButNeverSaved()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var store = DefaultStore();
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter());
        await vm.InitializeAsync();
        var saved = store.State.Settings.Equipment;

        var camera = vm.EquipmentCameraEditors[0];
        camera.DisplayName = "";
        await vm.SettleSettingsAsync();
        Assert.NotNull(vm.EquipmentValidationMessage);
        Assert.Same(saved, store.State.Settings.Equipment);

        camera.DisplayName = "Renamed";
        await vm.SettleSettingsAsync();
        Assert.Null(vm.EquipmentValidationMessage);
        Assert.Equal("Renamed", store.State.Settings.Equipment!.Cameras![0].DisplayName);
        Assert.Contains(vm.Cameras, item => item.DisplayName == "Renamed"); // The Planner picks it up.
    }

    [Fact]
    public async Task NoCurrentSettingRequiresRestart()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, DefaultStore(), new FakeExporter());
        await vm.InitializeAsync();
        vm.SettingsUnits = "UK"; vm.SettingsTimeSnapMinutes = 20; vm.SettingsEnableTerrainCalculations = false;
        vm.SettingsTerrainCacheLimitGiB = 3; vm.SettingsTerrainDebugOverlay = true;
        await vm.SettleSettingsAsync();
        Assert.False(vm.HasPendingRestart);
        Assert.Empty(vm.PendingRestartSettings);
    }

    [Fact]
    [CoversSettingsInput("RestartApplicationCommand")]
    public async Task RestartRequiredSetting_IsSavedAtOnceAndPendingOnlyWhileItDiffersFromTheActiveValue()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var store = DefaultStore();
        var restart = new FakeRestartService(ApplicationRestartResult.Started);
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter(), restartService: restart);
        await vm.InitializeAsync();
        vm.AddSettingDefinitionForTesting(new("TestRestartSetting", "Time slider snap interval", s => s.TimeSnapMinutes, RequiresRestart: true));
        var notified = new List<string?>();
        vm.PropertyChanged += (_, e) => notified.Add(e.PropertyName);
        Assert.False(vm.HasPendingRestart);

        vm.SettingsTimeSnapMinutes = 10;
        await vm.SettleSettingsAsync();
        Assert.True(vm.HasPendingRestart);
        Assert.Contains(nameof(vm.HasPendingRestart), notified);
        Assert.Equal(["Time slider snap interval"], vm.PendingRestartSettings);
        Assert.Contains("Time slider snap interval", vm.PendingRestartSummary);
        Assert.Equal(10, store.State.Settings.TimeSnapMinutes); // The requested value is saved immediately...
        Assert.Equal(5, vm.ActiveSettings.TimeSnapMinutes);      // ...while this process keeps the active one.

        vm.SettingsTimeSnapMinutes = 20;
        Assert.True(vm.HasPendingRestart);
        vm.SettingsTimeSnapMinutes = 5;                           // Back to the active value: nothing pending.
        Assert.False(vm.HasPendingRestart);
        Assert.Empty(vm.PendingRestartSummary);

        vm.SettingsTimeSnapMinutes = 15;
        await vm.RestartApplicationCommand.ExecuteAsync(null);
        Assert.Equal(1, restart.Requests);
        Assert.Equal(15, store.State.Settings.TimeSnapMinutes);  // Saved before the restart was requested.
        Assert.Null(vm.RestartStatus);
    }

    [Fact]
    public async Task RestartFailure_KeepsNoctaxisRunningAndSaysWhy()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var restart = new FakeRestartService(ApplicationRestartResult.Failed("the executable was not found"));
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, DefaultStore(), new FakeExporter(), restartService: restart);
        await vm.InitializeAsync();

        await vm.RestartApplicationCommand.ExecuteAsync(null);

        Assert.Equal(1, restart.Requests);
        Assert.Contains("the executable was not found", vm.RestartStatus);
        Assert.Contains("close and reopen", vm.RestartStatus);
        Assert.False(vm.IsRestarting);
        Assert.True(vm.RestartApplicationCommand.CanExecute(null));
    }

    [Fact]
    public async Task RestartIsNotAttemptedWhenSettingsCannotBeSaved()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var store = new FlakyStore(new PersistedState(4, new AppSettings(), [],
            PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null));
        var restart = new FakeRestartService(ApplicationRestartResult.Started);
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter(), restartService: restart);
        await vm.InitializeAsync();
        store.Fail = true;

        await vm.RestartApplicationCommand.ExecuteAsync(null);

        Assert.Equal(0, restart.Requests);
        Assert.Contains("could not be saved", vm.RestartStatus);
        Assert.True(vm.HasOperationalNotice);
    }

    [Fact]
    public void RestartService_RelaunchesTheSameCommandAndClosesOnlyAfterTheNewProcessStarted()
    {
        ProcessStartInfo? started = null;
        var shutdowns = 0;
        var service = new ApplicationRestartService(info => { started = info; return new Process(); }, () => shutdowns++,
            NullLogger<ApplicationRestartService>.Instance, processPath: "/opt/noctaxis/Noctaxis.Desktop",
            arguments: ["--flag", ApplicationRestartService.WaitForExitArgument, "12"], processId: 4242);

        Assert.True(service.Restart().Succeeded);
        Assert.Equal(1, shutdowns);
        Assert.Equal("/opt/noctaxis/Noctaxis.Desktop", started!.FileName);
        Assert.False(started.UseShellExecute);
        // An earlier wait argument is replaced by one naming this process.
        Assert.Equal(["--flag", ApplicationRestartService.WaitForExitArgument, "4242"], started.ArgumentList);

        // Paths as this operating system writes them: a Windows path is not one elsewhere ('\' is no separator).
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var host = Path.Combine(root, "dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var assembly = Path.Combine(root, "Noctaxis", "Noctaxis.Desktop.dll");
        var dotnet = ApplicationRestartService.CreateStartInfo(host, assembly, [], 7);
        Assert.Equal([assembly, ApplicationRestartService.WaitForExitArgument, "7"], dotnet.ArgumentList);

        var remaining = ApplicationRestartService.WithoutWaitArgument(["a", ApplicationRestartService.WaitForExitArgument, "99", "b"], out var waitFor);
        Assert.Equal(["a", "b"], remaining);
        Assert.Equal(99, waitFor);
    }

    [Fact]
    public void RestartService_FailureToStartLeavesThisInstanceRunning()
    {
        var shutdowns = 0;
        var throwing = new ApplicationRestartService(_ => throw new Win32Exception(2, "not found"), () => shutdowns++,
            NullLogger<ApplicationRestartService>.Instance, processPath: "noctaxis", arguments: [], processId: 1);
        var nothing = new ApplicationRestartService(_ => null, () => shutdowns++,
            NullLogger<ApplicationRestartService>.Instance, processPath: "noctaxis", arguments: [], processId: 1);
        var noPath = new ApplicationRestartService(_ => new Process(), () => shutdowns++,
            NullLogger<ApplicationRestartService>.Instance, processPath: "", arguments: [], processId: 1);

        Assert.False(throwing.Restart().Succeeded);
        Assert.False(nothing.Restart().Succeeded);
        Assert.False(noPath.Restart().Succeeded);
        Assert.Equal(0, shutdowns);
    }

    [Fact]
    public async Task LegacySettingsFileWithoutNewerFields_LoadsWithDefaultsAndSettingsStillSave()
    {
        await using var paths = new AtlasTestData();
        var store = new JsonUserDataStore(paths, NullLogger<JsonUserDataStore>.Instance);
        await store.SaveAsync(new PersistedState(1, new AppSettings(Units: "UK", SelectedTimeZoneId: "UTC", TimeSnapMinutes: 15), [],
            PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null), CancellationToken.None);
        // An early settings record: no appearance, equipment, framing, weather or light-pollution sections,
        // and a value this version does not know.
        var file = Path.Combine(store.StorageDirectory, "state.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(file))!;
        var settingsNode = json["Settings"]!.AsObject();
        foreach (var section in new[] { "Appearance", "Equipment", "CameraFraming", "Weather", "LightPollution", "CameraHeightAboveGroundMetres" })
            settingsNode.Remove(section);
        settingsNode["UnknownFutureOption"] = true;
        await File.WriteAllTextAsync(file, json.ToJsonString());
        var catalogue = new OpenNgcTargetCatalogue();
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter());
        await vm.InitializeAsync();

        Assert.Equal("UK", vm.SettingsUnits);
        Assert.Equal(15, vm.SettingsTimeSnapMinutes);
        Assert.Equal(AppSettings.DefaultCameraHeightAboveGroundMetres, vm.SettingsCameraHeightAboveGroundMetres);
        Assert.Equal("grayscale", vm.SettingsLightPollutionColorMap.Id);

        vm.SettingsTimeSnapMinutes = 20;
        await vm.SettleSettingsAsync();
        var reloaded = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(20, reloaded.Settings.TimeSnapMinutes);
        Assert.Equal("UK", reloaded.Settings.Units);
        Assert.Equal(AppSettings.DefaultCameraHeightAboveGroundMetres, reloaded.Settings.EffectiveCameraHeightAboveGroundMetres);
    }

    [AvaloniaFact]
    public void CommitSlider_DragPreviewsAndCommitsOnReleaseButKeyboardCommitsAtOnce()
    {
        var slider = new CommitSlider
        {
            Minimum = 100, Maximum = 200, TickFrequency = 10, SmallChange = 10, LargeChange = 10,
            IsSnapToTickEnabled = true, Width = 300, CommittedValue = 100, HorizontalAlignment = HorizontalAlignment.Left
        };
        var commits = new List<double>();
        slider.PropertyChanged += (_, e) => { if (e.Property == CommitSlider.CommittedValueProperty) commits.Add(slider.CommittedValue); };
        var window = new Window { Width = 400, Height = 120, Content = slider };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(100, slider.Value);
            var thumb = slider.GetVisualDescendants().OfType<Thumb>().Single();
            var start = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), window)!.Value;
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(start + new Point(80, 0));
            window.MouseMove(start + new Point(160, 0));
            Dispatcher.UIThread.RunJobs();
            Assert.True(slider.Value > 100, "the thumb follows the drag");
            Assert.Equal(100, slider.CommittedValue);
            Assert.Empty(commits);
            window.MouseUp(start + new Point(160, 0), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(slider.Value, slider.CommittedValue);
            Assert.Single(commits);

            var committed = slider.CommittedValue;
            slider.Focus(NavigationMethod.Tab);
            window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(committed - 10, slider.CommittedValue);

            slider.CommittedValue = 180; // A change from the bound setting moves the thumb.
            Assert.Equal(180, slider.Value);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("builtin.noctaxis", AppearanceMode.Dark, 1.0)]
    [InlineData("builtin.noctaxis", AppearanceMode.Light, 2.0)]
    [InlineData("builtin.highcontrast", AppearanceMode.Dark, 2.0)]
    [InlineData("builtin.highcontrast", AppearanceMode.Light, 1.5)]
    [InlineData("supporter.noctaxis-neon", AppearanceMode.Dark, 2.0)]
    public async Task EverySettingsCategory_IsLabelledAndUnclippedAtLargeTextInEveryTheme(string family, AppearanceMode appearance, double textScale)
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var store = CreateSupporterStore(new AppSettings());
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter());
        await vm.InitializeAsync();
        var service = ((App)Application.Current!).Themes!;
        vm.AttachThemeService(service);
        // A pending restart shows the notice too, so it is checked with everything else.
        vm.AddSettingDefinitionForTesting(new("TestRestartSetting", "Time slider snap interval", s => s.TimeSnapMinutes, RequiresRestart: true));
        vm.SettingsTimeSnapMinutes = 10;
        await vm.ApplyAppearanceAsync(new(family, TextScale: textScale, AppearanceMode: appearance));
        var window = new MainWindow { Width = 900, Height = 1000, DataContext = vm };
        window.Show();
        try
        {
            var pages = (TabControl)((Grid)window.Content!).Children[1]; pages.SelectedIndex = 2;
            Dispatcher.UIThread.RunJobs();
            var notice = window.FindControl<Border>("PendingRestartNotice")!;
            Assert.True(notice.IsVisible);
            var restart = window.FindControl<Button>("RestartApplicationButton")!;
            Assert.Contains("accent", restart.Classes);
            Assert.True(restart.IsEffectivelyEnabled && restart.Focusable);
            AssertNothingClipped(notice, $"{family}/{appearance}/{textScale} restart notice");

            var categories = window.FindControl<TabControl>("SettingsCategories")!;
            // Diagnostics is a developer tool, so it is not among them in a normal run.
            Assert.Equal(["General", "Appearance", "Equipment", "Planner map", "Weather", "Celestial objects", "Data", "Supporter"],
                categories.Items.OfType<TabItem>().Where(tab => tab.IsVisible).Select(tab => (string?)tab.Header));
            foreach (var tab in categories.Items.OfType<TabItem>().Where(tab => tab.IsVisible))
            {
                window.SelectSettingsCategory((string)tab.Header!);
                foreach (var expander in window.GetVisualDescendants().OfType<Expander>().Where(e => e.IsEffectivelyVisible)) expander.IsExpanded = true;
                Dispatcher.UIThread.RunJobs();
                var page = (Control)categories.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.IsEffectivelyVisible);
                var context = $"{family}/{appearance}/{textScale} {tab.Header}";
                AssertNothingClipped(page, context);
                foreach (var input in page.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible &&
                             c is TextBox or NumericUpDown or ComboBox or Slider or ColorPicker &&
                             c.FindAncestorOfType<NumericUpDown>() is null && c.FindAncestorOfType<ColorPicker>() is null))
                    Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(input)),
                        $"{context}: {input.GetType().Name} has no accessible name");
                AppearancePolishTests.Capture(window, $"settings-{family}-{appearance}-{textScale:0.0}-{tab.Header}");
            }
        }
        finally { window.Close(); service.Apply(new()); }
    }

    /// <summary>Visible text never overflows its box, and nothing extends past the page's right edge.</summary>
    private static void AssertNothingClipped(Control root, string context)
    {
        var right = root.Bounds.Width;
        foreach (var text in root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)))
        {
            if (text.FindAncestorOfType<ComboBox>() is not null || text.FindAncestorOfType<NumericUpDown>() is not null) continue;
            Assert.True(text.TextLayout.Width <= text.Bounds.Width + 1, $"{context}: '{text.Text}' is clipped");
            var edge = text.TranslatePoint(new Point(text.Bounds.Width, 0), root)?.X ?? 0;
            Assert.True(edge <= right + 1, $"{context}: '{text.Text}' extends past the page edge");
        }
    }
}
