using System.ComponentModel;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Localisation;
using Noctaxis.Core.Measurements;

namespace Noctaxis.Desktop.ViewModels;

/// <summary>
/// Inspector → Weather as four short sections — Sky, Conditions, Light, Moon — with related values on
/// one line each. Every value and its formatting is the one the Weather tab already showed, and each
/// line carries only the fields switched on in Settings → Weather; a line with none of them is empty and
/// is not shown. Moonrise and moonset are not repeated here: the Moon's own target details have them.
/// </summary>
public partial class MainViewModel
{
    private static readonly string[] WeatherDashboardProperties =
    [
        nameof(WeatherHeadline), nameof(WeatherCloudText), nameof(WeatherCloudLayersText), nameof(WeatherVisibilityText),
        nameof(WeatherPrecipitationText), nameof(WeatherTemperatureText), nameof(WeatherDewPointText), nameof(WeatherWindText),
        nameof(WeatherSunText), nameof(WeatherDarknessText), nameof(WeatherTwilightText), nameof(WeatherMoonText),
        nameof(HasWeatherSky), nameof(HasWeatherConditions), nameof(HasWeatherLight), nameof(HasWeatherTwilight), nameof(HasWeatherMoon),
        nameof(WeatherWindDirectionDegrees), nameof(HasWeatherWindDirection), nameof(HasWeatherWindSeparator),
        nameof(WeatherWindCompassText), nameof(WeatherWindDirectionDetail)
    ];

