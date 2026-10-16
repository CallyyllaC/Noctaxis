using Noctaxis.Core.Localisation;
using CommunityToolkit.Mvvm.ComponentModel;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Themes;

namespace Noctaxis.Desktop.ViewModels;

public sealed record AppearanceOption(string Id, string Label, bool IsSupporter, bool IsAvailable)
{
    /// <summary>What kind of theme this is, never empty: Standard, Supporter, or Locked · Supporter.</summary>
    public string AvailabilityLabel => Strings.Get(!IsSupporter ? "Settings_Appearance_Theme_Standard"
        : IsAvailable ? "Settings_Appearance_Theme_Supporter" : "Settings_Appearance_Theme_LockedSupporter");

    /// <summary>
    /// A theme's name: its translatable name where it has one (key
    /// <c>Settings_Appearance_ThemeName_&lt;id with . and - as _&gt;</c>), otherwise the name it is defined
    /// with, which is a proper name ("Noctaxis", "Foxfire") and stays as it is.
    /// </summary>
    public static string ThemeName(string id, string definedName) =>
        Strings.TryGet("Settings_Appearance_ThemeName_" + id.Replace('.', '_').Replace('-', '_'), out var name) ? name : definedName;
}
public sealed record ColourVisionOption(ColourVisionMode Mode, string Label);

public partial class MainViewModel
{
    private bool _loadingAppearance;
    private ThemeService? _themeService;
    public void AttachThemeService(ThemeService service) { _themeService = service; LoadAppearance(); }
    public IReadOnlyList<AppearanceOption> ThemeOptions =>
        AppearanceCatalogue.Themes.Select(t => new AppearanceOption(t.Id, AppearanceOption.ThemeName(t.Id, t.DisplayName), t.IsSupporter, AppearanceCatalogue.IsAvailable(t))).ToArray();
    private ThemeCatalogue AppearanceCatalogue => _themeService?.Catalogue ?? new ThemeCatalogue();
    public IReadOnlyList<ColourVisionOption> ColourVisionOptions { get; } =
    [new(ColourVisionMode.None, Strings.Get("Settings_Appearance_None_Option")), new(ColourVisionMode.Protanopia, Strings.Get("Settings_Appearance_Protanopia_Option")),
     new(ColourVisionMode.Deuteranopia, Strings.Get("Settings_Appearance_Deuteranopia_Option")), new(ColourVisionMode.Tritanopia, Strings.Get("Settings_Appearance_Tritanopia_Option"))];
    public IReadOnlyList<AppearanceMode> AppearanceModeOptions { get; } = Enum.GetValues<AppearanceMode>();
    [ObservableProperty] private AppearanceMode _selectedAppearanceMode;
    [ObservableProperty] private AppearanceOption? _selectedTheme;
    [ObservableProperty] private ColourVisionOption? _selectedColourVision;
    public ColourVisionMode SelectedColourVisionValue
    {
        get => SelectedColourVision?.Mode ?? ColourVisionMode.None;
        set => SelectedColourVision = ColourVisionOptions.Single(option => option.Mode == value);
    }
    [ObservableProperty] private double _textSizePercent = 100;

    private void LoadAppearance()
    {
        _loadingAppearance = true;
        try
        {
            var p = (Settings.Appearance ?? new()).Normalised();
            SelectedTheme = ThemeOptions.FirstOrDefault(t => t.Id == p.ThemeFamilyId && t.IsAvailable) ?? ThemeOptions.Single(t => t.Id == ThemeCatalogue.DefaultId);
            SelectedAppearanceMode = p.AppearanceMode;
            SelectedColourVision = ColourVisionOptions.Single(t => t.Mode == p.ColourVisionMode);
            TextSizePercent = p.TextScale * 100;
            _themeService?.Apply(p);
        }
        finally { _loadingAppearance = false; }
    }
    partial void OnSelectedAppearanceModeChanged(AppearanceMode value) => ChangeAppearance();
    partial void OnSelectedThemeChanged(AppearanceOption? value)
    {
        if (value is { IsAvailable: false }) { LoadAppearance(); return; }
        ChangeAppearance();
    }
    partial void OnSelectedColourVisionChanged(ColourVisionOption? value)
    {
        OnPropertyChanged(nameof(SelectedColourVisionValue));
        OnPropertyChanged(nameof(LightPollutionColourVisionAdvice));
        OnPropertyChanged(nameof(HasLightPollutionColourVisionAdvice));
        ChangeAppearance();
    }
    partial void OnTextSizePercentChanged(double value) => ChangeAppearance();
    private void ChangeAppearance()
    {
        if (_loadingAppearance || SelectedTheme is null || SelectedColourVision is null) return;
        SetAppearance(new(SelectedTheme.Id, SelectedColourVision.Mode, TextSizePercent / 100, SelectedAppearanceMode));
        // Text size is a slider: coalesce its rapid changes rather than writing on every step.
        RequestSave();
    }
    public async Task ApplyAppearanceAsync(AppearancePreferences preferences)
    {
        SetAppearance(preferences);
        await FlushAsync();
    }
    private void SetAppearance(AppearancePreferences preferences)
    {
        // Deliberately bypass ApplySettingsAsync: it owns calculation and equipment changes.
        Settings = Settings with { Appearance = preferences.Normalised() };
        LoadAppearance();
    }
}
