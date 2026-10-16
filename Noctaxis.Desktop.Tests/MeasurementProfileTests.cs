using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Localisation;
using Noctaxis.Core.Measurements;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Mapping;
using Noctaxis.Desktop.Mapping.Vector;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// Language, regional format and measurement profile are three separate things: the language picks
/// the text and the map's place names, the operating system's region writes the numbers, and the
/// user's measurement profile picks the units.
/// </summary>
public sealed partial class MainViewModelTests
{
    private async Task<ViewModels.MainViewModel> CreateMeasuredViewModelAsync(FakeStore store)
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter());
        await vm.InitializeAsync();
        vm.ShowPlannerCommand.Execute(null);
        await WaitUntilAsync(() => vm.Snapshot?.Weather.Conditions is not null, TimeSpan.FromSeconds(5));
        return vm;
    }

    // The synthetic forecast: 12 °C, dew point 7 °C, wind 2 m/s gusting 4 m/s, visibility 20 km, no rain.
    [Theory]
    [InlineData("Metric", "Temperature 12 °C", "Dew point 7 °C", "Wind speed 7 km/h", "Wind gusts 14 km/h", "Visibility 20.0 km", "Precipitation 0.0 mm")]
    [InlineData("UK", "Temperature 12 °C", "Dew point 7 °C", "Wind speed 4 mph", "Wind gusts 9 mph", "Visibility 12.4 mi", "Precipitation 0.0 mm")]
    [InlineData("Imperial", "Temperature 54 °F", "Dew point 45 °F", "Wind speed 4 mph", "Wind gusts 9 mph", "Visibility 12.4 mi", "Precipitation 0.00 in")]
    public async Task TheWeatherIsShownInTheProfilesUnits(string units, params string[] expected)
    {
        var vm = await CreateMeasuredViewModelAsync(DefaultStore(new AppSettings(Units: units)));
        Assert.NotNull(vm.Snapshot?.Weather.Conditions);
        var lines = TestCulture.InRegion("en-GB", () => vm.ConfiguredWeatherDetails).Split('\n');
        Assert.All(expected, line => Assert.Contains(line, lines));
        // Quantities the profile does not govern read the same in every profile.
        Assert.Contains("Wind direction 180°", lines);
        Assert.Contains(lines, line => line.Contains("70%"));
    }

    [Fact]
    public async Task ANewInstallationStartsInUkMixedAndAnExplicitChoiceIsKept()
    {
        var fresh = await CreateMeasuredViewModelAsync(DefaultStore());
        Assert.Equal("UK", fresh.SettingsUnits);
        Assert.Equal(MeasurementSystem.Uk, fresh.Measurements);
        Assert.Equal(new[] { "Metric", "UK", "Imperial" }, fresh.UnitsOptions);

        foreach (var saved in new[] { "Metric", "Imperial", "UK" })
            Assert.Equal(saved, (await CreateMeasuredViewModelAsync(DefaultStore(new AppSettings(Units: saved)))).SettingsUnits);
    }

    [Fact]
    [CoversSettingsInput("SettingsCameraHeightAboveGround")]
    [CoversSettingsInput("SettingsWeatherCacheDistanceDisplay")]
    public async Task HeightsAndDistancesAreEditedInTheProfilesUnitsAndStoredInSi()
    {
        var store = DefaultStore(new AppSettings(Units: "Metric", CameraHeightAboveGroundMetres: 1.7,
            Weather: new WeatherSettings(CacheDistanceKilometres: 5)));
        var vm = await CreateMeasuredViewModelAsync(store);

        Assert.Equal((1.7, "Camera height above ground (m)", "Camera height above ground in metres"),
            (vm.SettingsCameraHeightAboveGround, vm.CameraHeightLabel, vm.CameraHeightAccessibleName));
        Assert.Equal((5d, "Reuse forecasts within", "km", "Forecast reuse distance in kilometres"),
            (vm.SettingsWeatherCacheDistanceDisplay, vm.WeatherCacheDistanceLabel, vm.WeatherCacheDistanceUnit, vm.WeatherCacheDistanceAccessibleName));

        // UK Mixed: heights stay in metres, the long distance is in miles.
        var notified = new List<string?>();
        vm.PropertyChanged += (_, e) => notified.Add(e.PropertyName);
        vm.SettingsUnits = "UK";
        await vm.SettleSettingsAsync();
        Assert.Equal((1.7, "Camera height above ground (m)"), (vm.SettingsCameraHeightAboveGround, vm.CameraHeightLabel));
        Assert.Equal((3.1, "mi", "Forecast reuse distance in miles"),
            (vm.SettingsWeatherCacheDistanceDisplay, vm.WeatherCacheDistanceUnit, vm.WeatherCacheDistanceAccessibleName));
        Assert.Subset(new HashSet<string?>(notified), new HashSet<string?>
        {
            nameof(vm.Measurements), nameof(vm.ConfiguredWeatherDetails), nameof(vm.GroundObstructionText), nameof(vm.FramingVisibilityStatus),
            nameof(vm.EffectiveObserverAltitudeText), nameof(vm.EffectiveCameraAltitudeShortText), nameof(vm.EffectiveCameraAltitudeDetail),
            nameof(vm.SettingsWeatherCacheDistanceDisplay), nameof(vm.WeatherCacheDistanceLabel), nameof(vm.CameraHeightLabel)
        });

        // Imperial: feet and miles. Changing the unit changes nothing that is stored.
        vm.SettingsUnits = "Imperial";
        await vm.SettleSettingsAsync();
        Assert.Equal((5.6, "Camera height above ground (ft)", "Camera height above ground in feet"),
            (vm.SettingsCameraHeightAboveGround, vm.CameraHeightLabel, vm.CameraHeightAccessibleName));
        Assert.Equal(1.7, store.State.Settings.CameraHeightAboveGroundMetres);
        Assert.Equal(5, store.State.Settings.EffectiveWeather.CacheDistanceKilometres);
        // A control writing back the value it was given (focus loss, a redraw) is not an edit.
        vm.SettingsCameraHeightAboveGround = 5.6;
        vm.SettingsWeatherCacheDistanceDisplay = 3.1;
        await vm.SettleSettingsAsync();
        Assert.Equal(1.7, store.State.Settings.CameraHeightAboveGroundMetres);
        Assert.Equal(5, store.State.Settings.EffectiveWeather.CacheDistanceKilometres);

        // An edit is converted back to SI; a value beyond the limit is held at the limit.
        vm.SettingsCameraHeightAboveGround = 10;
        vm.SettingsWeatherCacheDistanceDisplay = 10;
        await vm.SettleSettingsAsync();
        Assert.Equal(3.048, store.State.Settings.CameraHeightAboveGroundMetres, 9);
        Assert.Equal(16.09344, store.State.Settings.EffectiveWeather.CacheDistanceKilometres, 9);
        Assert.Equal((10d, 10d), (vm.SettingsCameraHeightAboveGround, vm.SettingsWeatherCacheDistanceDisplay));
        vm.SettingsCameraHeightAboveGround = 1_000;
        vm.SettingsWeatherCacheDistanceDisplay = 99;
        await vm.SettleSettingsAsync();
        Assert.Equal(AppSettings.MaximumCameraHeightAboveGroundMetres, store.State.Settings.CameraHeightAboveGroundMetres);
        Assert.Equal(100, store.State.Settings.EffectiveWeather.CacheDistanceKilometres);
        Assert.Equal((328.1, 62.1), (vm.SettingsCameraHeightAboveGround, vm.SettingsWeatherCacheDistanceDisplay));
    }

    [Theory]
    [InlineData("Metric", 100d)]
    [InlineData("UK", 100d)]
    // 100 m is 328.08… ft: the control's limit is rounded up to its own decimal place so that the
    // canonical limit itself can be typed, and 100 m remains the rule.
    [InlineData("Imperial", 328.1)]
    public async Task TheCameraHeightMaximumIsTheCanonicalMetresInTheProfilesUnit(string units, double maximum)
    {
        var store = DefaultStore(new AppSettings(Units: units));
        var vm = await CreateMeasuredViewModelAsync(store);
        Assert.Equal(100, AppSettings.MaximumCameraHeightAboveGroundMetres);
        Assert.Equal(maximum, vm.CameraHeightMaximum);
        Assert.True(vm.CameraHeightMaximum >= MeasurementProfile.For(units).Height.FromBase(AppSettings.MaximumCameraHeightAboveGroundMetres));

        // The limit the control shows is accepted, and is exactly the canonical limit once converted.
        vm.SettingsCameraHeightAboveGround = vm.CameraHeightMaximum;
        await vm.SettleSettingsAsync();
        Assert.Equal(AppSettings.MaximumCameraHeightAboveGroundMetres, store.State.Settings.CameraHeightAboveGroundMetres);
        // Anything above it is held at the canonical limit, not at the rounded display limit.
        vm.SettingsCameraHeightAboveGround = vm.CameraHeightMaximum + 50;
        await vm.SettleSettingsAsync();
        Assert.Equal(AppSettings.MaximumCameraHeightAboveGroundMetres, store.State.Settings.CameraHeightAboveGroundMetres);
        Assert.Equal(AppSettings.MinimumCameraHeightAboveGroundMetres, 0);
        vm.SettingsCameraHeightAboveGround = -5;
        await vm.SettleSettingsAsync();
        Assert.Equal(0, store.State.Settings.CameraHeightAboveGroundMetres);
    }

    [Fact]
    public async Task SwitchingProfileMovesTheLimitAndTheValueWithoutChangingWhatIsStored()
    {
        var store = DefaultStore(new AppSettings(Units: "Metric", CameraHeightAboveGroundMetres: 100));
        var vm = await CreateMeasuredViewModelAsync(store);
        Assert.Equal((100d, 100d), (vm.CameraHeightMaximum, vm.SettingsCameraHeightAboveGround));

        // At the limit in metres, the control's value and maximum both become feet at the same moment.
        // The value the control writes back as it re-clamps must not be taken as an edit.
        vm.SettingsUnits = "Imperial";
        await vm.SettleSettingsAsync();
        Assert.Equal((328.1, 328.1), (vm.CameraHeightMaximum, vm.SettingsCameraHeightAboveGround));
        Assert.Equal(100, store.State.Settings.CameraHeightAboveGroundMetres);

        vm.SettingsUnits = "UK";
        await vm.SettleSettingsAsync();
        Assert.Equal((100d, 100d), (vm.CameraHeightMaximum, vm.SettingsCameraHeightAboveGround));
        Assert.Equal(100, store.State.Settings.CameraHeightAboveGroundMetres);
        Assert.Equal(5, store.State.Settings.EffectiveWeather.CacheDistanceKilometres);
    }

    [Theory]
    [InlineData("Metric", "Terrain disabled · 0 m MSL fallback")]
    [InlineData("UK", "Terrain disabled · 0 m MSL fallback")]
    [InlineData("Imperial", "Terrain disabled · 0 ft MSL fallback")]
    public async Task TheTerrainDisabledFallbackNamesItsUnit(string units, string expected)
    {
        var vm = await CreateMeasuredViewModelAsync(DefaultStore(new AppSettings(Units: units, EnableTerrainCalculations: false)));
        Assert.Equal(expected, TestCulture.InRegion("en-GB", () => vm.GroundElevationSourceText));
        // The same wording explains the altitude on the map overlay while terrain is off.
        Assert.EndsWith(expected, TestCulture.InRegion("en-GB", () => vm.EffectiveCameraAltitudeDetail));
        Assert.Equal("m", MeasurementProfile.Metric.Height.Symbol);
    }

    [Fact]
    public async Task ChangingTheProfileRewordsTheFallbackAtOnce()
    {
        var vm = await CreateMeasuredViewModelAsync(DefaultStore(new AppSettings(Units: "UK", EnableTerrainCalculations: false)));
        var notified = new List<string?>();
        vm.PropertyChanged += (_, e) => notified.Add(e.PropertyName);
        Assert.Equal("Terrain disabled · 0 m MSL fallback", TestCulture.InRegion("en-GB", () => vm.GroundElevationSourceText));

        vm.SettingsUnits = "Imperial";
        await vm.SettleSettingsAsync();

        Assert.Equal("Terrain disabled · 0 ft MSL fallback", TestCulture.InRegion("en-GB", () => vm.GroundElevationSourceText));
        Assert.Contains(nameof(vm.GroundElevationSourceText), notified);
        Assert.Contains(nameof(vm.EffectiveCameraAltitudeDetail), notified);
    }

    [Theory]
    [InlineData("Metric", "m ASL", 1, "m")]
    [InlineData("UK", "m ASL", 1, "m")]
    [InlineData("Imperial", "ft ASL", 1 / 0.3048, "ft")]
    public async Task ElevationIsShownInTheProfilesHeightUnit(string units, string unitText, double scale, string symbol)
    {
        // A ground elevation that does not wait on terrain data: 6.3 m, under a 2 m camera.
        var session = PlanningSession.Default(NodaTime.Instant.FromUtc(2024, 1, 1, 0, 0), "UTC") with
        {
            ObserverElevation = new ObserverElevationState(ManualGroundElevationOverrideAslMetres: 6.3)
        };
        var vm = await CreateMeasuredViewModelAsync(new FakeStore(new Noctaxis.Core.Persistence.PersistedState(4,
            new AppSettings(Units: units, CameraHeightAboveGroundMetres: 2), [], session, null)));
        Assert.Equal(6.3, vm.ResolvedGroundElevationMetres!.Value, 6);
        // The one sum: ground plus camera height.
        Assert.Equal(8.3, vm.EffectiveCameraAltitudeMetres!.Value, 6);
        static string F1(double value) => value.ToString("F1", CultureInfo.CurrentCulture);

        // The overlay's line, in the profile's height unit.
        Assert.Equal(TestCulture.InRegion("en-GB", () => $"{F1(8.3 * scale)} {unitText}"),
            TestCulture.InRegion("en-GB", () => vm.EffectiveCameraAltitudeShortText));
        // The full sentence, unchanged, which the overlay's tooltip opens with.
        var expected = TestCulture.InRegion("en-GB", () => $"Effective camera altitude: {F1(8.3 * scale)} {symbol} ASL " +
            $"(ground + {F1(2 * scale)} {symbol} camera height)");
        Assert.Equal(expected, TestCulture.InRegion("en-GB", () => vm.EffectiveObserverAltitudeText));
        // The tooltip adds the ground under the camera, and where that number came from.
        var detail = TestCulture.InRegion("en-GB", () => vm.EffectiveCameraAltitudeDetail);
        Assert.StartsWith(expected, detail);
        Assert.Contains(TestCulture.InRegion("en-GB", () => $"Ground elevation: {F1(6.3 * scale)} {unitText}"), detail);
        Assert.EndsWith(vm.GroundElevationSourceText, detail);
    }

    [Fact]
    public async Task CameraLensAndAngularValuesKeepTheirConventionalUnitsInEveryProfile()
    {
        var shown = new List<string[]>();
        foreach (var units in MeasurementUnits.Options)
        {
            var vm = await CreateMeasuredViewModelAsync(DefaultStore(new AppSettings(Units: units)));
            shown.Add(TestCulture.InRegion("en-GB", () => new[]
            {
                vm.FieldOfViewText, vm.AltitudeText, vm.GroundHorizonAngleText, vm.TargetTerrainMarginText, vm.MoonDetails,
                string.Join("|", vm.Lenses.Select(lens => lens.DisplayName)), string.Join("|", vm.Cameras.Select(camera => camera.DisplayName)),
                vm.FocalLengthMinimum.ToString(CultureInfo.InvariantCulture), vm.FocalLengthMaximum.ToString(CultureInfo.InvariantCulture),
                Strings.Format("Planner_Camera_FocalLength_Format", 400d), Strings.Format("Planner_Camera_ZoomLens_Format", 70d, 200d),
                Strings.Format("Export_Card_Lens_Format", "Lens", 400d, "Landscape"),
                Strings.Get("Settings_Equipment_SensorWidthMm_Label"), Strings.Get("Settings_Equipment_MaximumFocalLengthMm_Label")
            }));
        }
        Assert.Equal(shown[0], shown[1]);
        Assert.Equal(shown[0], shown[2]);
        // A 400 mm lens is a 400 mm lens.
        Assert.Contains("400 mm", shown[2]);
        Assert.Contains("Zoom · 70–200 mm", shown[2]);
        Assert.DoesNotContain(shown[2], text => text.Contains(" in") && text.Contains("15.7"));
    }

    [Fact]
    public async Task LanguageAndMeasurementsDoNotChangeEachOtherOrTheRegion()
    {
        var store = DefaultStore(new AppSettings(Units: "Imperial"));
        var vm = await CreateMeasuredViewModelAsync(store);
        var region = CultureInfo.CurrentCulture;

        vm.SettingsLanguage = vm.LanguageOptions[0];
        await vm.SettleSettingsAsync();
        Assert.Equal(("Imperial", "en-GB"), (vm.Settings.Units, vm.Settings.Language));

        foreach (var units in new[] { "Metric", "UK", "Imperial" })
        {
            vm.SettingsUnits = units;
            await vm.SettleSettingsAsync();
            Assert.Equal((units, "en-GB"), (store.State.Settings.Units, store.State.Settings.Language));
            Assert.Equal(vm.LanguageOptions[0], vm.SettingsLanguage);
        }
        // Neither setting touches the regional culture; nothing in the view model sets a culture at all.
        Assert.Same(region, CultureInfo.CurrentCulture);
    }

    [Theory]
    [InlineData("en-GB", "12.4 mi", "0.00 in")]
    [InlineData("de-DE", "12,4 mi", "0,00 in")]
    [InlineData("tr-TR", "12,4 mi", "0,00 in")]
    [InlineData("fa-IR", null, null)]
    public async Task TheRegionChangesHowNumbersAreWrittenAndNeverTheUnitsOrTheLanguage(string region, string? visibility, string? precipitation)
    {
        var vm = await CreateMeasuredViewModelAsync(DefaultStore(new AppSettings(Units: "Imperial")));
        var lines = TestCulture.InRegion(region, () => vm.ConfiguredWeatherDetails).Split('\n');
        // English words, the region's digits and separators, the profile's units.
        Assert.Contains(lines, line => line.StartsWith("Visibility ", StringComparison.Ordinal) && line.EndsWith(" mi", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("Temperature ", StringComparison.Ordinal) && line.EndsWith(" °F", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("Precipitation ", StringComparison.Ordinal) && line.EndsWith(" in", StringComparison.Ordinal));
        if (visibility is not null) Assert.Contains("Visibility " + visibility, lines);
        if (precipitation is not null) Assert.Contains("Precipitation " + precipitation, lines);
        Assert.Equal("Imperial", vm.SettingsUnits);
        Assert.Equal("en-GB", vm.Settings.Language);
        Assert.Equal("English (United Kingdom)", TestCulture.InRegion(region, () => Strings.Get("Settings_Language_Name_en_GB")));
    }

    [AvaloniaFact]
    public async Task SettingsOffersMetricUkMixedAndImperialByNameAndNamesTheUnitBeingEdited()
    {
        var (vm, window) = await CreateLocalisedWindowAsync();
        try
        {
            var pages = (TabControl)((Grid)window.Content!).Children[1];
            pages.SelectedItem = pages.Items.OfType<TabItem>().Single(item => Equals(item.Header, Strings.Get("Shell_Settings_Tab")));
            var categories = window.FindControl<TabControl>("SettingsCategories")!;
            categories.SelectedItem = categories.Items.OfType<TabItem>().Single(item => Equals(item.Header, Strings.Get("Settings_General_Tab")));
            Dispatcher.UIThread.RunJobs();
            var radios = window.FindControl<WrapPanel>("MeasurementChoices")!.Children.OfType<RadioButton>().ToArray();
            Assert.Equal(new[] { "Metric", "UK Mixed", "Imperial" }, radios.Select(radio => (string)radio.Content!));
            Assert.Equal(new[] { Strings.Get("Settings_General_Metric_Option"), Strings.Get("Settings_General_UK_Option"), Strings.Get("Settings_General_Imperial_Option") },
                radios.Select(radio => AutomationProperties.GetName(radio)));
            Assert.True(radios[1].IsChecked); // UK Mixed, the default.

            categories.SelectedItem = categories.Items.OfType<TabItem>().Single(item => Equals(item.Header, Strings.Get("Settings_Equipment_Tab")));
            Dispatcher.UIThread.RunJobs();
            var height = categories.GetVisualDescendants().OfType<NumericUpDown>().Single(control =>
                AutomationProperties.GetName(control) == "Camera height above ground in metres");
            Assert.Equal((decimal)AppSettings.DefaultCameraHeightAboveGroundMetres, height.Value);
            vm.SettingsUnits = "Imperial";
            await vm.SettleSettingsAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Camera height above ground in feet", AutomationProperties.GetName(height));
            Assert.Equal(5.6m, height.Value);
            Assert.Equal(AppSettings.DefaultCameraHeightAboveGroundMetres, vm.Settings.CameraHeightAboveGroundMetres);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task MeasurementsAndRegionNeverRedrawTheMapsPlaceNames()
    {
        var (vm, window) = await CreateLocalisedWindowAsync();
        try
        {
            var map = window.FindControl<PlannerMapView>("PlannerMap")!;
            var composition = map.CompositionForTesting;
            var basemap = Assert.IsType<OpenFreeMapBasemap>(composition.ActiveBasemap);
            var layers = composition.Map.Layers.OfType<ThemedBasemapTileLayer>().ToArray();
            var before = (basemap.Source.LabelVersion, basemap.Source.StyleVersion, layers.Sum(layer => layer.Invalidations));

            foreach (var units in new[] { "Metric", "Imperial", "UK" })
            {
                vm.SettingsUnits = units;
                await vm.SettleSettingsAsync();
                Dispatcher.UIThread.RunJobs();
            }
            TestCulture.InRegion("de-DE", () => { Dispatcher.UIThread.RunJobs(); return vm.ConfiguredWeatherDetails; });

            Assert.Equal(before, (basemap.Source.LabelVersion, basemap.Source.StyleVersion, layers.Sum(layer => layer.Invalidations)));
            // The map's language is still the application language, whatever the region or the units.
            Assert.Equal(AppLanguages.Current, basemap.LabelLanguage);
            Assert.Equal(vm.Settings.Language, basemap.LabelLanguage);
        }
        finally { window.Close(); }
    }
}
