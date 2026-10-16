using Noctaxis.Core.Localisation;
using Noctaxis.Core.Domain;

namespace Noctaxis.Desktop.ViewModels;

/// <summary>
/// What the Settings pages show beyond the settings themselves: the weather topics the user chooses between,
/// the terrain cache's capacity, the atlas's one install action, and whether the developer diagnostics exist.
/// </summary>
public partial class MainViewModel
{
    // ---- Developer diagnostics -------------------------------------------------------------------------

    /// <summary>
    /// Whether the terrain diagnostics exist in this run: a developer tool, not a setting, so they are off
    /// unless Noctaxis was started with NOCTAXIS_DIAGNOSTICS=1, in which case Settings shows its Diagnostics
    /// page again. Off, a diagnostics preference saved earlier is kept but does nothing.
    /// </summary>
    public bool IsDeveloperDiagnosticsEnabled
    {
        get => _isDeveloperDiagnosticsEnabled;
        set
        {
            if (!SetProperty(ref _isDeveloperDiagnosticsEnabled, value)) return;
            NotifyTerrainProperties();
        }
    }
    private bool _isDeveloperDiagnosticsEnabled = DeveloperDiagnosticsRequested;

    internal static bool DeveloperDiagnosticsRequested =>
        string.Equals(System.Environment.GetEnvironmentVariable("NOCTAXIS_DIAGNOSTICS"), "1", StringComparison.Ordinal);

    // ---- Weather topics --------------------------------------------------------------------------------

    /// <summary>
    /// The weather the Planner shows, in the dashboard's own groups. Each topic stands for the forecast or
    /// astronomy values behind it (Clouds is the total and the three layers), and is on while any of them is.
    /// The saved setting is still the list of values, so older settings load as they were.
    /// </summary>
    public System.Collections.ObjectModel.ObservableCollection<WeatherTopicGroupViewModel> WeatherTopicGroups { get; } = [];

    private static readonly IReadOnlyList<(string LabelKey, IReadOnlyList<(string LabelKey, WeatherField[] Fields)> Topics)> WeatherTopicDefinitions =
    [
        ("Settings_Weather_Group_Sky",
        [
            ("Settings_Weather_Topic_Clouds", [WeatherField.TotalCloudCover, WeatherField.LowCloudCover, WeatherField.MediumCloudCover, WeatherField.HighCloudCover]),
            ("Settings_Weather_Field_Visibility", [WeatherField.Visibility]),
            ("Settings_Weather_Topic_Precipitation", [WeatherField.PrecipitationProbability, WeatherField.PrecipitationAmount, WeatherField.PrecipitationType]),
        ]),
        ("Settings_Weather_Group_Conditions",
        [
            ("Settings_Weather_Field_Temperature", [WeatherField.Temperature]),
            ("Settings_Weather_Topic_HumidityDewPoint", [WeatherField.RelativeHumidity, WeatherField.DewPoint]),
            ("Settings_Weather_Topic_Wind", [WeatherField.WindSpeed, WeatherField.WindGusts, WeatherField.WindDirection]),
        ]),
        ("Settings_Weather_Group_Light",
        [
            ("Settings_Weather_Topic_SunriseSunset", [WeatherField.Sunrise, WeatherField.Sunset]),
            ("Settings_Weather_Topic_Twilight", [WeatherField.CivilTwilight, WeatherField.NauticalTwilight, WeatherField.AstronomicalTwilight]),
            ("Settings_Weather_Field_AstronomicalDarkness", [WeatherField.AstronomicalDarkness]),
        ]),
        ("Settings_Weather_Group_Moon",
        [
            ("Settings_Weather_Topic_MoonPhase", [WeatherField.MoonPhase, WeatherField.MoonIllumination]),
            ("Settings_Weather_Topic_MoonRiseSet", [WeatherField.Moonrise, WeatherField.Moonset]),
        ]),
    ];

    private void LoadWeatherTopics()
    {
        WeatherTopicGroups.Clear();
        foreach (var (groupKey, topics) in WeatherTopicDefinitions)
            WeatherTopicGroups.Add(new WeatherTopicGroupViewModel(Strings.Get(groupKey), topics.Select(topic =>
                new WeatherTopicOptionViewModel(Strings.Get(topic.LabelKey),
                    topic.Fields.Select(field => WeatherFieldOptions.Single(option => option.Field == field)).ToArray())).ToArray()));
    }

    // ---- Terrain cache capacity ------------------------------------------------------------------------

    /// <summary>How full the terrain cache is, 0–100: a downloading tile may take it briefly past its limit, shown as full.</summary>
    public double TerrainCacheUsedPercent { get => _terrainCacheUsedPercent; private set => SetProperty(ref _terrainCacheUsedPercent, value); }
    private double _terrainCacheUsedPercent;

    /// <summary>"1.00 GiB used of 2 GiB · 5,912 files".</summary>
    public string TerrainCacheCapacityText { get => _terrainCacheCapacityText; private set => SetProperty(ref _terrainCacheCapacityText, value); }
    private string _terrainCacheCapacityText = Strings.Get("Settings_Data_TerrainCache_NotLoaded");

    public string TerrainCacheUsedPercentText => Strings.Format("Common_PercentWhole_Format", Math.Floor(TerrainCacheUsedPercent));

    internal static double CachePercent(long bytes, long limitBytes) =>
        limitBytes <= 0 ? 0 : Math.Clamp(bytes * 100d / limitBytes, 0, 100);

    internal static string CacheSize(long bytes) => bytes >= 1024L * 1024 * 1024
        ? Strings.Format("Settings_Data_TerrainCache_GiB_Format", bytes / (1024d * 1024 * 1024))
        : Strings.Format("Settings_Data_TerrainCache_MiB_Format", bytes / (1024d * 1024));

    private void ShowTerrainCacheCapacity(long bytes, long limitBytes, int entries)
    {
        TerrainCacheUsedPercent = CachePercent(bytes, limitBytes);
        OnPropertyChanged(nameof(TerrainCacheUsedPercentText));
        TerrainCacheCapacityText = Strings.Format("Settings_Data_TerrainCache_Capacity_Format",
            CacheSize(bytes), Strings.Format("Settings_Data_TerrainCache_Limit_Format", limitBytes / (1024d * 1024 * 1024)), entries);
    }

    // ---- The light pollution atlas's one action --------------------------------------------------------

    /// <summary>
    /// The atlas's one install action, named for what it will do: Install when there is none, Repair when an
    /// installation is there but incomplete or unreadable, Reinstall over a good one.
    /// </summary>
    public string LightPollutionInstallLabel => Strings.Get(
        LightPollutionInstallation?.ReadError is not null ? "Settings_Data_LightPollution_Repair_Button"
        : LightPollutionInstallation?.IsAvailable == true ? "Settings_Data_LightPollution_Reinstall_Button"
        : "Settings_Data_LightPollution_Install_Button");

    partial void OnLightPollutionStatusChanged(string value) => OnPropertyChanged(nameof(LightPollutionInstallLabel));
}
