using CommunityToolkit.Mvvm.ComponentModel;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Themes;

namespace Noctaxis.Desktop.ViewModels;

public sealed record AppearanceOption(string Id, string Label, bool IsSupporter, bool IsAvailable)
{
    public string AvailabilityLabel => IsSupporter ? (IsAvailable ? "Supporter" : "Locked · Supporter") : string.Empty;
}
public sealed record ColourVisionOption(ColourVisionMode Mode, string Label);

public partial class MainViewModel
{
    private bool _loadingAppearance;
    private ThemeService? _themeService;
    public void AttachThemeService(ThemeService service) { _themeService = service; LoadAppearance(); }
    public IReadOnlyList<AppearanceOption> ThemeOptions =>
        AppearanceCatalogue.Themes.Select(t => new AppearanceOption(t.Id, t.DisplayName, t.IsSupporter, AppearanceCatalogue.IsAvailable(t))).ToArray();
    private ThemeCatalogue AppearanceCatalogue => _themeService?.Catalogue ?? new ThemeCatalogue();
    public IReadOnlyList<ColourVisionOption> ColourVisionOptions { get; } =
    [new(ColourVisionMode.None, "None"), new(ColourVisionMode.Protanopia, "Protanopia-friendly"),
     new(ColourVisionMode.Deuteranopia, "Deuteranopia-friendly"), new(ColourVisionMode.Tritanopia, "Tritanopia-friendly")];
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
        ChangeAppearance();
    }
    partial void OnTextSizePercentChanged(double value) => ChangeAppearance();
    private async void ChangeAppearance()
    {
        if (_loadingAppearance || SelectedTheme is null || SelectedColourVision is null) return;
        try { await ApplyAppearanceAsync(new(SelectedTheme.Id, SelectedColourVision.Mode, TextSizePercent / 100, SelectedAppearanceMode)); }
        catch (Exception ex) { StatusMessage = $"Could not save appearance: {ex.Message}"; }
    }
    public async Task ApplyAppearanceAsync(AppearancePreferences preferences)
    {
        Settings = Settings with { Appearance = preferences.Normalised() };
        LoadAppearance();
        // Deliberately bypass ApplySettingsAsync: it owns calculation and equipment changes.
        await PersistAsync(CancellationToken.None);
    }
}
