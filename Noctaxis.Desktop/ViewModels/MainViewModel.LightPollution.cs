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
    [ObservableProperty] private Mapping.ILightPollutionColorMap _settingsLightPollutionColorMap = Mapping.LightPollutionColorMaps.Grayscale;
    public IReadOnlyList<Mapping.ILightPollutionColorMap> LightPollutionColorMapOptions => LightPollutionPaletteHelp.Options;
    public string LightPollutionColorMapDescription => LightPollutionPaletteHelp.Description(SettingsLightPollutionColorMap?.Id);
    partial void OnSettingsLightPollutionColorMapChanged(Mapping.ILightPollutionColorMap value) => OnPropertyChanged(nameof(LightPollutionColorMapDescription));
    [ObservableProperty] private string _lightPollutionStatus = "Not installed";
    [ObservableProperty] private string _lightPollutionProgress = "";
    [ObservableProperty] private bool _isManagingLightPollution;
    public bool CanManageLightPollution => !IsManagingLightPollution && LightPollutionInstallation is not null;
    public LightPollutionPreferences LightPollutionMapPreferences => (Settings.LightPollution ?? new()).Normalised() with
    { PaletteId = Mapping.LightPollutionColorMaps.Resolve(Settings.LightPollution?.PaletteId).Id };
    public string LightPollutionSourceName => LorenzAtlas.Name;
    partial void OnIsManagingLightPollutionChanged(bool value) => OnPropertyChanged(nameof(CanManageLightPollution));
    private void LoadLightPollutionEditor()
    {
        SettingsLightPollutionColorMap = Mapping.LightPollutionColorMaps.Resolve(LightPollutionMapPreferences.PaletteId);
    }
    public void PreviewPlannerLayers(bool visible, double opacityPercent)
    {
        var current = LightPollutionMapPreferences;
        var next = (current with { IsVisible = visible, Opacity = opacityPercent / 100 }).Normalised();
        if (current == next) return;
        Settings = Settings with { LightPollution = next };
        OnPropertyChanged(nameof(LightPollutionMapPreferences));
    }
    public Task CommitPlannerLayersAsync() => PersistAsync(CancellationToken.None);
    private async Task DetectLightPollutionAsync()
    {
        if (LightPollutionInstallation is null) return;
        if (!_lightPollutionNotificationsAttached)
        {
            LightPollutionInstallation.Changed += LightPollutionDataChanged;
            _lightPollutionNotificationsAttached = true;
        }
        await Task.Run(() => LightPollutionInstallation.DetectAsync());
        LightPollutionStatus = LightPollutionInstallation.ReadError ?? (LightPollutionInstallation.IsAvailable ? "Installed" : "Not installed");
        OnPropertyChanged(nameof(LightPollutionMapPreferences));
    }
    [RelayCommand]
    private async Task InstallLightPollution()
    {
        if (!CanManageLightPollution) return;
        IsManagingLightPollution = true;
        _lightPollutionCancellation = new();
        var installCancellation = _lightPollutionCancellation;
        LightPollutionStatus = "Installing…";
        LightPollutionProgress = "";
        var progress = new Progress<LorenzInstallProgress>(p =>
        {
            if (ReferenceEquals(_lightPollutionCancellation, installCancellation))
                LightPollutionProgress = $"{p.CompletedTiles:N0} / {p.TotalTiles:N0} files · {p.DownloadedBytes / 1048576d:F1} MiB downloaded";
        });
        try
        {
            await Task.Run(() => LightPollutionInstallation!.InstallAsync(progress, _lightPollutionCancellation.Token));
            LightPollutionStatus = "Installed";
        }
        catch (OperationCanceledException) { LightPollutionStatus = LightPollutionInstallation!.IsAvailable ? "Cancelled · previous installation retained" : "Installation cancelled"; }
        catch (Exception ex) { LightPollutionStatus = $"Installation error: {ex.Message}"; }
        finally { _lightPollutionCancellation.Dispose(); _lightPollutionCancellation = null; IsManagingLightPollution = false; }
    }
    [RelayCommand] private void CancelLightPollutionInstall() => _lightPollutionCancellation?.Cancel();
    [RelayCommand]
    private async Task RemoveLightPollution()
    {
        if (!CanManageLightPollution) return;
        IsManagingLightPollution = true;
        try { await Task.Run(() => LightPollutionInstallation!.RemoveAsync()); LightPollutionStatus = "Not installed"; LightPollutionProgress = ""; }
        catch (Exception ex) { LightPollutionStatus = $"Removal error: {ex.Message}"; }
        finally { IsManagingLightPollution = false; }
    }
    [RelayCommand]
    private void OpenLightPollutionSource()
    {
        if (!_externalUriLauncher.TryOpen(new(LorenzAtlas.InformationUrl), out var error)) LightPollutionStatus = $"Could not open source: {error}";
    }
    private void LightPollutionDataChanged(object? sender, EventArgs args)
    {
        var generation = _lightPollutionNotificationGeneration;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_lightPollutionNotificationsAttached && generation == _lightPollutionNotificationGeneration && !IsManagingLightPollution)
                LightPollutionStatus = LightPollutionInstallation!.ReadError ?? (LightPollutionInstallation.IsAvailable ? "Installed" : "Not installed");
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