    // The dashboard changes exactly when the configured weather text does: new weather, new fields, new units.
    // The Picture-in-Picture map's light pollution takes the palette the main map's does.
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(LightPollutionMapPreferences))
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(PlannerPipLightPollution)));
        // The time bar's date and zone name follow the plan's date, time and zone (a zone's short name can
        // change with the date, GMT to BST).
        if (e.PropertyName is nameof(LocalDate) or nameof(TimeText) or nameof(TimeZoneId))
        {
            if (e.PropertyName == nameof(TimeZoneId)) base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(TimelineZone)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(TimelineSelectedInstant)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(TimelineSelectionText)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(TimelineChartDescription)));
            if (e.PropertyName == nameof(LocalDate)) base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(PlannerDateText)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(PlannerTimeZoneText)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(PlannerTimeZoneDetail)));
        }
        // The Timeline's spoken description follows the moment chosen and the focused target.
        if (e.PropertyName is nameof(TimelineSelectionText) or nameof(Snapshot))
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(TimelineChartDescription)));
        // The bar's location line follows the name, the pin and the altitude (and its unit) as they change.
        if (e.PropertyName is nameof(LocationName) or nameof(PreviewObserver) or nameof(EffectiveCameraAltitudeShortText))
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(PlannerLocationText)));
        if (e.PropertyName is nameof(EffectiveCameraAltitudeDetail) or nameof(CurrentLocationAttribution) or nameof(EffectiveCameraAltitudeShortText))
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(PlannerLocationDetail)));
        if (e.PropertyName != nameof(ConfiguredWeatherDetails)) return;
        foreach (var name in WeatherDashboardProperties) base.OnPropertyChanged(new PropertyChangedEventArgs(name));
    }

    private WeatherConditions? DashboardWeather => Snapshot?.Weather.Conditions;
    private bool Shows(WeatherField field) => Settings.EffectiveWeather.IsEnabled(field);

    /// <summary>One line: a label, then its values joined; empty when none of its values is shown.</summary>
    private static string Line(string? label, params string?[] items)
    {
        var shown = items.Where(item => !string.IsNullOrWhiteSpace(item)).ToArray();
        if (shown.Length == 0) return "";
        var values = string.Join(" · ", shown);
        return label is null ? values : $"{label} {values}";
    }

    /// <summary>The provider's own summary of the conditions, or why there are none.</summary>
    public string WeatherHeadline => DashboardWeather is { } weather
        ? (weather.IsStale ? Strings.Get("Planner_Weather_Stale_Prefix") : "") + weather.Summary
        : Snapshot?.Weather.Message ?? Strings.Get("Weather_Loading");

    // ---- Sky ----------------------------------------------------------------------------------------

    public string WeatherCloudText => DashboardWeather is { } w && Shows(WeatherField.TotalCloudCover)
        ? Strings.Format("Planner_Weather_Cloud_Format", Value(w.CloudCoverPercent, "%")) : "";

    public string WeatherCloudLayersText => DashboardWeather is not { } w ? "" : Line(null,
        Shows(WeatherField.LowCloudCover) ? Strings.Format("Planner_Weather_LowLayer_Format", Value(w.LowCloudPercent, "%")) : null,
        Shows(WeatherField.MediumCloudCover) ? Strings.Format("Planner_Weather_MidLayer_Format", Value(w.MediumCloudPercent, "%")) : null,
        Shows(WeatherField.HighCloudCover) ? Strings.Format("Planner_Weather_HighLayer_Format", Value(w.HighCloudPercent, "%")) : null);

    public string WeatherVisibilityText => DashboardWeather is { } w && Shows(WeatherField.Visibility)
        ? Strings.Format("Planner_Weather_Visibility_Format", MeasurementUnits.FormatVisibility(w.VisibilityKilometres, Settings.EffectiveMeasurementSystem)) : "";

    public string WeatherPrecipitationText => DashboardWeather is not { } w ? "" : Line(Strings.Get("Planner_Weather_Precipitation_Label"),
        Shows(WeatherField.PrecipitationProbability) ? Value(w.PrecipitationProbabilityPercent, "%") : null,
        Shows(WeatherField.PrecipitationAmount) ? MeasurementUnits.FormatPrecipitation(w.PrecipitationMillimetres, Settings.EffectiveMeasurementSystem) : null,
        // The type only when there is one to name.
        Shows(WeatherField.PrecipitationType) && w.PrecipitationType is { Length: > 0 } type &&
            !type.Equals("None", StringComparison.OrdinalIgnoreCase) ? type : null);

    public bool HasWeatherSky => WeatherCloudText != "" || WeatherCloudLayersText != "" || WeatherVisibilityText != "" || WeatherPrecipitationText != "";

    // ---- Conditions ---------------------------------------------------------------------------------

    public string WeatherTemperatureText => DashboardWeather is not { } w ? "" : Line(null,
        Shows(WeatherField.Temperature) ? MeasurementUnits.FormatTemperature(w.TemperatureCelsius, Settings.EffectiveMeasurementSystem) : null,
        Shows(WeatherField.RelativeHumidity) ? Strings.Format("Planner_Weather_HumidityCompact_Format", Value(w.HumidityPercent, "%")) : null);

    public string WeatherDewPointText => DashboardWeather is { } w && Shows(WeatherField.DewPoint)
        ? Strings.Format("Planner_Weather_DewPoint_Format", MeasurementUnits.FormatTemperature(w.DewPointCelsius, Settings.EffectiveMeasurementSystem)) : "";

    /// <summary>
    /// The wind's speed and gusts, labelled "Wind". The direction follows it on the same line as an arrow
    /// and a compass point (<see cref="WeatherWindCompassText"/>); when only the direction is shown, this
    /// is the label alone.
    /// </summary>
    public string WeatherWindText => DashboardWeather is not { } w ? "" : Line(Strings.Get("Planner_Weather_Wind_Label"),
        Shows(WeatherField.WindSpeed) ? MeasurementUnits.FormatWindSpeed(w.WindSpeedMetresPerSecond, Settings.EffectiveMeasurementSystem) : null,
        Shows(WeatherField.WindGusts) ? Strings.Format("Planner_Weather_GustsCompact_Format",
            MeasurementUnits.FormatWindSpeed(w.WindGustMetresPerSecond, Settings.EffectiveMeasurementSystem)) : null) is { Length: > 0 } speed
        ? speed : HasWeatherWindDirection ? Strings.Get("Planner_Weather_Wind_Label") : "";

    /// <summary>The direction the wind comes from, when it is shown and known.</summary>
    public double? WeatherWindDirectionDegrees => DashboardWeather is { WindDirectionDegrees: double degrees } && Shows(WeatherField.WindDirection)
        && double.IsFinite(degrees) ? Angles.NormaliseDegrees(degrees) : null;
    public bool HasWeatherWindDirection => WeatherWindDirectionDegrees is not null;
    /// <summary>A separator between speed and direction, only when there are both.</summary>
    public bool HasWeatherWindSeparator => HasWeatherWindDirection && WeatherWindText != Strings.Get("Planner_Weather_Wind_Label");
    public string WeatherWindCompassText => WeatherWindDirectionDegrees is double degrees ? CompassPoint(degrees) : "";
    /// <summary>The exact direction, for the direction's accessible name and tooltip.</summary>
    public string WeatherWindDirectionDetail => WeatherWindDirectionDegrees is double degrees
        ? Strings.Format("Planner_Weather_WindDirection_Format", Value(degrees, "°")) : "";

    /// <summary>The nearest of the 16 compass points: N, NNE, NE … NNW.</summary>
    internal static string CompassPoint(double degrees) =>
        Strings.Get("Planner_Weather_CompassPoints").Split(',')[(int)Math.Round(Angles.NormaliseDegrees(degrees) / 22.5, MidpointRounding.AwayFromZero) % 16];

    public bool HasWeatherConditions => WeatherTemperatureText != "" || WeatherDewPointText != "" || WeatherWindText != "";

    // ---- Light --------------------------------------------------------------------------------------

    private TwilightEvents? DashboardTwilight => DashboardWeather is null ? null : Snapshot?.Astronomy.Sun.Twilight;

    public string WeatherSunText => DashboardWeather is null ? "" : Line(null,
        Shows(WeatherField.Sunrise) ? Strings.Format("Planner_Weather_Sunrise_Format", FormatTime(DashboardTwilight?.Sunrise)) : null,
        Shows(WeatherField.Sunset) ? Strings.Format("Planner_Weather_Sunset_Format", FormatTime(DashboardTwilight?.Sunset)) : null);

    /// <summary>Dark before the morning's astronomical dawn and after the evening's dusk: two times, not one window.</summary>
    public string WeatherDarknessText => DashboardWeather is not null && Shows(WeatherField.AstronomicalDarkness)
        ? Strings.Format("Planner_Weather_AstronomicalDarkness_Format", FormatTime(DashboardTwilight?.AstronomicalDawn), FormatTime(DashboardTwilight?.AstronomicalDusk)) : "";

    /// <summary>Civil, nautical and astronomical twilight, one per line: the detail behind the Light section's info button.</summary>
    public string WeatherTwilightText
    {
        get
        {
            if (DashboardWeather is null) return "";
            var t = DashboardTwilight;
            var lines = new List<string>();
            if (Shows(WeatherField.CivilTwilight)) lines.Add(Strings.Format("Planner_Weather_CivilTwilight_Format", FormatTime(t?.CivilDawn), FormatTime(t?.CivilDusk)));
            if (Shows(WeatherField.NauticalTwilight)) lines.Add(Strings.Format("Planner_Weather_NauticalTwilight_Format", FormatTime(t?.NauticalDawn), FormatTime(t?.NauticalDusk)));
            if (Shows(WeatherField.AstronomicalTwilight)) lines.Add(Strings.Format("Planner_Weather_AstronomicalTwilight_Format", FormatTime(t?.AstronomicalDawn), FormatTime(t?.AstronomicalDusk)));
            return string.Join('\n', lines);
        }
    }

    public bool HasWeatherTwilight => WeatherTwilightText != "";
    public bool HasWeatherLight => WeatherSunText != "" || WeatherDarknessText != "" || HasWeatherTwilight;

    // ---- Moon ---------------------------------------------------------------------------------------

    public string WeatherMoonText => DashboardWeather is null || Snapshot?.Astronomy.Moon is not { } moon ? "" : Line(null,
        Shows(WeatherField.MoonPhase) ? MoonPhaseName(moon.MoonPhaseAngleDegrees ?? 0) : null,
        Shows(WeatherField.MoonIllumination) && moon.MoonIlluminatedFraction is double fraction
            ? Strings.Format("Planner_Weather_MoonLitCompact_Format", fraction * 100) : null);

    public bool HasWeatherMoon => WeatherMoonText != "";
}
