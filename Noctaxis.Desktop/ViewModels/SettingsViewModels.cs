using Noctaxis.Core.Localisation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctaxis.Core.Domain;

namespace Noctaxis.Desktop.ViewModels;

public partial class WeatherFieldOptionViewModel(WeatherField field, string label, bool isEnabled) : ObservableObject
{
    public WeatherField Field { get; } = field;
    public string Label { get; } = label;
    [ObservableProperty] private bool _isEnabled = isEnabled;
}

public sealed record WeatherFieldGroupViewModel(
    string Label,
    IReadOnlyList<WeatherFieldOptionViewModel> Fields);

public partial class CameraProfileEditorViewModel : ObservableObject
{
    private readonly Func<CameraProfileEditorViewModel, Task> _remove;

    public CameraProfileEditorViewModel(CameraProfile profile, Func<CameraProfileEditorViewModel, Task> remove)
    {
        Id = profile.Id;
        _displayName = profile.DisplayName;
        _sensorWidthMillimetres = profile.SensorWidthMillimetres;
        _sensorHeightMillimetres = profile.SensorHeightMillimetres;
        _manufacturer = profile.Manufacturer ?? string.Empty;
        _model = profile.Model ?? string.Empty;
        _remove = remove;
    }

    public string Id { get; }
    [ObservableProperty] private string _displayName;
    [ObservableProperty] private double _sensorWidthMillimetres;
    [ObservableProperty] private double _sensorHeightMillimetres;
    [ObservableProperty] private string _manufacturer;
    [ObservableProperty] private string _model;
    public CameraProfile Profile => new(Id, DisplayName, SensorWidthMillimetres,
        SensorHeightMillimetres, EmptyToNull(Manufacturer), EmptyToNull(Model));
    public bool IsValid => Profile.IsValid;
    public string? ValidationMessage => Profile.ValidationMessage;

    /// <summary>Asks, then removes this profile if confirmed (see MainViewModel).</summary>
    [RelayCommand]
    private Task Remove() => _remove(this);

    partial void OnDisplayNameChanged(string value) => NotifyValidation();
    partial void OnSensorWidthMillimetresChanged(double value) => NotifyValidation();
    partial void OnSensorHeightMillimetresChanged(double value) => NotifyValidation();

    private void NotifyValidation()
    {
        OnPropertyChanged(nameof(Profile));
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(ValidationMessage));
    }

    private static string? EmptyToNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public partial class LensProfileEditorViewModel : ObservableObject
{
    private readonly Func<LensProfileEditorViewModel, Task> _remove;

    public LensProfileEditorViewModel(LensProfile profile, Func<LensProfileEditorViewModel, Task> remove)
    {
        Id = profile.Id;
        _displayName = profile.DisplayName;
        _minimumFocalLengthMillimetres = profile.MinimumFocalLengthMillimetres;
        _maximumFocalLengthMillimetres = profile.MaximumFocalLengthMillimetres;
        _manufacturer = profile.Manufacturer ?? string.Empty;
        _model = profile.Model ?? string.Empty;
        _remove = remove;
    }

    public string Id { get; }
    [ObservableProperty] private string _displayName;
    [ObservableProperty] private double _minimumFocalLengthMillimetres;
    [ObservableProperty] private double _maximumFocalLengthMillimetres;
    [ObservableProperty] private string _manufacturer;
    [ObservableProperty] private string _model;
    public LensProfile Profile => new(Id, DisplayName, MinimumFocalLengthMillimetres,
        MaximumFocalLengthMillimetres, EmptyToNull(Manufacturer), EmptyToNull(Model));
    public bool IsValid => Profile.IsValid;
    public string? ValidationMessage => Profile.ValidationMessage;
    public string TypeText => Profile.IsValid ? Strings.Get(Profile.IsPrime ? "Settings_Equipment_PrimeLens" : "Settings_Equipment_ZoomLens") : string.Empty;

    /// <summary>Asks, then removes this profile if confirmed (see MainViewModel).</summary>
    [RelayCommand]
    private Task Remove() => _remove(this);

    partial void OnDisplayNameChanged(string value) => NotifyValidation();
    partial void OnMinimumFocalLengthMillimetresChanged(double value) => NotifyValidation();
    partial void OnMaximumFocalLengthMillimetresChanged(double value) => NotifyValidation();

    private void NotifyValidation()
    {
        OnPropertyChanged(nameof(Profile));
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(ValidationMessage));
        OnPropertyChanged(nameof(TypeText));
    }

    private static string? EmptyToNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
