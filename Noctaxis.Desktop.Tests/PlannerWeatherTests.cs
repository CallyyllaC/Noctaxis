using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Persistence;
using Noctaxis.Desktop.ViewModels;
using NodaTime;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// Inspector → Weather as a short dashboard — Sky, Conditions, Light, Moon — showing the values the tab
/// always showed, in the same formatting, grouped onto fewer lines; twilight one tap away; the moon's
/// rise and set left to its own target details; and the source credited quietly.
/// </summary>
public sealed partial class MainViewModelTests
{
    private static System.Xml.Linq.XElement WeatherMarkup() => TestMarkup.LoadMainWindow().Descendants()
        .Single(element => NameOf(element) == "PlannerInspectorTabs").Elements()
        .Single(element => element.Name.LocalName == "TabItem" && element.Attribute("Header")?.Value == "Weather");

    private async Task<MainViewModel> WeatherPlannerAsync(params WeatherField[] enabled)
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var store = new FakeStore(new PersistedState(4, new AppSettings(Weather: new WeatherSettings(enabled.Length == 0 ? null : enabled)), [],
            PlanningSession.Default(Instant.FromUtc(2026, 10, 2, 21, 0), "UTC"), null));
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter());
        await vm.InitializeAsync(); vm.ShowPlannerCommand.Execute(null); await vm.WaitForPlannerRefreshAsync();
        await Until(() => vm.Snapshot?.Weather.Conditions is not null, "the weather should have arrived");
        return vm;
    }

    [Fact]
    public void WeatherIsSkyConditionsLightAndMoonWithoutTheMoonsRiseAndSet()
    {
        var weather = WeatherMarkup();
        var sections = weather.Descendants().Where(element => NameOf(element) is "WeatherSky" or "WeatherConditions" or "WeatherLight" or "WeatherMoon")
            .Select(NameOf).ToArray();
        Assert.Equal(["WeatherSky", "WeatherConditions", "WeatherLight", "WeatherMoon"], sections);
        Assert.Equal(["Sky", "Conditions", "Light", "Moon"], weather.Descendants()
            .Where(element => (string?)element.Attribute("Classes") == "section").Select(element => element.Attribute("Text")?.Value));
        // What the tab shows and binds, not what its comments say.
        var text = string.Join('\n', weather.DescendantsAndSelf().SelectMany(element => element.Attributes()).Select(attribute => attribute.Value));
        Assert.DoesNotContain("Moonrise", text);
        Assert.DoesNotContain("Moonset", text);
        Assert.DoesNotContain("ConfiguredWeatherDetails", text);
        // Twilight is behind the Light section's info button; Refresh and the source are in plain view.
        var twilight = weather.Descendants().Single(element => NameOf(element) == "WeatherTwilight");
        Assert.Contains(twilight.Ancestors(), element => element.Name.LocalName == "Flyout");
        Assert.Equal("{Binding RefreshWeatherCommand}", weather.Descendants().Single(element => NameOf(element) == "WeatherRefreshButton").Attribute("Command")?.Value);
        Assert.Equal("Weather data: Open-Meteo", weather.Descendants().Single(element => NameOf(element) == "WeatherSource").Attribute("Text")?.Value);
    }

    /// <summary>
    /// Every value the tab showed before is still shown, worded and formatted as it was — except the
    /// moon's rise and set — and twilight is in its detail.
    /// </summary>
    [Fact]
    public async Task TheDashboardShowsTheSameValuesInTheSameFormatting()
    {
        var vm = await WeatherPlannerAsync();
        var shown = string.Join("\n", vm.WeatherHeadline, vm.WeatherCloudText, vm.WeatherCloudLayersText, vm.WeatherVisibilityText,
            vm.WeatherPrecipitationText, vm.WeatherTemperatureText, vm.WeatherDewPointText, vm.WeatherWindText,
            vm.WeatherSunText, vm.WeatherDarknessText, vm.WeatherMoonText);
        var w = vm.Snapshot!.Weather.Conditions!;
        var units = vm.Settings.EffectiveMeasurementSystem;
        foreach (var value in new[]
        {
            "Clear", "Cloud 10%", "Low 2%", "Mid 3%", "High 5%",
            Noctaxis.Core.Measurements.MeasurementUnits.FormatVisibility(w.VisibilityKilometres, units),
            Noctaxis.Core.Measurements.MeasurementUnits.FormatPrecipitation(w.PrecipitationMillimetres, units),
            Noctaxis.Core.Measurements.MeasurementUnits.FormatTemperature(w.TemperatureCelsius, units), "70% humidity",
            "Dew point " + Noctaxis.Core.Measurements.MeasurementUnits.FormatTemperature(w.DewPointCelsius, units),
            Noctaxis.Core.Measurements.MeasurementUnits.FormatWindSpeed(w.WindSpeedMetresPerSecond, units),
            "Gusts " + Noctaxis.Core.Measurements.MeasurementUnits.FormatWindSpeed(w.WindGustMetresPerSecond, units)
        })
            Assert.Contains(value, shown);
        // The lines the old list had word for word are unchanged, and the moon's rise and set are not here.
        var before = vm.ConfiguredWeatherDetails.Split('\n');
        foreach (var line in before.Where(line => line.StartsWith("Visibility") || line.StartsWith("Dew point") ||
                     line.StartsWith("Sunrise") || line.StartsWith("Sunset") || line.StartsWith("Astronomical darkness") || line.StartsWith("Cloud ")))
            Assert.Contains(line, shown);
        foreach (var line in before.Where(line => line.Contains("twilight")))
            Assert.Contains(line, vm.WeatherTwilightText);
        Assert.Equal(3, vm.WeatherTwilightText.Split('\n').Length);
        Assert.DoesNotContain("Moonrise", shown + vm.WeatherTwilightText);
        Assert.DoesNotContain("Moonset", shown + vm.WeatherTwilightText);
        Assert.True(vm.HasWeatherSky && vm.HasWeatherConditions && vm.HasWeatherLight && vm.HasWeatherMoon);
        // No precipitation type to name.
        Assert.DoesNotContain("None", vm.WeatherPrecipitationText);
    }

    /// <summary>Each line carries only the fields chosen in Settings → Weather; a section with none is not shown.</summary>
    [Fact]
    public async Task TheDashboardShowsOnlyTheChosenFields()
    {
        var vm = await WeatherPlannerAsync(WeatherField.TotalCloudCover, WeatherField.HighCloudCover, WeatherField.AstronomicalDarkness);
        Assert.Equal("Cloud 10%", vm.WeatherCloudText);
        Assert.Equal("High 5%", vm.WeatherCloudLayersText);
        Assert.Equal("", vm.WeatherVisibilityText);
        Assert.False(vm.HasWeatherConditions);
        Assert.False(vm.HasWeatherMoon);
        Assert.True(vm.HasWeatherLight);
        Assert.Equal("", vm.WeatherSunText);
        Assert.False(vm.HasWeatherTwilight);
        Assert.StartsWith("Astronomical darkness before", vm.WeatherDarknessText);
    }

    /// <summary>Twilight opens from its info button by a tap or the keyboard; Refresh still refreshes.</summary>
    [AvaloniaFact]
    public async Task TwilightOpensFromItsInfoButtonAndRefreshStillRefreshes()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            await Until(() => vm.Snapshot?.Weather.Conditions is not null, "the weather should have arrived");
            window.FindControl<TabControl>("PlannerInspectorTabs")!.SelectedIndex = 1;
            Settle(window);
            var button = window.FindControl<Button>("WeatherTwilightButton")!;
            Assert.True(button.IsEffectivelyVisible);
            Assert.Equal("Twilight times", Avalonia.Automation.AutomationProperties.GetName(button));
            var flyout = Assert.IsType<Flyout>(button.Flyout);
            var at = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Settle(window);
            Assert.True(flyout.IsOpen, "a click should open the twilight times");
            var text = Assert.IsType<TextBlock>(flyout.Content).Text!;
            Assert.Contains("Civil twilight", text);
            Assert.Contains("Nautical twilight", text);
            flyout.Hide();
            Settle(window);
            Assert.True(button.Focus(NavigationMethod.Tab));
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Settle(window);
            Assert.True(flyout.IsOpen, "Enter should open the twilight times");
            flyout.Hide();

            // The visible lines are shown, the empty ones are not.
            Assert.True(window.FindControl<StackPanel>("WeatherSky")!.IsEffectivelyVisible);
            Assert.Equal(vm.WeatherHeadline, window.FindControl<TextBlock>("WeatherHeadline")!.Text);

            var refresh = window.FindControl<Button>("WeatherRefreshButton")!;
            Assert.True(refresh.Command!.CanExecute(null));
            refresh.Command.Execute(null);
            await Until(() => vm.StatusMessage == Noctaxis.Core.Localisation.Strings.Get("Planner_Weather_Refreshed"), "Refresh should refresh the weather");
        }
        finally { Close(window); }
    }

    /// <summary>The 16 compass points, each centred on its bearing, wrapping through north.</summary>
    [Theory]
    [InlineData(0, "N")] [InlineData(11.2, "N")] [InlineData(11.3, "NNE")] [InlineData(45, "NE")] [InlineData(90, "E")]
    [InlineData(180, "S")] [InlineData(198, "SSW")] [InlineData(202.5, "SSW")] [InlineData(270, "W")]
    [InlineData(348.7, "NNW")] [InlineData(348.8, "N")] [InlineData(337.5, "NNW")] [InlineData(360, "N")] [InlineData(-90, "W")]
    public void WindDirectionIsTheNearestCompassPoint(double degrees, string point) =>
        Assert.Equal(point, MainViewModel.CompassPoint(degrees));

    /// <summary>
    /// The wind line reads speed, Gusts, then an arrow turned to where the wind comes from and its compass
    /// point; the exact bearing is the direction's accessible name and tooltip, so it is never hover-only.
    /// </summary>
    [AvaloniaFact]
    public async Task WindDirectionIsAnArrowAndACompassPointWithTheExactBearingNamed()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            await Until(() => vm.Snapshot?.Weather.Conditions is not null, "the weather should have arrived");
            window.FindControl<TabControl>("PlannerInspectorTabs")!.SelectedIndex = 1;
            Settle(window);
            Assert.StartsWith("Wind ", vm.WeatherWindText);
            Assert.Contains("Gusts ", vm.WeatherWindText);
            Assert.DoesNotContain("°", vm.WeatherWindText);
            Assert.Equal(180, vm.WeatherWindDirectionDegrees);
            Assert.Equal("S", window.FindControl<TextBlock>("WeatherWindCompass")!.Text);
            var arrow = window.FindControl<Avalonia.Controls.Shapes.Path>("WeatherWindArrow")!;
            Assert.Equal(180, Assert.IsType<Avalonia.Media.RotateTransform>(arrow.RenderTransform).Angle);
            var direction = window.FindControl<StackPanel>("WeatherWindDirection")!;
            Assert.True(direction.IsEffectivelyVisible);
            Assert.Equal("Wind direction 180°", Avalonia.Automation.AutomationProperties.GetName(direction));
            Assert.Equal("Wind direction 180°", ToolTip.GetTip(direction));
        }
        finally { Close(window); }
    }

    /// <summary>Without the direction chosen, the line is speed and gusts alone, with no stray separator.</summary>
    [Fact]
    public async Task WindWithoutDirectionHasNoArrowOrSeparator()
    {
        var vm = await WeatherPlannerAsync(WeatherField.WindSpeed, WeatherField.WindGusts);
        Assert.False(vm.HasWeatherWindDirection);
        Assert.False(vm.HasWeatherWindSeparator);
        Assert.Equal("", vm.WeatherWindCompassText);
        var only = await WeatherPlannerAsync(WeatherField.WindDirection);
        Assert.Equal("Wind", only.WeatherWindText);
        Assert.False(only.HasWeatherWindSeparator);
        Assert.Equal("S", only.WeatherWindCompassText);
    }
}
