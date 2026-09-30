using Noctaxis.Core.Localisation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctaxis.Core.Domain;
using Noctaxis.Core.LightPollution;

namespace Noctaxis.Desktop.ViewModels;

public partial class MainViewModel
{
    public LorenzInstallation? LightPollutionInstallation { get; private set; }
    private CancellationTokenSource? _lightPollutionCancellation;
    private bool _lightPollutionNotificationsAttached;
    private int _lightPollutionNotificationGeneration;
    public IReadOnlyList<Mapping.ILightPollutionColorMap> LightPollutionColorMapOptions => LightPollutionPaletteHelp.Options;
    public string LightPollutionColorMapDescription => LightPollutionPaletteHelp.Description(SettingsLightPollutionColorMap?.Id);
    /// <summary>
    /// Turbo is the one colour map whose levels collapse under colour-vision deficiencies. With a
    /// colour-vision mode active the user is offered Cividis; their choice is never changed for them.
    /// </summary>
    public string LightPollutionColourVisionAdvice => SelectedColourVisionValue != ColourVisionMode.None && SettingsLightPollutionColorMap?.Id == "turbo"
        ? Strings.Get("Settings_PlannerMap_Palette_TurboAdvice") : string.Empty;
    public bool HasLightPollutionColourVisionAdvice => LightPollutionColourVisionAdvice.Length > 0;
    [RelayCommand]
    private void UseColourVisionSafeLightPollutionPalette() => SettingsLightPollutionColorMap = Mapping.LightPollutionColorMaps.Resolve("cividis");
    [ObservableProperty] private string _lightPollutionStatus = Strings.Get("Settings_Data_LightPollution_NotInstalled");
    [ObservableProperty] private string _lightPollutionProgress = "";
    [ObservableProperty] private bool _isManagingLightPollution;
    public bool CanManageLightPollution => !IsManagingLightPollution && LightPollutionInstallation is not null;
    public LightPollutionPreferences LightPollutionMapPreferences => (Settings.LightPollution ?? new()).Normalised() with
    { PaletteId = Mapping.LightPollutionColorMaps.Resolve(Settings.LightPollution?.PaletteId).Id };
    public string LightPollutionSourceName => LorenzAtlas.Name;
    partial void OnIsManagingLightPollutionChanged(bool value) => OnPropertyChanged(nameof(CanManageLightPollution));
    public void PreviewPlannerLayers(bool visible, double opacityPercent)
    {
        var current = LightPollutionMapPreferences;
        var next = (current with { IsVisible = visible, Opacity = opacityPercent / 100 }).Normalised();
        if (current == next) return;
        Settings = Settings with { LightPollution = next };
        OnPropertyChanged(nameof(LightPollutionMapPreferences));
    }
    /// <summary>
    /// Persists layer preferences now. Throws <see cref="IOException"/> when they could not be saved
    /// (already logged and shown), so the Layers panel keeps them marked unsaved and shows its inline error.
    /// </summary>
    public async Task CommitPlannerLayersAsync()
    {
        if (!await FlushAsync()) throw new IOException("Layer preferences could not be saved.");
    }
    private async Task DetectLightPollutionAsync()
    {
        if (LightPollutionInstallation is null) return;
        if (!_lightPollutionNotificationsAttached)
        {
            LightPollutionInstallation.Changed += LightPollutionDataChanged;
            _lightPollutionNotificationsAttached = true;
        }
        await Task.Run(() => LightPollutionInstallation.DetectAsync());
        LightPollutionStatus = InstallationStatus(LightPollutionInstallation);
        OnPropertyChanged(nameof(LightPollutionMapPreferences));
    }
    [RelayCommand]
    private async Task InstallLightPollution()
    {
        if (!CanManageLightPollution) return;
        IsManagingLightPollution = true;
        _lightPollutionCancellation = new();
        var installCancellation = _lightPollutionCancellation;
        LightPollutionStatus = Strings.Get("Settings_Data_LightPollution_Installing");
        LightPollutionProgress = "";
        var progress = new Progress<LorenzInstallProgress>(p =>
        {
            if (ReferenceEquals(_lightPollutionCancellation, installCancellation))
                LightPollutionProgress = Strings.Format("Settings_Data_LightPollution_Progress_Format", p.CompletedTiles, p.TotalTiles, p.DownloadedBytes / 1048576d);
        });
        try
        {
            await Task.Run(() => LightPollutionInstallation!.InstallAsync(progress, _lightPollutionCancellation.Token));
            LightPollutionStatus = Strings.Get("Settings_Data_LightPollution_Installed");
        }
        catch (OperationCanceledException) { LightPollutionStatus = Strings.Get(LightPollutionInstallation!.IsAvailable ? "Settings_Data_LightPollution_CancelledRetained" : "Settings_Data_LightPollution_Cancelled"); }
        catch (Exception ex) { LightPollutionStatus = Strings.Format("Settings_Data_LightPollution_InstallError_Format", ex.Message); }
        finally { _lightPollutionCancellation.Dispose(); _lightPollutionCancellation = null; IsManagingLightPollution = false; }
    }
    [RelayCommand] private void CancelLightPollutionInstall() => _lightPollutionCancellation?.Cancel();
    [RelayCommand]
    private async Task RemoveLightPollution()
    {
        if (!CanManageLightPollution) return;
        if (!await _dialogs.ConfirmDestructiveAsync(new DestructiveConfirmation(Strings.Get("Dialogs_RemoveAtlas_Title"),
                Strings.Get("Dialogs_RemoveAtlas_Message"),
                Strings.Get("Dialogs_RemoveAtlas_Confirm"))))
            return;
        IsManagingLightPollution = true;
        try { await Task.Run(() => LightPollutionInstallation!.RemoveAsync()); LightPollutionStatus = Strings.Get("Settings_Data_LightPollution_NotInstalled"); LightPollutionProgress = ""; }
        catch (Exception ex) { LightPollutionStatus = Strings.Format("Settings_Data_LightPollution_RemoveError_Format", ex.Message); }
        finally { IsManagingLightPollution = false; }
    }
    [RelayCommand]
    private void OpenLightPollutionSource()
    {
        if (!_externalUriLauncher.TryOpen(new(LorenzAtlas.InformationUrl), out var error)) LightPollutionStatus = Strings.Format("Settings_Data_LightPollution_OpenSourceFailed_Format", error);
    }

    private static string InstallationStatus(LorenzInstallation installation) => installation.ReadError
        ?? Strings.Get(installation.IsAvailable ? "Settings_Data_LightPollution_Installed" : "Settings_Data_LightPollution_NotInstalled");
    private void LightPollutionDataChanged(object? sender, EventArgs args)
    {
        var generation = _lightPollutionNotificationGeneration;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_lightPollutionNotificationsAttached && generation == _lightPollutionNotificationGeneration && !IsManagingLightPollution)
                LightPollutionStatus = InstallationStatus(LightPollutionInstallation!);
        });
    }
    public void CancelEnvironmentalInstall()
    {
        _lightPollutionCancellation?.Cancel();
        _lightPollutionNotificationGeneration++;
        if (LightPollutionInstallation is not null) LightPollutionInstallation.Changed -= LightPollutionDataChanged;
        _lightPollutionNotificationsAttached = false;
    }
}
