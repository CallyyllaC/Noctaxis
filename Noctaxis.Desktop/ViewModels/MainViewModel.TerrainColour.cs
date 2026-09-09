using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Noctaxis.Core.Domain;

namespace Noctaxis.Desktop.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty] private double _settingsTerrainTintStrengthPercent = 40;

    partial void OnSettingsTerrainTintStrengthPercentChanged(double value) =>
        OnPropertyChanged(nameof(CameraFramingMapSettings));

    [ObservableProperty]
    private Color _settingsTerrainObstructionColour = Color.Parse(CameraFramingSettings.DefaultTerrainObstructionColour);

    public string SettingsTerrainObstructionColourHex =>
        $"#{SettingsTerrainObstructionColour.R:X2}{SettingsTerrainObstructionColour.G:X2}{SettingsTerrainObstructionColour.B:X2}";

    partial void OnSettingsTerrainObstructionColourChanged(Color value)
    {
        OnPropertyChanged(nameof(SettingsTerrainObstructionColourHex));
        OnPropertyChanged(nameof(CameraFramingMapSettings));
    }
}
